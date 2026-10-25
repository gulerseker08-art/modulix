using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Customization;

namespace Modulix.Core.Records;

/// <summary>
/// Boru hattının özel alan adımları (Y9a). Değerler <see cref="RecordFields.Custom"/> nesnesinde gelir; her biri tanımına
/// göre doğrulanır ve kaydın mevcut özel alanlarıyla birleştirilir (gönderilmeyen alan korunur, null siler). Depo
/// birleşmiş nesnenin tamamını saklar. Arşivlenmiş alanın değeri kayıtta kalır ama okumada verilmez.
/// </summary>
internal static class RecordCustomFields
{
    /// <summary>
    /// Yazılacak değerler: <see cref="RecordFields.Custom"/> birleşmiş nesneyle değiştirilir. Güncellemede özel alan
    /// gönderilmemişse (veya null) dokunulmaz. Zorunlu alan oluştururken dolu olmalı, güncellemede boşaltılamaz.
    /// </summary>
    public static (JsonObject Values, List<RecordFieldError> Errors) Prepare(
        IReadOnlyList<CustomFieldInfo> fields, RecordOperation operation, JsonObject values, JsonObject? before)
    {
        var errors = new List<RecordFieldError>();
        var incoming = values.TryGetPropertyValue(RecordFields.Custom, out var node) ? node : null;
        if (incoming is null && operation == RecordOperation.Update)
        {
            return (Without(values), errors);
        }

        if (incoming is not null and not JsonObject)
        {
            errors.Add(new RecordFieldError(RecordFields.Custom, "Özel alanlar nesne olmalıdır."));
            return (values, errors);
        }

        var previous = before?[RecordFields.Custom] as JsonObject;
        var merged = previous?.DeepClone().AsObject() ?? [];
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var (key, value) in (JsonObject?)incoming ?? [])
        {
            if (!byKey.TryGetValue(key, out var field))
            {
                errors.Add(new RecordFieldError(RecordFields.CustomPrefix + key, "Bilinmeyen alan."));
                continue;
            }

            var (normalized, error) = CustomFieldRules.Normalize(field, value, previous?[key]);
            if (error is not null)
            {
                errors.Add(new RecordFieldError(RecordFields.CustomPrefix + key, error));
            }
            else if (normalized is null)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = normalized;
            }
        }

        var sent = (JsonObject?)incoming;
        var failed = errors.Select(e => e.Field).ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields.Where(f => f.Required && merged[f.Key] is null && !failed.Contains(RecordFields.CustomPrefix + f.Key)))
        {
            // Eski kayıt zorunlu alan eklenmeden önce açılmış olabilir: yalnızca boşaltma denemesi reddedilir.
            if (operation == RecordOperation.Create || sent?.ContainsKey(field.Key) == true)
            {
                errors.Add(new RecordFieldError(RecordFields.CustomPrefix + field.Key, "Bu alan zorunludur."));
            }
        }

        var result = Without(values);
        result[RecordFields.Custom] = merged;
        return (result, errors);
    }

    /// <summary>Okumada yalnızca etkin alanlar, tanım sırasıyla; özel alanı olmayan kayıtta boş nesne.</summary>
    public static RecordData Present(RecordData data, IReadOnlyList<CustomFieldInfo> fields)
    {
        var stored = data.Values[RecordFields.Custom] as JsonObject;
        var visible = new JsonObject();
        foreach (var field in fields)
        {
            if (stored?[field.Key] is { } value)
            {
                visible[field.Key] = value.DeepClone();
            }
        }

        var values = data.Values.DeepClone().AsObject();
        values[RecordFields.Custom] = visible;
        return data with { Values = values };
    }

    /// <summary>
    /// "custom.anahtar" filtrelerini ayırır. Yalnızca seçim tipli alanlarla süzülür; değer alanın seçeneklerinden biri
    /// olmalıdır (pasif seçenek de olur: eski kayıtlar bulunabilsin).
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Filters, IReadOnlyDictionary<string, string>? Custom, List<RecordFieldError> Errors) SplitFilters(
        IReadOnlyList<CustomFieldInfo> fields, IReadOnlyDictionary<string, string>? filters)
    {
        var errors = new List<RecordFieldError>();
        if (filters is null || !filters.Keys.Any(k => k.StartsWith(RecordFields.CustomPrefix, StringComparison.Ordinal)))
        {
            return (filters, null, errors);
        }

        var regular = new Dictionary<string, string>(StringComparer.Ordinal);
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in filters)
        {
            if (!name.StartsWith(RecordFields.CustomPrefix, StringComparison.Ordinal))
            {
                regular[name] = value;
                continue;
            }

            var key = name[RecordFields.CustomPrefix.Length..];
            var field = fields.FirstOrDefault(f => f.Key == key);
            if (field is null || field.Type != CustomFieldType.Choice)
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen filtre."));
            }
            else if (field.Options.All(o => o.Key != value))
            {
                errors.Add(new RecordFieldError(name, "Geçersiz filtre değeri."));
            }
            else
            {
                custom[key] = value;
            }
        }

        return (regular, custom, errors);
    }

    /// <summary>Denetim ve olay için: özel alanlar "custom.anahtar" adıyla ayrı alanlar olur (kayıt geçmişi alan alan gösterir).</summary>
    public static JsonObject? Flatten(JsonObject? values)
    {
        if (values?[RecordFields.Custom] is not JsonObject custom)
        {
            return values;
        }

        var flat = Without(values);
        foreach (var (key, value) in custom)
        {
            flat[RecordFields.CustomPrefix + key] = value?.DeepClone();
        }

        return flat;
    }

    private static JsonObject Without(JsonObject values)
    {
        var copy = values.DeepClone().AsObject();
        copy.Remove(RecordFields.Custom);
        return copy;
    }
}
