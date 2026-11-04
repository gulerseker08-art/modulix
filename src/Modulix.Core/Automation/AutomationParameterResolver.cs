using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>Çözülen aksiyon parametreleri. <see cref="Notes"/>: alan bağlaması boş/görülemeyen kayıttan geldiği için değer taşımayanlar.</summary>
public sealed record ResolvedParameters(JsonObject Values, IReadOnlyList<string> Notes);

/// <summary>
/// Aksiyon parametrelerini çözer (ADR-0027): sabit değerler olduğu gibi, <c>{"$field": anahtar}</c> bağlamaları kaydın güncel
/// değerinden. Önizleme ve gerçek çalışma aynı kodu kullanır; önizlemede görülen parametreler çalışmada da aynıdır.
/// Görülemeyen (alan yetkisi) ya da boş alanın değeri aksiyona <c>null</c> gider.
/// </summary>
public static class AutomationParameterResolver
{
    public static ResolvedParameters Resolve(AutomationActionStep step, AutomationFieldSet fields, IAutomationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(facts);

        var values = new JsonObject();
        var notes = new List<string>();
        foreach (var (name, value) in step.Parameters ?? new JsonObject())
        {
            if (ActionParameterBinding.FieldOf(value) is { } fieldKey)
            {
                var fact = facts.Get(fieldKey, AutomationValueSource.New);
                values[name] = fact.State == AutomationFactState.Value ? ToJson(fact.Value!) : null;
                if (fact.State != AutomationFactState.Value)
                {
                    notes.Add($"'{fields.Find(fieldKey)?.Label ?? fieldKey}' bu kayıtta {(fact.State == AutomationFactState.Hidden ? "görülemiyor" : "boş")}.");
                }
            }
            else
            {
                values[name] = value?.DeepClone();
            }
        }

        return new ResolvedParameters(values, notes);
    }

    /// <summary>Şemada zorunlu olup değeri boş kalan parametrelerin etiketleri.</summary>
    public static IReadOnlyList<string> MissingRequired(AutomationActionDefinition definition, JsonObject values) =>
        [.. definition.Parameters
            .Where(p => p.Required && IsBlank(values[p.Name]))
            .Select(p => p.Label)];

    private static bool IsBlank(JsonNode? node) =>
        node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text));

    public static JsonNode? ToJson(object value) => value switch
    {
        string text => text,
        decimal number => number,
        bool flag => flag,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        Guid id => id.ToString(),
        AutomationMoney money => money.Amount,
        _ => value.ToString(),
    };
}
