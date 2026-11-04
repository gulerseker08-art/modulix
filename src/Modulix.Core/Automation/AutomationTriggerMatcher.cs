using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>
/// Olayın kuralın tetikleyicisine uyup uymadığına <b>yalnızca olayın farkından</b> karar verir (ADR-0027); kayıt okunmaz, koşul
/// değerlendirilmez. Bu ucuz süzgeç, her olayın her kural için çalışma satırı üretmesini engeller. Zamanlama tetikleyicileri
/// (<see cref="ScheduledTrigger"/>, <see cref="DateRelativeTrigger"/>, <see cref="InactivityTrigger"/>) ve iş olayı
/// (<see cref="DomainEventTrigger"/>) olay yoluyla eşleşmez; bu dilimde çalışmazlar.
/// </summary>
public static class AutomationTriggerMatcher
{
    public static bool Matches(AutomationTrigger trigger, string entityKey, string eventType, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(payload);

        switch (trigger)
        {
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
    /// Tetikleyici bu sürümde çalışır mı? Kayıt olayı tetikleyicileri ve elle çalıştırma çalışır; tarama/zamanlama tetikleyicileri ile iş olayı
    /// henüz çalışmaz (ADR-0027 Dilim 2b). Çalışmayan tetikleyiciyle yayın reddedilir: sessizce hiç tetiklenmeyen kural bırakılmaz.
    /// </summary>
    public static bool IsExecutable(AutomationTrigger trigger) =>
        trigger is RecordCreatedTrigger or FieldChangedTrigger or StatusTransitionTrigger or ManualTrigger;

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
            if (modules.FindAutomationAction(step.Action) is { } found)
            {
                permissions.UnionWith(found.Action.Definition.RequiredPermissions);
            }

            foreach (var value in (step.Parameters ?? new JsonObject()).Select(p => p.Value))
            {
                if (ActionParameterBinding.FieldOf(value) is { } fieldKey)
                {
                    AddRead(modules.FindAutomationField(fieldKey)?.ReadPermission);
                }
            }
        }

        return permissions;
    }
}
