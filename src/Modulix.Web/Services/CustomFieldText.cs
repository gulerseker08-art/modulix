using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Shared.Customization;

namespace Modulix.Web.Services;

/// <summary>Özel alan değerlerinin ekrandaki hali ve formdan kayda dönüşümü (Y9a).</summary>
public static class CustomFieldText
{
    /// <summary>Listede ve ayrıntıda gösterilen metin; boşsa boş metin.</summary>
    public static string Format(CustomFieldDto field, JsonObject? values)
    {
        var value = values?[field.Key];
        return value switch
        {
            null => string.Empty,
            JsonArray array => string.Join(", ", array.Select(i => OptionLabel(field, i?.GetValue<string>()))),
            JsonValue v when v.GetValueKind() == JsonValueKind.True => "Evet",
            JsonValue v when v.GetValueKind() == JsonValueKind.False => "Hayır",
            JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
                decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("#,0.##", Regional.Numbers)
                    : v.ToJsonString(),
            JsonValue v when field.Type == CustomFieldType.Date && DateOnly.TryParseExact(
                v.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => CrmLabels.Date(date),
            JsonValue v when field.Type == CustomFieldType.Choice => OptionLabel(field, v.GetValue<string>()),
            _ => value.GetValue<string>(),
        };
    }

    /// <summary>
    /// Kaydedilecek özel alanlar: türün her etkin alanı, boşsa null (sunucu boşaltır ve zorunluluğu denetler).
    /// </summary>
    public static JsonObject ForSave(IEnumerable<CustomFieldDto> fields, JsonObject values)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            result[field.Key] = values[field.Key]?.DeepClone();
        }

        return result;
    }

    private static string OptionLabel(CustomFieldDto field, string? key) =>
        field.Options.FirstOrDefault(o => o.Key == key)?.Label ?? key ?? string.Empty;
}
