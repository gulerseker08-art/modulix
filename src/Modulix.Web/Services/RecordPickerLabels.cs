using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>
/// Kayıt seçicide (<c>RecordPicker</c>) kaydı tanıtan metin. Etiket alanı kayıt türü beyanından gelir (ör. fırsat için "title");
/// alan yoksa ya da bu kayıtta boşsa kısa kimlik gösterilir, böylece liste hiçbir zaman boş satırlarla dolmaz.
/// </summary>
public static class RecordPickerLabels
{
    public static string Label(RecordResponse record, string? labelField)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TextOf(record.Values, labelField) ?? ShortId(record.Id);
    }

    /// <summary>Alanın metin değeri; alan yoksa, metin değilse ya da boşsa <c>null</c>.</summary>
    public static string? TextOf(JsonObject? values, string? field)
    {
        if (values is null || string.IsNullOrWhiteSpace(field) || values[field] is not JsonValue value)
        {
            return null;
        }

        var text = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Kimliğin ilk sekiz karakteri ("Kayıt 3f9a1c2e").</summary>
    public static string ShortId(Guid id) => $"Kayıt {id.ToString("N")[..8]}";
}
