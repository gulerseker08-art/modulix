using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>
/// Olayın kuralın tetikleyicisine uyup uymadığına <b>yalnızca olayın farkından</b> karar verir (ADR-0027); kayıt okunmaz, koşul
/// değerlendirilmez. Bu ucuz süzgeç, her olayın her kural için çalışma satırı üretmesini engeller. Zamanlama tetikleyicileri
/// (<see cref="ScheduledTrigger"/>, <see cref="DateRelativeTrigger"/>, <see cref="InactivityTrigger"/>) olay yoluyla eşleşmez, günlük
/// taramayla çalışır (<c>AutomationScanner</c>). İş olayı (<see cref="DomainEventTrigger"/>, ADR-0039) modülün beyan ettiği olay
/// anahtarını katalogdan türe çevirip olayın türü ve kayıt türüyle eşler; <paramref name="events"/> bu çözümlemeyi verir.
/// </summary>
public static class AutomationTriggerMatcher
{
    public static bool Matches(
        AutomationTrigger trigger, string entityKey, string eventType, JsonObject payload, Func<string, AutomationEventDefinition?>? events = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(payload);

        switch (trigger)
        {
            case DomainEventTrigger domainEvent:
                // Yalnızca modül önekli iş olayı; kayıt olayı anahtarı (RecordCreated/RecordUpdated) bu tetikleyiciyle eşleşmez.
                return events?.Invoke(domainEvent.EventKey) is { } declared
                    && declared.EntityKey == entityKey
                    && declared.EventType == eventType
                    && eventType is not (DomainEventTypes.RecordCreated or DomainEventTypes.RecordUpdated);

            case RecordCreatedTrigger:
                return eventType == DomainEventTypes.RecordCreated;

            case FieldChangedTrigger changed:
                return eventType == DomainEventTypes.RecordUpdated
                    && changed.Fields.Any(field => payload.ContainsKey(FieldName(entityKey, field)));

            case StatusTransitionTrigger transition:
                if (eventType is not (DomainEventTypes.RecordUpdated or DomainEventTypes.RecordCreated)
                    || payload[FieldName(entityKey, transition.Field)] is not JsonObject change)
                {
                    return false;
                }

                return Text(change["new"]) == transition.To
                    && (string.IsNullOrEmpty(transition.From) || Text(change["old"]) == transition.From);

            default:
                return false;
        }
    }

    /// <summary>
    /// Tetikleyici bu sürümde çalışır mı? Kayıt olayı tetikleyicileri, elle çalıştırma, tarama tabanlı tetikleyiciler (kayıt türünün
    /// kayıt kaynağı ayrıca doğrulanır) ve iş olayı (ADR-0027 Dilim 2b-3, ADR-0039; beyanlı olay anahtarı ayrıca doğrulanır) çalışır.
    /// Bilinmeyen tetikleyiciyle yayın reddedilir: sessizce hiç tetiklenmeyen kural bırakılmaz.
    /// </summary>
    public static bool IsExecutable(AutomationTrigger trigger) =>
        trigger is RecordCreatedTrigger or FieldChangedTrigger or StatusTransitionTrigger or ManualTrigger
            or ScheduledTrigger or DateRelativeTrigger or InactivityTrigger or DomainEventTrigger;

    /// <summary>Tetikleyici bir günlük taramayla mı çalışır (olay yoluyla değil)?</summary>
    public static bool IsScan(AutomationTrigger trigger) => trigger is ScheduledTrigger or DateRelativeTrigger or InactivityTrigger;

    /// <summary>Tetikleyiciye göre olayda aranan alan adları (değişen kayıt değerleri bu adlarla gelir).</summary>
    private static string FieldName(string entityKey, string fieldKey) =>
        fieldKey.StartsWith(entityKey + ".", StringComparison.Ordinal) ? fieldKey[(entityKey.Length + 1)..] : fieldKey;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString();
}

/// <summary>
/// Kuralın çalışma kimliğinin izinleri (ADR-0027): kural, yayınlayanın değil <b>kendi tanımının gerektirdiği</b> izinlerle çalışır —
/// kayıt türünün okuma izni, koşulda ve parametre bağlamalarında kullanılan alanların/ilişkilerin okuma izni, aksiyonların
/// gerektirdiği izinler. Yayın doğrulaması yayınlayanın bunların hepsine sahip olduğunu denetlemiştir; kimse tanımın
/// dışında bir şey yapamaz. Çalışma anında bu küme tenant'ın açık modülleriyle ayrıca süzülür.
/// </summary>
public static class AutomationRuleIdentity
{
    public static IReadOnlySet<string> Permissions(string entityKey, AutomationDefinition definition, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(modules);

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        if (modules.FindAutomationEntity(entityKey)?.Entity.ReadPermission is { } entityRead)
        {
            permissions.Add(entityRead);
        }

        void AddRead(string? read)
        {
            if (read is not null)
            {
                permissions.Add(read);
            }
        }

        // İlişkinin Where koşulundaki yapraklar hedef kayıt türünün alanlarına bakar; bu alanlar ilişki tanımında aranır.
        void Walk(ConditionNode? node, IReadOnlyList<AutomationFieldDefinition>? targetFields = null)
        {
            switch (node)
            {
                case ConditionLeaf leaf:
                    AddRead(targetFields is null
                        ? modules.FindAutomationField(leaf.Field)?.ReadPermission
                        : targetFields.FirstOrDefault(f => f.Key == leaf.Field)?.ReadPermission);
                    break;
                case ConditionGroup group:
                    foreach (var item in group.Items)
                    {
                        Walk(item, targetFields);
                    }

                    break;
                case ConditionRelation relation when modules.FindAutomationRelation(relation.Relation) is { } relationDefinition:
                    AddRead(relationDefinition.ReadPermission);
                    Walk(relation.Where, relationDefinition.TargetFields);
                    break;
            }
        }

        Walk(definition.Condition);
        foreach (var step in definition.Actions)
        {
            // Adım koşulunun (Dilim 4c-5) okuduğu alanlar da kuralın kimliğine girer.
            Walk(step.When);
            if (modules.FindAutomationAction(step.Action) is { } found)
            {
                permissions.UnionWith(found.Action.Definition.RequiredPermissions);
            }

            // "Onaya gönder" (Dilim 4c-2) tutarı ve para birimini parametreden değil kayıttan okur (kayda bağlı onay): kimlik, onay
            // hedefinin korunan alanlarının iznine de sahip olmalı. Yayın denetimi (ApprovalProcessCheck) yayınlayanda da arar.
            if (step.Action == Approvals.ApprovalAutomation.RequestAction)
            {
                foreach (var field in Approvals.ApprovalAutomation.GuardedFields(entityKey, modules))
                {
                    AddRead(field.ReadPermission);
                }
            }

            foreach (var value in (step.Parameters ?? new JsonObject()).Select(p => p.Value))
            {
                if (ActionParameterBinding.FieldOf(value) is { } fieldKey)
                {
                    AddRead(modules.FindAutomationField(fieldKey)?.ReadPermission);
                }
                else if (value is JsonValue json && json.TryGetValue<string>(out var text))
                {
                    // Metin değişkeni ({{alan}}) kayıttan okur: kuralın kimliği o alanın izinlerine de sahip olmalı (Dilim 4d).
                    foreach (var variable in AutomationTextTemplate.Variables(text))
                    {
                        AddRead(modules.FindAutomationField(variable)?.ReadPermission);
                    }
                }
            }
        }

        return permissions;
    }
}
