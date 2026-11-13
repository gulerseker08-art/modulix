using System.Diagnostics;
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
/// Özel alan tanımları (Y9a). Boru hattına da tanımları verir (<see cref="ICustomFieldProvider"/>): kayıt türü başına
/// istekte bir kez okunur. Seçenek silinmez: listede verilmeyen seçenek pasif olarak kalır, çünkü kayıtlarda değeri olabilir.
/// </summary>
internal sealed class CustomFieldService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    IAuditWriter audit,
    CustomizationLimits limits) : ICustomFieldService, ICustomFieldProvider
{
    private readonly Dictionary<(Guid TenantId, string EntityKey), IReadOnlyList<CustomFieldInfo>> cache = [];

    public async Task<IReadOnlyList<CustomFieldInfo>> GetFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((tenant.TenantId, entityKey), out var fields))
        {
            return fields;
        }

        fields = (await db.CustomFields.AsNoTracking()
                .Where(f => f.EntityKey == entityKey && f.ArchivedAt == null)
                .OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt)
                .ToListAsync(cancellationToken))
            .Select(f => f.ToInfo())
            .ToList();
        cache[(tenant.TenantId, entityKey)] = fields;
        return fields;
    }

    public async Task<IReadOnlyList<CustomizableEntity>> ListEntitiesAsync(CancellationToken cancellationToken)
    {
        var enabled = await entitlements.GetEnabledAsync(RequireTenant().TenantId, cancellationToken);
        return catalog.CustomizableEntities.Where(e => catalog.IsEnabled(e.ModuleKey, enabled)).Select(e => e.Entity).ToList();
    }

    public async Task<IReadOnlyList<CustomFieldInfo>> ListAsync(string? entityKey, bool includeArchived, CancellationToken cancellationToken)
    {
        var entities = (await ListEntitiesAsync(cancellationToken)).Select(e => e.EntityKey).ToList();
        if (entityKey is not null)
        {
            entities = entities.Where(e => e == entityKey).ToList();
        }

        var fields = await db.CustomFields.AsNoTracking()
            .Where(f => entities.Contains(f.EntityKey) && (includeArchived || f.ArchivedAt == null))
            .ToListAsync(cancellationToken);
        return fields
            .OrderBy(f => entities.IndexOf(f.EntityKey)).ThenBy(f => f.SortOrder).ThenBy(f => f.CreatedAt)
            .Select(f => f.ToInfo())
            .ToList();
    }

    public async Task<CustomFieldInfo> CreateAsync(string entityKey, CustomFieldInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = RequireTenant();
        await EnsureCustomizableAsync(entityKey, cancellationToken);

        var existing = await db.CustomFields.Where(f => f.EntityKey == entityKey).ToListAsync(cancellationToken);
        if (limits.MaxFieldsPerEntity is { } maxFields && existing.Count(f => !f.IsArchived) >= maxFields)
        {
            throw new ArgumentException($"Bir kayıt türünde en fazla {maxFields} özel alan olabilir.", "label");
        }

        if (string.IsNullOrWhiteSpace(input.Label))
        {
            throw new ArgumentException("Alan adı zorunludur.", "label");
        }

        var key = CustomFieldRules.KeyFrom(input.Label, existing.Select(f => f.Key).ToHashSet(StringComparer.Ordinal));
        var field = new CustomField(entityKey, key, input.Label, input.Type, existing.Count == 0 ? 0 : existing.Max(f => f.SortOrder) + 1);
        Apply(field, input, existing);
        db.CustomFields.Add(field);
        Audit(tenant, field.Id, AuditAction.Created, before: null, after: Snapshot(field));
        await db.SaveChangesAsync(cancellationToken);
        cache.Clear();
        return field.ToInfo();
    }

    public async Task<CustomFieldInfo?> UpdateAsync(Guid id, CustomFieldInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = RequireTenant();
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return null;
        }

        await EnsureCustomizableAsync(field.EntityKey, cancellationToken);
        var before = Snapshot(field);
        var others = await db.CustomFields.Where(f => f.EntityKey == field.EntityKey && f.Id != field.Id).ToListAsync(cancellationToken);
        field.Relabel(input.Label);
        Apply(field, input, others);
        var after = Snapshot(field);
        if (!JsonNode.DeepEquals(before, after))
        {
            Audit(tenant, field.Id, AuditAction.Updated, before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        cache.Clear();
        return field.ToInfo();
    }

    public async Task<CustomFieldInfo?> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return null;
        }

        await EnsureCustomizableAsync(field.EntityKey, cancellationToken);
        if (field.IsArchived == archived)
        {
            return field.ToInfo();
        }

        if (!archived)
        {
            var active = await db.CustomFields.CountAsync(f => f.EntityKey == field.EntityKey && f.ArchivedAt == null, cancellationToken);
            if (limits.MaxFieldsPerEntity is { } maxFields && active >= maxFields)
            {
                throw new ArgumentException($"Bir kayıt türünde en fazla {maxFields} özel alan olabilir.", "archived");
            }

            await EnsureUniqueLabelAsync(field.EntityKey, field.Label, field.Id, cancellationToken);
        }

        var before = Snapshot(field);
        if (archived)
        {
            field.Archive();
        }
        else
        {
            field.Restore();
        }

        Audit(tenant, field.Id, AuditAction.Updated, before, Snapshot(field));
        await db.SaveChangesAsync(cancellationToken);
        cache.Clear();
        return field.ToInfo();
    }

    public async Task ReorderAsync(string entityKey, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        RequireTenant();
        await EnsureCustomizableAsync(entityKey, cancellationToken);
        var fields = await db.CustomFields.Where(f => f.EntityKey == entityKey).ToListAsync(cancellationToken);
        var ordered = ids.Distinct()
            .Select(id => fields.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Bu türde olmayan alan verildi.", nameof(ids)))
            .Concat(fields.Where(f => !ids.Contains(f.Id)).OrderBy(f => f.SortOrder))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        // Sıra ekranın düzenidir; denetim kaydına yazılmaz.
        await db.SaveChangesAsync(cancellationToken);
        cache.Clear();
    }

    // Ad türde benzersizdir (arşivdekiler hariç); seçenekler yalnızca seçim tiplerinde, pasife alınabilir ama silinmez.
    private void Apply(CustomField field, CustomFieldInput input, IReadOnlyCollection<CustomField> others)
    {
        if (!field.IsArchived && others.Any(f => !f.IsArchived && string.Equals(f.Label, field.Label, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Bu adda bir alan zaten var: '{field.Label}'.", "label");
        }

        field.Required = input.Required;
        field.ShowInList = input.ShowInList;
        if (!field.HasOptions)
        {
            return;
        }

        var current = field.Options;
        var options = new List<CustomFieldOption>();
        var keys = current.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var option in input.Options ?? [])
        {
            if (string.IsNullOrWhiteSpace(option.Label) || option.Label.Trim().Length > CustomField.LabelMaxLength)
            {
                throw new ArgumentException($"Seçenek adı zorunludur ve en fazla {CustomField.LabelMaxLength} karakter olabilir.", "options");
            }

            var label = option.Label.Trim();
            if (options.Any(o => string.Equals(o.Label, label, StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new ArgumentException($"Seçenek iki kez verildi: '{label}'.", "options");
            }

            if (option.Key is { } key)
            {
                if (!current.Any(o => o.Key == key) || options.Any(o => o.Key == key))
                {
                    throw new ArgumentException("Bilinmeyen seçenek.", "options");
                }

                options.Add(new CustomFieldOption(key, label, option.Active));
            }
            else
            {
                var generated = CustomFieldRules.KeyFrom(label, keys);
                keys.Add(generated);
                options.Add(new CustomFieldOption(generated, label, option.Active));
            }
        }

        // Listede verilmeyen eski seçenek silinmez, pasif kalır: kayıtlardaki değeri anlamını korur.
        options.AddRange(current.Where(o => options.All(n => n.Key != o.Key)).Select(o => o with { Active = false }));
        if (limits.MaxOptionsPerField is { } maxOptions && options.Count > maxOptions)
        {
            throw new ArgumentException($"En fazla {maxOptions} seçenek olabilir.", "options");
        }

        if (!options.Any(o => o.Active))
        {
            throw new ArgumentException("En az bir etkin seçenek olmalıdır.", "options");
        }

        field.Options = options;
    }

    private async Task EnsureUniqueLabelAsync(string entityKey, string label, Guid exceptId, CancellationToken cancellationToken)
    {
        var labels = await db.CustomFields
            .Where(f => f.EntityKey == entityKey && f.Id != exceptId && f.ArchivedAt == null)
            .Select(f => f.Label)
            .ToListAsync(cancellationToken);
        if (labels.Any(l => string.Equals(l, label, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Bu adda bir alan zaten var: '{label}'. Önce birinin adını değiştirin.", "label");
        }
    }

    // Kayıt türü tanımlı ve modülü açık olmalı; kapalı modülün alanları değiştirilemez (veriler kalır).
    private async Task EnsureCustomizableAsync(string entityKey, CancellationToken cancellationToken)
    {
        if ((await ListEntitiesAsync(cancellationToken)).All(e => e.EntityKey != entityKey))
        {
            throw new ArgumentException("Bu kayıt türüne özel alan eklenemez.", nameof(entityKey));
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan özel alanlar yönetilemez.");

    private void Audit(TenantContext tenant, Guid fieldId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.CustomField,
            fieldId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private JsonObject Snapshot(CustomField field) => new()
    {
        ["entity"] = catalog.FindCustomizable(field.EntityKey)?.Entity.DisplayName ?? field.EntityKey,
        ["label"] = field.Label,
        ["key"] = field.Key,
        ["type"] = field.Type.ToString(),
        ["required"] = field.Required,
        ["showInList"] = field.ShowInList,
        ["options"] = field.HasOptions
            ? new JsonArray([.. field.Options.Select(o => JsonValue.Create(o.Active ? o.Label : $"{o.Label} (pasif)"))])
            : null,
        ["archived"] = field.IsArchived,
    };
}
