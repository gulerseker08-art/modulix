using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Audit;
using Modulix.Core.Imports;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Imports;

/// <summary><see cref="IImportDesignStore"/>: tasarım satırını okur. Sorgu aktif tenant'ta çalışır (sorgu süzgeci + RLS).</summary>
internal sealed class ImportDesignStore(ModulixDbContext db) : IImportDesignStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ImportDesignInput> GetAsync(string entityKey, CancellationToken cancellationToken)
    {
        var json = await db.ImportDesigns.AsNoTracking().Where(d => d.EntityKey == entityKey).Select(d => d.DesignJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? ImportDesignInput.Default : JsonSerializer.Deserialize<ImportDesignInput>(json, Json) ?? ImportDesignInput.Default;
    }
}

/// <summary><see cref="IImportDesignService"/>: yönetim işlemi olduğu için her değişiklik denetim kaydına yazılır.</summary>
internal sealed class ImportDesignService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ImportSchema schema,
    IAuditWriter audit) : IImportDesignService
{
    public async Task<IReadOnlyList<ImportDesignInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        var rows = await db.ImportDesigns.AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<ImportDesignInfo>();
        foreach (var (moduleKey, definition) in catalog.ImportDefinitions.Where(i => catalog.IsEnabled(i.ModuleKey, enabled)))
        {
            var fields = await schema.AllFieldsAsync(tenant, definition, cancellationToken);
            result.Add(ToInfo(definition, fields, rows.SingleOrDefault(r => r.EntityKey == definition.EntityKey)));
        }

        return result;
    }

    public async Task<RecordResult<ImportDesignInfo>> SaveAsync(string entityKey, ImportDesignInput input, long version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = RequireTenant();
        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        if (catalog.FindImport(entityKey) is not { } found || !catalog.IsEnabled(found.ModuleKey, enabled))
        {
            return RecordResult<ImportDesignInfo>.Fail(RecordErrorCode.NotFound, "Kayıt türü bulunamadı.");
        }

        var fields = await schema.AllFieldsAsync(tenant, found.Definition, cancellationToken);
        var errors = ImportDesignRules.Validate(fields, input);
        if (errors.Count > 0)
        {
            return RecordResult<ImportDesignInfo>.Fail(RecordErrorCode.ValidationFailed, "İçe aktarma tasarımı geçersiz.", errors);
        }

        var row = await db.ImportDesigns.SingleOrDefaultAsync(d => d.EntityKey == entityKey, cancellationToken);
        if ((row?.Version ?? 0) != version)
        {
            return Conflict();
        }

        var before = row is null ? null : Snapshot(Parse(row));
        var created = row is null;
        if (row is null)
        {
            row = new TenantImportDesign(entityKey);
            db.ImportDesigns.Add(row);
        }

        row.Set(JsonSerializer.Serialize(input, ImportDesignStore.Json), tenant.UserId);
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.ImportDesign,
            row.Id,
            created ? AuditAction.Created : AuditAction.Updated,
            RecordDiff.Compute(before, Snapshot(input, entityKey)),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Aynı sürümden başlayan ikinci kayıt (eşzamanlılık belirteci ya da tekil anahtar) çakışmadır.
            return Conflict();
        }

        return RecordResult<ImportDesignInfo>.Ok(ToInfo(found.Definition, fields, row));
    }

    private static ImportDesignInfo ToInfo(ImportDefinition definition, IReadOnlyList<ImportField> fields, TenantImportDesign? row)
    {
        var design = row is null ? ImportDesignInput.Default : Parse(row);
        return new ImportDesignInfo(
            definition.EntityKey,
            definition.DisplayName,
            design.Enabled,
            design.RunAutomation,
            [.. fields.Select(f => new ImportDesignField(f.Key, f.Label, f.Required || !design.Excluded.Contains(f.Key), f.Required || design.Required.Contains(f.Key), f.Required) { Aliases = design.AliasesOf(f.Key) })],
            row?.Version ?? 0);
    }

    private static ImportDesignInput Parse(TenantImportDesign row) =>
        JsonSerializer.Deserialize<ImportDesignInput>(row.DesignJson, ImportDesignStore.Json) ?? ImportDesignInput.Default;

    private static JsonObject Snapshot(ImportDesignInput design, string? entityKey = null) => new()
    {
        ["entityKey"] = entityKey,
        ["enabled"] = design.Enabled,
        ["runAutomation"] = design.RunAutomation,
        ["excluded"] = string.Join(", ", design.Excluded.Order(StringComparer.Ordinal)),
        ["required"] = string.Join(", ", design.Required.Order(StringComparer.Ordinal)),
        ["aliases"] = string.Join("; ", (design.Aliases ?? new Dictionary<string, IReadOnlyList<string>>()).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}: {string.Join(", ", a.Value)}")),
    };

    private static RecordResult<ImportDesignInfo> Conflict() =>
        RecordResult<ImportDesignInfo>.Fail(RecordErrorCode.Conflict, "Tasarım siz düzenlerken değişti. Sayfayı yenileyip tekrar deneyin.");

    private TenantContext RequireTenant() => tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant yok.");
}
