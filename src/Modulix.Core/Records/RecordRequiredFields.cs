using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Records;

/// <summary>
/// Firmanın form düzeniyle zorunlu kıldığı alanların denetimi (ADR-0021). Kodun zorunlu tuttuğu alanları kayıt deposu
/// doğrular; buradakiler yalnızca firmanın eklediği zorunluluklardır. Oluştururken alan dolu olmalıdır; güncellemede
/// yalnızca gönderilip boşaltılan alan reddedilir (eski kayıt zorunluluk konmadan önce açılmış olabilir).
/// Özel alanlar <c>custom.{anahtar}</c> adıyla gelir ve birleşmiş özel alan nesnesinde aranır.
/// </summary>
internal static class RecordRequiredFields
{
    public static IEnumerable<RecordFieldError> Check(IReadOnlySet<string> required, RecordOperation operation, JsonObject values)
    {
        foreach (var key in required)
        {
            var (sent, node) = Find(values, key);
            if (operation == RecordOperation.Create ? IsEmpty(node) : sent && IsEmpty(node))
            {
                yield return new RecordFieldError(key, "Bu alan zorunludur.");
            }
        }
    }

    private static (bool Sent, JsonNode? Node) Find(JsonObject values, string key)
    {
        if (key.StartsWith(RecordFields.CustomPrefix, StringComparison.Ordinal))
        {
            var custom = values[RecordFields.Custom] as JsonObject;
            var name = key[RecordFields.CustomPrefix.Length..];
            return custom is not null && custom.ContainsKey(name) ? (true, custom[name]) : (false, null);
        }

        return values.ContainsKey(key) ? (true, values[key]) : (false, null);
    }

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetValue<string>()),
        _ => false,
    };
}
