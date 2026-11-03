using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>Kural editörüne sunulan kayıt türü: alanları (özel alanlar dahil), olayları ve ilişkileri.</summary>
public sealed record AutomationEntityView(
    string ModuleKey,
    string EntityKey,
    string DisplayName,
    IReadOnlyList<AutomationFieldDefinition> Fields,
    IReadOnlyList<AutomationEventDefinition> Events,
    IReadOnlyList<AutomationRelationDefinition> Relations);

/// <summary>Kural editörüne sunulan aksiyon. <see cref="Available"/> yanlışsa neden <see cref="UnavailableReason"/>'dadır.</summary>
public sealed record AutomationActionView(
    string ModuleKey, AutomationActionDefinition Definition, bool Available, string? UnavailableReason);

public sealed record AutomationCatalogView(
    IReadOnlyList<AutomationEntityView> Entities, IReadOnlyList<AutomationActionView> Actions);

/// <summary>
/// Aktif tenant ve kullanıcı için kullanılabilir kural yetenekleri (ADR-0027): modülü tenant için açık olan, kullanıcının
/// okuma iznine sahip olduğu kayıt türleri; modül alanlarına eklenen firmanın özel alanları; aksiyonlar ve neden kullanılamadıkları.
/// </summary>
public interface IAutomationCatalog
{
    Task<AutomationCatalogView> GetAsync(CancellationToken cancellationToken);

    /// <summary>Kayıt türünün alan kümesi (değerlendiren kimliğin izinleriyle); tür bilinmiyor, modülü kapalı ya da izin yoksa <c>null</c>.</summary>
    Task<AutomationFieldSet?> GetFieldSetAsync(string entityKey, CancellationToken cancellationToken);

    /// <summary>Aksiyon ve bu kimlik için kullanılabilirliği; aksiyon bilinmiyor ya da modülü tenant için kapalıysa <c>null</c>.</summary>
    Task<AutomationActionView?> FindActionAsync(string actionKey, CancellationToken cancellationToken);
}

public sealed class AutomationCatalogService(
    ModuleCatalog modules, ICurrentAccess access, ICustomFieldService customFields) : IAutomationCatalog
{
    public async Task<AutomationCatalogView> GetAsync(CancellationToken cancellationToken)
    {
        var permissions = await access.GetPermissionsAsync(cancellationToken);
        var entities = new List<AutomationEntityView>();
        foreach (var (moduleKey, entity) in modules.AutomationEntities)
        {
            if (await BuildEntityAsync(moduleKey, entity, permissions, cancellationToken) is { } view)
            {
                entities.Add(view);
            }
        }

        var actions = new List<AutomationActionView>();
        foreach (var (moduleKey, registration) in modules.AutomationActions)
        {
            if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
            {
                continue;
            }

            var reason = await UnavailableReasonAsync(registration.Definition, moduleKey, permissions, cancellationToken);
            actions.Add(new AutomationActionView(moduleKey, registration.Definition, reason is null, reason));
        }

        return new AutomationCatalogView(entities, actions);
    }

    public async Task<AutomationFieldSet?> GetFieldSetAsync(string entityKey, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationEntity(entityKey) is not { } found)
        {
            return null;
        }

        var permissions = await access.GetPermissionsAsync(cancellationToken);
        var view = await BuildEntityAsync(found.ModuleKey, found.Entity, permissions, cancellationToken);
        return view is null ? null : new AutomationFieldSet(entityKey, view.Fields, view.Relations, permissions);
    }

    public async Task<AutomationActionView?> FindActionAsync(string actionKey, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationAction(actionKey) is not { } found || !await access.IsModuleEnabledAsync(found.ModuleKey, cancellationToken))
        {
            return null;
        }

        var permissions = await access.GetPermissionsAsync(cancellationToken);
        var reason = await UnavailableReasonAsync(found.Action.Definition, found.ModuleKey, permissions, cancellationToken);
        return new AutomationActionView(found.ModuleKey, found.Action.Definition, reason is null, reason);
    }

    /// <summary>Aksiyonun bu kimlik ve tenant için kullanılamama nedeni; kullanılabilirse <c>null</c>.</summary>
    private async Task<string?> UnavailableReasonAsync(
        AutomationActionDefinition definition, string moduleKey, IReadOnlySet<string> permissions, CancellationToken cancellationToken)
    {
        foreach (var required in definition.RequiredModules.Append(moduleKey).Distinct(StringComparer.Ordinal))
        {
            if (!await access.IsModuleEnabledAsync(required, cancellationToken))
            {
                return $"'{modules.Find(required)?.DisplayName ?? required}' modülü bu firmada etkin değil.";
            }
        }

        var missing = definition.RequiredPermissions.Where(p => !permissions.Contains(p)).ToList();
        return missing.Count == 0 ? null : "Bu aksiyon için gereken izne sahip değilsiniz.";
    }

    private async Task<AutomationEntityView?> BuildEntityAsync(
        string moduleKey, AutomationEntityDefinition entity, IReadOnlySet<string> permissions, CancellationToken cancellationToken)
    {
        if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken)
            || (entity.ReadPermission is { } read && !permissions.Contains(read)))
        {
            return null;
        }

        var fields = modules.AutomationFieldsOf(entity.EntityKey).ToList();
        if (modules.FindCustomizable(entity.EntityKey) is not null)
        {
            fields.AddRange(await CustomFieldDefinitionsAsync(entity.EntityKey, cancellationToken));
        }

        return new AutomationEntityView(
            moduleKey,
            entity.EntityKey,
            entity.DisplayName,
            fields,
            [.. modules.AutomationEventsOf(entity.EntityKey)],
            [.. modules.AutomationRelationsOf(entity.EntityKey)]);
    }

    // Özel alanlar kayıt değerlerinde "custom" nesnesi altındadır; çoklu seçim ilk sürümde koşulda yoktur.
    private async Task<IEnumerable<AutomationFieldDefinition>> CustomFieldDefinitionsAsync(string entityKey, CancellationToken cancellationToken)
    {
        var custom = await customFields.ListAsync(entityKey, includeArchived: false, cancellationToken);
        return custom
            .Where(f => f.Type != CustomFieldType.MultiChoice)
            .Select(f => new AutomationFieldDefinition(entityKey, "custom." + f.Key, f.Label, f.Type switch
            {
                CustomFieldType.Number => AutomationFieldType.Number,
                CustomFieldType.Date => AutomationFieldType.Date,
                CustomFieldType.YesNo => AutomationFieldType.Boolean,
                CustomFieldType.Choice => AutomationFieldType.Choice,
                _ => AutomationFieldType.Text,
            })
            {
                Options = f.Type == CustomFieldType.Choice
                    ? [.. f.Options.Where(o => o.Active).Select(o => new AutomationOption(o.Key, o.Label))]
                    : [],
                Hint = "Özel alan",
            });
    }
}
