using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customization;

/// <summary>
/// Form düzenleri (ADR-0021). Etkin düzen = modülün tanımı + firmanın satırı (varsa) + özel alanlar. Boru hattına firmanın
/// eklediği zorunlu alanları verir (<see cref="IFormLayoutProvider"/>); kayıt türü başına istekte bir kez okunur.
/// Satır silinmez: "varsayılana dön" JSON'u boşaltır ve sürümü ilerletir.
/// </summary>
internal sealed class FormLayoutService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ICustomFieldProvider customFields,
    IAuditWriter audit) : IFormLayoutService, IFormLayoutProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<(Guid TenantId, string EntityKey), IReadOnlySet<string>> requiredCache = [];

    public async Task<IReadOnlySet<string>> GetRequiredFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)
    {
        if (requiredCache.TryGetValue((tenant.TenantId, entityKey), out var cached))
        {
            return cached;
        }

        IReadOnlySet<string> required = new HashSet<string>();
        if (catalog.FindForm(entityKey) is { } found)
        {
            var row = await db.FormLayouts.AsNoTracking().SingleOrDefaultAsync(l => l.EntityKey == entityKey, cancellationToken);
            if (row?.LayoutJson is not null)
            {
                var custom = await customFields.GetFieldsAsync(tenant, entityKey, cancellationToken);
                required = FormLayoutRules.ExtraRequired(FormLayoutRules.Resolve(found.Form, custom, Parse(row.LayoutJson), row.Version));
            }
        }

        requiredCache[(tenant.TenantId, entityKey)] = required;
        return required;
    }

    public async Task<IReadOnlyList<FormLayoutInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        var rows = await db.FormLayouts.AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<FormLayoutInfo>();
        foreach (var (moduleKey, form) in catalog.Forms.Where(f => catalog.IsEnabled(f.ModuleKey, enabled)))
        {
            var custom = await customFields.GetFieldsAsync(tenant, form.EntityKey, cancellationToken);
            result.Add(Resolve(form, custom, rows.FirstOrDefault(r => r.EntityKey == form.EntityKey)));
        }

        return result;
    }

    public async Task<FormLayoutInfo?> FindAsync(string entityKey, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (await FindEnabledAsync(entityKey, tenant, cancellationToken) is not { } form)
        {
            return null;
        }

        var row = await db.FormLayouts.AsNoTracking().SingleOrDefaultAsync(l => l.EntityKey == entityKey, cancellationToken);
        return Resolve(form, await customFields.GetFieldsAsync(tenant, entityKey, cancellationToken), row);
    }

    public Task<RecordResult<FormLayoutInfo>> SaveAsync(string entityKey, FormLayoutInput input, long version, CancellationToken cancellationToken) =>
        WriteAsync(entityKey, input, version, cancellationToken);

    public Task<RecordResult<FormLayoutInfo>> ResetAsync(string entityKey, long version, CancellationToken cancellationToken) =>
        WriteAsync(entityKey, null, version, cancellationToken);

    private async Task<RecordResult<FormLayoutInfo>> WriteAsync(string entityKey, FormLayoutInput? input, long version, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (await FindEnabledAsync(entityKey, tenant, cancellationToken) is not { } form)
        {
            return RecordResult<FormLayoutInfo>.Fail(RecordErrorCode.NotFound, "Form bulunamadı.");
        }

        var custom = await customFields.GetFieldsAsync(tenant, entityKey, cancellationToken);
        if (input is not null)
        {
            var errors = FormLayoutRules.Validate(form, custom, input);
            if (errors.Count > 0)
            {
                return RecordResult<FormLayoutInfo>.Fail(RecordErrorCode.ValidationFailed, "Form düzeni geçersiz.", errors);
            }
        }

        var row = await db.FormLayouts.SingleOrDefaultAsync(l => l.EntityKey == entityKey, cancellationToken);
        if ((row?.Version ?? 0) != version)
        {
            return Conflict(row?.Version ?? 0, version);
        }

        var before = Resolve(form, custom, row);
        var created = row is null;
        if (created && input is null)
        {
            return RecordResult<FormLayoutInfo>.Ok(before);
        }

        if (row is null)
        {
            row = new TenantFormLayout(entityKey);
            db.FormLayouts.Add(row);
        }

        row.Set(input is null ? null : JsonSerializer.Serialize(input, Json), tenant.UserId);
        var after = Resolve(form, custom, row);
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.FormLayout,
            row.Id,
            created ? AuditAction.Created : AuditAction.Updated,
            RecordDiff.Compute(created ? null : Snapshot(before), Snapshot(after)),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı yazım: aynı sürümden başlayan ikinci kayıt (eşzamanlılık belirteci ya da tekil anahtar) çakışmadır.
            return Conflict(version + 1, version);
        }

        requiredCache.Remove((tenant.TenantId, entityKey));
        return RecordResult<FormLayoutInfo>.Ok(after);
    }

    private static FormLayoutInfo Resolve(FormDefinition form, IReadOnlyList<CustomFieldInfo> custom, TenantFormLayout? row) =>
        FormLayoutRules.Resolve(form, custom, row?.LayoutJson is null ? null : Parse(row.LayoutJson), row?.Version ?? 0);

    private static FormLayoutInput? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FormLayoutInput>(json, Json);
        }
        catch (JsonException)
        {
            // Bozuk satır düzeni kilitlemez: tanım geçerli olur; bir sonraki kayıt satırı düzeltir.
            return null;
        }
    }

    private async Task<FormDefinition?> FindEnabledAsync(string entityKey, TenantContext tenant, CancellationToken cancellationToken)
    {
        if (catalog.FindForm(entityKey) is not { } found)
        {
            return null;
        }

        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        return catalog.IsEnabled(found.ModuleKey, enabled) ? found.Form : null;
    }

    private static JsonObject Snapshot(FormLayoutInfo info) => new()
    {
        ["form"] = info.DisplayName,
        ["tabs"] = string.Join(", ", info.Tabs.Select(t => t.Label)),
        ["sections"] = string.Join(", ", info.Sections.Select(s => s.Label)),
        ["required"] = string.Join(", ", info.Fields.Where(f => f.Required && !f.DefaultRequired).Select(f => f.Label)),
        ["hidden"] = string.Join(", ", info.Fields.Where(f => !f.Visible).Select(f => f.Label)),
        ["source"] = info.Customized ? "Firma düzeni" : "Varsayılan",
    };

    private static RecordResult<FormLayoutInfo> Conflict(long current, long expected) => RecordResult<FormLayoutInfo>.Fail(
        RecordErrorCode.Conflict,
        $"Düzen siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {current}). Güncel halini alıp tekrar deneyin.");

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan form düzeni okunamaz.");
}
