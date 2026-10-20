using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Tenant ayarları için kayıt deposu ("core.tenant-setting"). Alanlar: key (oluşturulduktan sonra
/// değişmez), value. Boru hattının ilk gerçek kullanıcısıdır; modül entity'leri aynı kalıbı izler.
/// </summary>
internal sealed class TenantSettingRecordStore(ModulixDbContext db) : IRecordStore
{
    public const string Key = "core.tenant-setting";

    private const string KeyField = "key";
    private const string ValueField = "value";

    public string EntityKey => Key;

    public string FeatureKey => "core.settings";

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

        if (operation == RecordOperation.Create)
        {
            if (ReadString(values, KeyField) is not { } key)
            {
                errors.Add(new RecordFieldError(KeyField, "Anahtar zorunludur."));
            }
            else
            {
                try
                {
                    TenantSetting.NormalizeKey(key);
                }
                catch (ArgumentException exception)
                {
                    errors.Add(new RecordFieldError(KeyField, exception.Message.Split(" (Parameter")[0]));
                }
            }
        }

        if (ReadString(values, ValueField) is not { } value)
        {
            errors.Add(new RecordFieldError(ValueField, "Değer zorunludur ve metin olmalıdır."));
        }
        else if (value.Length > TenantSetting.ValueMaxLength)
        {
            errors.Add(new RecordFieldError(ValueField, $"Değer en fazla {TenantSetting.ValueMaxLength} karakter olabilir."));
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

    public Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var setting = new TenantSetting(ReadString(values, KeyField)!, ReadString(values, ValueField)!);
        db.TenantSettings.Add(setting);
        return Task.FromResult(ToData(setting));
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var setting = await db.TenantSettings.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (setting is null)
        {
            return null;
        }

        EnsureVersion(setting, expectedVersion);
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
