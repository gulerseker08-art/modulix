using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>Bir kaydın ilişkili kayıt sayısını modülün çözümleyicisinden alır.</summary>
public interface IAutomationRelationCounter
{
    /// <summary>İlişki bilinmiyorsa ya da çözümleyici kayıtlı değilse <c>null</c>.</summary>
    Task<int?> CountAsync(Guid recordId, ConditionRelation relation, CancellationToken cancellationToken);
}

public sealed class AutomationRelationCounter(ModuleCatalog modules, IServiceProvider services) : IAutomationRelationCounter
{
    public async Task<int?> CountAsync(Guid recordId, ConditionRelation relation, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationRelation(relation.Relation) is not { } definition
            || services.GetService(definition.Resolver) is not IAutomationRelationResolver resolver)
        {
            return null;
        }

        return await resolver.CountAsync(new AutomationRelationQuery(relation.Relation, recordId, relation.Where), cancellationToken);
    }
}

/// <summary>
/// Kayıt değerlerinden (<see cref="Contracts.Records.RecordData.Values"/>) ve (varsa) olayın farkından kurulan olgular. Alan yetkisi
/// uygulanır: değerlendiren kimliğin göremediği alan <see cref="AutomationFactState.Hidden"/> döner. Eski değer yalnızca fark
/// verildiyse bilinir; farkta olmayan alanın eski değeri yenisiyle aynıdır (alan değişmedi).
/// </summary>
public sealed class RecordFacts(
    AutomationFieldSet fields,
    Guid recordId,
    JsonObject values,
    JsonObject? changes,
    IAutomationRelationCounter relations) : IAutomationFacts
{
    public bool HasChangeInformation => changes is not null;

    public AutomationFact Get(string fieldKey, AutomationValueSource source)
    {
        var field = fields.Find(fieldKey);
        if (field is null)
        {
            return AutomationFact.Unavailable;
        }

        if (!fields.CanRead(field))
        {
            return AutomationFact.Hidden;
        }

        JsonNode? node;
        if (source == AutomationValueSource.Old)
        {
            if (changes is null)
            {
                return AutomationFact.Unavailable;
            }

            node = changes[field.Name] is JsonObject diff ? diff["old"] : Read(values, field.Name);
        }
        else
        {
            node = changes is not null && changes[field.Name] is JsonObject diff ? diff["new"] : Read(values, field.Name);
        }

        return Convert(field, node, source);
    }

    public Task<int?> CountRelatedAsync(ConditionRelation relation, CancellationToken cancellationToken) =>
        relations.CountAsync(recordId, relation, cancellationToken);

    // Yol "a.b" biçimindedir (özel alan: "custom.anahtar"); ara düğüm nesne değilse değer yoktur.
    private static JsonNode? Read(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private AutomationFact Convert(AutomationFieldDefinition field, JsonNode? node, AutomationValueSource source)
    {
        if (node is null || (node is JsonValue empty && empty.TryGetValue<string>(out var text) && text.Length == 0))
        {
            return AutomationFact.Null;
        }

        if (node is not JsonValue json)
        {
            return AutomationFact.Unavailable;
        }

        switch (field.Type)
        {
            case AutomationFieldType.Text:
            case AutomationFieldType.Choice:
                return json.TryGetValue<string>(out var s) ? AutomationFact.Of(s) : AutomationFact.Unavailable;

            case AutomationFieldType.Number:
                return TryDecimal(json, out var number) ? AutomationFact.Of(number) : AutomationFact.Unavailable;

            case AutomationFieldType.Money:
                if (!TryDecimal(json, out var amount))
                {
                    return AutomationFact.Unavailable;
                }

                // Para birimi kardeş alandan; farkta yoksa kaydın güncel değeri kullanılır.
                var currency = field.CurrencyField is { } currencyField && Read(values, currencyField) is JsonValue c && c.TryGetValue<string>(out var code)
                    ? code
                    : null;
                return currency is null ? AutomationFact.Unavailable : AutomationFact.Of(new AutomationMoney(amount, currency.ToUpperInvariant()));

            case AutomationFieldType.Boolean:
                return json.TryGetValue<bool>(out var flag) ? AutomationFact.Of(flag) : AutomationFact.Unavailable;

            case AutomationFieldType.Date:
                return json.TryGetValue<string>(out var dateText)
                    && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateMoment)
                    ? AutomationFact.Of(DateOnly.FromDateTime(dateText.Length == 10 ? dateMoment.Date : dateMoment.UtcDateTime))
                    : AutomationFact.Unavailable;

            case AutomationFieldType.DateTime:
                return json.TryGetValue<string>(out var momentText)
                    && DateTimeOffset.TryParse(momentText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                    ? AutomationFact.Of(moment.ToUniversalTime())
                    : AutomationFact.Unavailable;

            case AutomationFieldType.Lookup:
                return json.TryGetValue<string>(out var idText) && Guid.TryParse(idText, out var id) && id != Guid.Empty
                    ? AutomationFact.Of(id)
                    : AutomationFact.Unavailable;

            default:
                return AutomationFact.Unavailable;
        }
    }

    private static bool TryDecimal(JsonValue json, out decimal value)
    {
        var kind = json.GetValueKind();
        var text = kind switch
        {
            System.Text.Json.JsonValueKind.Number => json.ToJsonString(),
            System.Text.Json.JsonValueKind.String => json.GetValue<string>(),
            _ => null,
        };
        value = 0;
        return text is not null
            && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
