using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Tenant ayarları için kayıt deposu ("core.tenant-setting"). Alanlar: key (oluşturulduktan sonra
/// değişmez), value. Boru hattının ilk gerçek kullanıcısıdır; modül entity'leri aynı kalıbı izler.
/// Yalnızca modüllerin tanımladığı ayarlar (Y6) ve tanıma uyan değerler yazılır; modülü kapalı ayar yazılamaz.
/// </summary>
internal sealed class TenantSettingRecordStore(
    ModulixDbContext db,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ITenantContextAccessor tenantAccessor,
    ICompanyContextAccessor companyAccessor) : IRecordStore
{
    public const string Key = "core.tenant-setting";

    private const string KeyField = "key";
    private const string ValueField = "value";

    public string EntityKey => Key;

    public string FeatureKey => "core.settings";

    /// <summary>Ayarları her üye okur (ekranlar dil, para birimi gibi değerlere bakar); yazmak için izin gerekir.</summary>
    public string? PermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Read ? null : CoreModule.Permissions.ManageSettings;

    public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = new List<RecordFieldError>();
        var allowed = operation == RecordOperation.Create ? new[] { KeyField, ValueField } : new[] { ValueField };

        foreach (var (field, _) in values)
        {
            if (!allowed.Contains(field, StringComparer.Ordinal))
            {
                errors.Add(new RecordFieldError(field,
                    field == KeyField ? "Ayar anahtarı oluşturulduktan sonra değiştirilemez." : "Bilinmeyen alan."));
            }
        }

        SettingDefinition? definition = null;
        if (operation == RecordOperation.Create)
        {
            if (ReadString(values, KeyField) is not { } key)
            {
                errors.Add(new RecordFieldError(KeyField, "Anahtar zorunludur."));
            }
            else if (catalog.FindSetting(key) is { } found)
            {
                definition = found.Definition;
            }
            else
            {
                errors.Add(new RecordFieldError(KeyField, "Tanımlı bir ayar değil."));
            }
        }

        // Güncellemede anahtar kayıttadır; değer UpdateAsync'te tanıma göre denetlenir.
        if (ReadString(values, ValueField) is not { } value)
        {
            errors.Add(new RecordFieldError(ValueField, "Değer zorunludur ve metin olmalıdır."));
        }
        else if (definition is not null && SettingRules.Validate(definition, value) is { } error)
        {
            errors.Add(new RecordFieldError(ValueField, error));
        }

        return errors;
    }

    public async Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return setting is null ? null : ToData(setting);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var source = db.TenantSettings.AsNoTracking();
        var total = await source.CountAsync(cancellationToken);
        var items = await source
            .OrderBy(s => s.Key)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new RecordPage(items.Select(ToData).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var key = ReadString(values, KeyField)!;
        var value = ReadString(values, ValueField)!;
        await EnsureWritableAsync(key, value, cancellationToken);
        var setting = new TenantSetting(key, value, ResolveCompanyId(key));
        db.TenantSettings.Add(setting);
        return ToData(setting);
    }

    // Şirket başına ayarın değeri aktif şirkete yazılır (ADR-0008, adım 5); tenant geneli ayarda boştur. Şirket başına
    // ayar için aktif şirket yoksa yazılamaz.
    private Guid? ResolveCompanyId(string key)
    {
        if (catalog.FindSetting(key)?.Definition.PerCompany != true)
        {
            return null;
        }

        return companyAccessor.Current?.ActiveCompanyId
            ?? throw new RecordValidationException(
                [new RecordFieldError(KeyField, "Bu ayar şirket başına tutulur; değer yazmak için önce bir aktif şirket seçin.")]);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (setting is null)
        {
            return null;
        }

        EnsureVersion(setting, expectedVersion);
        await EnsureWritableAsync(setting.Key, ReadString(values, ValueField)!, cancellationToken);
        setting.SetValue(ReadString(values, ValueField)!);
        return ToData(setting);
    }

    public async Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (setting is null)
        {
            return false;
        }

        EnsureVersion(setting, expectedVersion);
        db.TenantSettings.Remove(setting);
        return true;
    }

    // Tanımı kaldırılmış ayar veya modülü kapalı ayar yazılamaz; değer tanıma uymalıdır.
    private async Task EnsureWritableAsync(string key, string value, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan ayar yazılamaz.");
        if (catalog.FindSetting(key) is not { } found
            || !catalog.IsEnabled(found.ModuleKey, await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken)))
        {
            throw new RecordValidationException([new RecordFieldError(KeyField, "Bu ayar bu firmada kullanılamıyor.")]);
        }

        if (SettingRules.Validate(found.Definition, value) is { } error)
        {
            throw new RecordValidationException([new RecordFieldError(ValueField, error)]);
        }
        // Y7b: zorunluluğu açan kendisi iki adımlı doğrulamasız kalıp firmadan dışarıda kalmasın.
        if (key == CoreModule.SettingKeys.RequireTwoFactor && value == CoreModule.SettingValues.Yes
            && !await db.Users.AnyAsync(u => u.Id == tenant.UserId && u.TwoFactorEnabled, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError(ValueField,
                "Zorunlu kılmadan önce kendi iki adımlı doğrulamanızı açın (Hesabım → İki adımlı doğrulama).")]);
        }
    }

    private static void EnsureVersion(TenantSetting setting, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != setting.Version)
        {
            throw new RecordConflictException(
                $"Kayıt siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {setting.Version}). Güncel halini alıp tekrar deneyin.");
        }
    }

    private static RecordData ToData(TenantSetting setting) =>
        new(Key, setting.Id, setting.Version, new JsonObject
        {
            [KeyField] = setting.Key,
            [ValueField] = setting.Value,
        });

    private static string? ReadString(JsonObject values, string field) =>
        values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
