using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modulix.Core.Customization;

/// <summary>
/// Özel alan değerlerinin kuralları (Y9a). Değerin JSON hali tipe göredir: metin, sayı, "yyyy-MM-dd" tarih, true/false,
/// seçenek anahtarı ya da seçenek anahtarları dizisi. Boş değer (boş metin, boş dizi) null'a çevrilir.
/// </summary>
public static class CustomFieldRules
{
    public const int TextMaxLength = 500;
    public const int MultilineTextMaxLength = 4000;
    public const decimal NumberLimit = 1_000_000_000_000_000m;

    /// <summary>
    /// Değeri doğrular ve saklanacak hale getirir. Pasif seçenek yeni değer olarak verilemez; kayıtta zaten varsa
    /// (<paramref name="previous"/>) korunabilir.
    /// </summary>
    public static (JsonNode? Value, string? Error) Normalize(CustomFieldInfo field, JsonNode? node, JsonNode? previous = null)
    {
        if (node is null)
        {
            return (null, null);
        }

        switch (field.Type)
        {
            case CustomFieldType.Text or CustomFieldType.MultilineText:
            {
                if (!TryString(node, out var text))
                {
                    return (null, "Metin olmalıdır.");
                }

                var max = field.Type == CustomFieldType.Text ? TextMaxLength : MultilineTextMaxLength;
                text = text.Trim();
                return text.Length > max ? (null, $"En fazla {max} karakter olabilir.") : (text.Length == 0 ? null : text, null);
            }

            case CustomFieldType.Number:
            {
                // Değer int, double ya da JsonElement olarak gelebilir; JSON metninden okunur.
                if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
                    || !decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return (null, "Sayı olmalıdır.");
                }

                return Math.Abs(number) >= NumberLimit ? (null, "Sayı çok büyük.") : (number, null);
            }

            case CustomFieldType.Date:
            {
                if (!TryString(node, out var text))
                {
                    return (null, "Tarih olmalıdır.");
                }

                if (text.Length == 0)
                {
                    return (null, null);
                }

                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (null, "Tarih yyyy-aa-gg biçiminde olmalıdır.");
            }

            case CustomFieldType.YesNo:
                return node is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? (flag.GetValue<bool>(), null)
                    : (null, "Evet ya da hayır olmalıdır.");

            case CustomFieldType.Choice:
            {
                if (!TryString(node, out var key))
                {
                    return (null, "Bir seçenek olmalıdır.");
                }

                if (key.Length == 0)
                {
                    return (null, null);
                }

                var kept = previous is JsonValue old && old.TryGetValue<string>(out var oldKey) && oldKey == key;
                return Selectable(field, key, kept) ? (key, null) : (null, "Geçersiz seçenek.");
            }

            case CustomFieldType.MultiChoice:
            {
                if (node is not JsonArray array || !array.All(i => i is JsonValue v && v.TryGetValue<string>(out _)))
                {
                    return (null, "Seçenek listesi olmalıdır.");
                }

                var previousKeys = previous is JsonArray old
                    ? old.Select(i => i?.GetValue<string>()).ToHashSet()
                    : [];
                var keys = array.Select(i => i!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList();
                if (keys.Any(k => !Selectable(field, k, previousKeys.Contains(k))))
                {
                    return (null, "Geçersiz seçenek.");
                }

                // Seçenek sırasıyla saklanır; aynı seçim farklı sırayla gelince değişiklik sayılmaz.
                var ordered = field.Options.Select(o => o.Key).Where(keys.Contains).ToList();
                return ordered.Count == 0 ? (null, null) : (new JsonArray([.. ordered.Select(k => (JsonNode?)JsonValue.Create(k))]), null);
            }

            default:
                return (null, "Bilinmeyen alan tipi.");
        }
    }

    /// <summary>
    /// Ekranda görünen addan değişmez anahtar üretir (ör. "Araç tipi" → "arac_tipi"): Türkçe harfler sadeleşir, harf ve
    /// rakam dışındakiler alt çizgi olur. <paramref name="taken"/> içinde varsa sonuna sayı eklenir.
    /// </summary>
    public static string KeyFrom(string label, IReadOnlySet<string> taken, int maxLength = CustomField.KeyMaxLength)
    {
        var builder = new StringBuilder();
        foreach (var c in label.Trim().ToLowerInvariant())
        {
            var mapped = c switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => c,
            };
            if (mapped is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(mapped);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var key = builder.ToString().Trim('_');
        if (key.Length == 0)
        {
            key = "alan";
        }
        else if (char.IsDigit(key[0]))
        {
            key = "a_" + key;
        }

        key = key[..Math.Min(key.Length, maxLength)].TrimEnd('_');
        var candidate = key;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            var suffix = "_" + i.ToString(CultureInfo.InvariantCulture);
            candidate = key[..Math.Min(key.Length, maxLength - suffix.Length)] + suffix;
        }

        return candidate;
    }

    private static bool Selectable(CustomFieldInfo field, string key, bool alreadySet) =>
        field.Options.Any(o => o.Key == key && (o.Active || alreadySet));

    private static bool TryString(JsonNode node, out string text)
    {
        text = string.Empty;
        return node is JsonValue value && value.TryGetValue(out text!);
    }
}
