using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Modules;

/// <summary>Kural altyapısı yetenek beyanları (ADR-0027): modül başına sabit; açılışta doğrulanır.</summary>
public sealed partial class ModuleCatalog
{
    private readonly Dictionary<string, (string ModuleKey, AutomationEntityDefinition Entity)> automationEntities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, AutomationEventDefinition Event)> automationEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, AutomationFieldDefinition Field)> automationFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, AutomationRelationDefinition Relation)> automationRelations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, AutomationActionRegistration Action)> automationActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, AutomationTemplateDefinition Template)> automationTemplates = new(StringComparer.Ordinal);

    /// <summary>Kural yazılabilen kayıt türleri, modül sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, AutomationEntityDefinition Entity)> AutomationEntities =>
        Modules.SelectMany(m => m.Automation.Entities).Select(e => automationEntities[e.EntityKey]);

    public (string ModuleKey, AutomationEntityDefinition Entity)? FindAutomationEntity(string entityKey) =>
        automationEntities.TryGetValue(entityKey, out var entity) ? entity : null;

    /// <summary>Kayıt türünün kuralda okunabilen alanları, modülün bildirdiği sırayla.</summary>
    public IEnumerable<AutomationFieldDefinition> AutomationFieldsOf(string entityKey) =>
        automationFields.Values.Where(f => f.Field.EntityKey == entityKey).Select(f => f.Field);

    public AutomationFieldDefinition? FindAutomationField(string fieldKey) =>
        automationFields.TryGetValue(fieldKey, out var field) ? field.Field : null;

    public IEnumerable<AutomationEventDefinition> AutomationEventsOf(string entityKey) =>
        automationEvents.Values.Where(e => e.Event.EntityKey == entityKey).Select(e => e.Event);

    public AutomationEventDefinition? FindAutomationEvent(string key) =>
        automationEvents.TryGetValue(key, out var automationEvent) ? automationEvent.Event : null;

    public IEnumerable<AutomationRelationDefinition> AutomationRelationsOf(string entityKey) =>
        automationRelations.Values.Where(r => r.Relation.EntityKey == entityKey).Select(r => r.Relation);

    public AutomationRelationDefinition? FindAutomationRelation(string key) =>
        automationRelations.TryGetValue(key, out var relation) ? relation.Relation : null;

    /// <summary>Aksiyonlar ve modülleri, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, AutomationActionRegistration Action)> AutomationActions =>
        Modules.SelectMany(m => m.Automation.Actions).Select(a => automationActions[a.Definition.Key]);

    public (string ModuleKey, AutomationActionRegistration Action)? FindAutomationAction(string key) =>
        automationActions.TryGetValue(key, out var action) ? action : null;

    /// <summary>Başlangıç şablonları ve modülleri, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, AutomationTemplateDefinition Template)> AutomationTemplates =>
        Modules.SelectMany(m => m.Automation.Templates).Select(t => automationTemplates[t.Key]);

    public (string ModuleKey, AutomationTemplateDefinition Template)? FindAutomationTemplate(string key) =>
        automationTemplates.TryGetValue(key, out var template) ? template : null;

    [GeneratedRegex("^[a-z][A-Za-z0-9_]*(\\.[a-z][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomationFieldNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+(-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomationKeyPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomationParameterPattern();

    // Bütün modüller okunduktan sonra çağrılır: izinler, modüller ve lookup'lar katalogda tamdır.
    private void RegisterAutomation()
    {
        foreach (var module in Modules)
        {
            var capabilities = module.Automation;
            string Prefix() => module.Key + ".";
            InvalidOperationException Fail(string what) => new($"Geçersiz kural yeteneği beyanı ({module.Key}): {what}.");

            foreach (var entity in capabilities.Entities)
            {
                if (!entity.EntityKey.StartsWith(Prefix(), StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(entity.DisplayName)
                    || (entity.ReadPermission is { } read && module.Permissions.All(p => p.Key != read))
                    || !automationEntities.TryAdd(entity.EntityKey, (module.Key, entity)))
                {
                    throw Fail($"kayıt türü '{entity.EntityKey}' modülün olmalı, tekil olmalı, adı ve (varsa) izni modülün olmalı");
                }

                if (entity.RecordSource is { } source
                    && (!typeof(IAutomationRecordSource).IsAssignableFrom(source) || source.IsAbstract || source.IsInterface))
                {
                    throw Fail($"kayıt türü '{entity.EntityKey}' kayıt kaynağı IAutomationRecordSource uygulayan somut bir tip olmalı");
                }
            }

            foreach (var automationEvent in capabilities.Events)
            {
                var knownRecordEvent = automationEvent.EventType is DomainEventTypes.RecordCreated or DomainEventTypes.RecordUpdated;
                if (!AutomationKeyPattern().IsMatch(automationEvent.Key)
                    || !automationEvent.Key.StartsWith(Prefix(), StringComparison.Ordinal)
                    || !capabilities.Entities.Any(e => e.EntityKey == automationEvent.EntityKey)
                    || !(knownRecordEvent || automationEvent.EventType.StartsWith(Prefix(), StringComparison.Ordinal))
                    || string.IsNullOrWhiteSpace(automationEvent.DisplayName)
                    || !automationEvents.TryAdd(automationEvent.Key, (module.Key, automationEvent)))
                {
                    throw Fail($"olay '{automationEvent.Key}': anahtar modül önekli ve tekil, kayıt türü modülün, tür kayıt olayı ya da modül önekli olmalı");
                }
            }

            foreach (var field in capabilities.Fields)
            {
                ValidateAutomationField(module, field, capabilities.Entities.Select(e => e.EntityKey).ToHashSet(StringComparer.Ordinal), Fail);
                if (!automationFields.TryAdd(field.Key, (module.Key, field)))
                {
                    throw Fail($"alan '{field.Key}' iki kez tanımlı");
                }
            }

            foreach (var relation in capabilities.Relations)
            {
                if (!AutomationKeyPattern().IsMatch(relation.Key)
                    || !relation.Key.StartsWith(Prefix(), StringComparison.Ordinal)
                    || !capabilities.Entities.Any(e => e.EntityKey == relation.EntityKey)
                    || !relation.TargetEntityKey.StartsWith(Prefix(), StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(relation.Label)
                    || (relation.ReadPermission is { } read && module.Permissions.All(p => p.Key != read))
                    || !typeof(IAutomationRelationResolver).IsAssignableFrom(relation.Resolver)
                    || relation.Resolver.IsAbstract)
                {
                    throw Fail($"ilişki '{relation.Key}': anahtar modül önekli, kaynak ve hedef türü modülün, çözümleyici IAutomationRelationResolver uygulamalı");
                }

                var targetNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var target in relation.TargetFields)
                {
                    if (target.EntityKey != relation.TargetEntityKey || !targetNames.Add(target.Name))
                    {
                        throw Fail($"ilişki '{relation.Key}' hedef alanı '{target.Key}' hedef türe ait ve tekil olmalı");
                    }

                    ValidateAutomationField(module, target, new HashSet<string>(StringComparer.Ordinal) { relation.TargetEntityKey }, Fail);
                }

                if (!automationRelations.TryAdd(relation.Key, (module.Key, relation)))
                {
                    throw Fail($"ilişki '{relation.Key}' iki kez tanımlı");
                }
            }

            foreach (var registration in capabilities.Actions)
            {
                ValidateAutomationAction(module, registration, Fail);
                if (!automationActions.TryAdd(registration.Definition.Key, (module.Key, registration)))
                {
                    throw Fail($"aksiyon '{registration.Definition.Key}' iki kez tanımlı");
                }
            }
        }

        // Şablonlar bütün aksiyonlar kayıtlandıktan sonra doğrulanır (aksiyon başka modülden de olabilir).
        foreach (var module in Modules)
        {
            foreach (var template in module.Automation.Templates)
            {
                ValidateAutomationTemplate(module, template);
                if (!automationTemplates.TryAdd(template.Key, (module.Key, template)))
                {
                    throw TemplateFail(module, template, "iki kez tanımlı");
                }
            }
        }

        // Kural aboneliği yalnızca kural yazılabilen kayıt türlerinin olaylarını alır (atama e-postasındaki gibi katalog doldurur):
        // hiçbir modül kural yeteneği beyan etmediyse abonelik de yoktur ve olay teslimine ek yük binmez.
        if (subscriptions.TryGetValue(CoreModule.Subscriptions.Automation, out var automation))
        {
            var entityKeys = automationEntities.Keys.Order(StringComparer.Ordinal).ToList();
            if (entityKeys.Count == 0)
            {
                subscriptions.Remove(automation.Subscription.Key);
            }
            else
            {
                subscriptions[automation.Subscription.Key] = (automation.ModuleKey, automation.Subscription with { EntityKeys = entityKeys });
            }
        }

        // Kayda bağlı onay aboneliği (ADR-0027 Dilim 4c) de yalnızca onaya açık kayıt türlerinin olaylarını alır; beyan yoksa abonelik yoktur.
        if (subscriptions.TryGetValue(CoreModule.Subscriptions.Approvals, out var approvals))
        {
            var entityKeys = Modules.SelectMany(m => m.ApprovalTargets).Select(t => t.EntityKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (entityKeys.Count == 0)
            {
                subscriptions.Remove(approvals.Subscription.Key);
            }
            else
            {
                subscriptions[approvals.Subscription.Key] = (approvals.ModuleKey, approvals.Subscription with { EntityKeys = entityKeys });
            }
        }
    }

    private static InvalidOperationException TemplateFail(IModule module, AutomationTemplateDefinition template, string what) =>
        new($"Geçersiz kural yeteneği beyanı ({module.Key}): şablon '{template.Key}' {what}.");

    // Açılışta yalnızca yapı denetlenir; alan/koşul ayrıntısı kullanan kimliğin yetkisiyle yayında doğrulanır.
    private void ValidateAutomationTemplate(IModule module, AutomationTemplateDefinition template)
    {
        if (!AutomationKeyPattern().IsMatch(template.Key)
            || !template.Key.StartsWith(module.Key + ".", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(template.DisplayName)
            || template.DisplayName.Trim().Length > 200
            || string.IsNullOrWhiteSpace(template.Description)
            || !module.Automation.Entities.Any(e => e.EntityKey == template.EntityKey))
        {
            throw TemplateFail(module, template, "anahtarı modül önekli, adı ve açıklaması dolu, kayıt türü modülün olmalı");
        }

        var actions = template.Definition.Actions;
        if (actions.Count == 0 || actions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != actions.Count)
        {
            throw TemplateFail(module, template, "en az bir aksiyon içermeli ve adım kimlikleri tekil olmalı");
        }

        var unknown = actions.Where(a => !automationActions.ContainsKey(a.Action)).Select(a => a.Action).ToList();
        if (unknown.Count > 0)
        {
            throw TemplateFail(module, template, $"kayıtlı olmayan aksiyon kullanıyor: {string.Join(", ", unknown)}");
        }
    }

    private void ValidateAutomationField(
        IModule module, AutomationFieldDefinition field, IReadOnlySet<string> entityKeys, Func<string, InvalidOperationException> fail)
    {
        if (!entityKeys.Contains(field.EntityKey)
            || !AutomationFieldNamePattern().IsMatch(field.Name)
            || string.IsNullOrWhiteSpace(field.Label)
            || (field.ReadPermission is { } read && !permissions.ContainsKey(read))
            || (field.Type == AutomationFieldType.Lookup && (field.LookupKey is null || !lookups.ContainsKey(field.LookupKey)))
            || (field.Type != AutomationFieldType.Lookup && field.LookupKey is not null)
            || (field.Type == AutomationFieldType.Money) != (field.CurrencyField is not null)
            || (field.Type != AutomationFieldType.Choice && field.Options.Count > 0)
            || field.Options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != field.Options.Count)
        {
            throw fail($"alan '{field.Key}': tür modülün, ad geçerli, izin ve lookup katalogda olmalı, seçenekler yalnızca seçim alanında ve tekil olmalı");
        }
    }

    private void ValidateAutomationAction(IModule module, AutomationActionRegistration registration, Func<string, InvalidOperationException> fail)
    {
        var definition = registration.Definition;
        if (!AutomationKeyPattern().IsMatch(definition.Key)
            || !definition.Key.StartsWith(module.Key + ".", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(definition.DisplayName)
            || string.IsNullOrWhiteSpace(definition.Description)
            || !typeof(IAutomationAction).IsAssignableFrom(registration.Handler)
            || registration.Handler.IsAbstract)
        {
            throw fail($"aksiyon '{definition.Key}': anahtar modül önekli, ad ve açıklama dolu, işleyici IAutomationAction uygulamalı");
        }

        var foreign = definition.RequiredPermissions.Where(p => module.Permissions.All(d => d.Key != p)).ToList();
        if (foreign.Count > 0)
        {
            throw fail($"aksiyon '{definition.Key}' modülün tanımlamadığı izin istiyor: {string.Join(", ", foreign)}");
        }

        var unknownModules = definition.RequiredModules.Where(m => !modules.ContainsKey(m)).ToList();
        if (unknownModules.Count > 0)
        {
            throw fail($"aksiyon '{definition.Key}' bilinmeyen modül istiyor: {string.Join(", ", unknownModules)}");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            if (!AutomationParameterPattern().IsMatch(parameter.Name)
                || string.IsNullOrWhiteSpace(parameter.Label)
                || !names.Add(parameter.Name)
                || (parameter.Type == AutomationFieldType.Choice && parameter.Options.Count == 0)
                || (parameter.Type != AutomationFieldType.Choice && parameter.Options.Count > 0)
                || (parameter.Type == AutomationFieldType.Lookup && (parameter.LookupKey is null || !lookups.ContainsKey(parameter.LookupKey)))
                || (parameter.Type != AutomationFieldType.Lookup && parameter.LookupKey is not null)
                || parameter.Type == AutomationFieldType.Money)
            {
                throw fail($"aksiyon '{definition.Key}' parametresi '{parameter.Name}': ad geçerli ve tekil, seçim/lookup tanımı tipe uygun olmalı (para parametresi desteklenmez)");
            }
        }
    }
}
