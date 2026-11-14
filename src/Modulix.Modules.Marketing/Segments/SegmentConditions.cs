using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Modules.Marketing.Contracts;

namespace Modulix.Modules.Marketing.Segments;

/// <summary>Koşul ağacındaki hata: <see cref="Path"/> örn. "items[1].values[0]".</summary>
public sealed record ConditionError(string Path, string Message);

/// <summary>
/// Segment koşul ağacı (ADR-0025 K4): ya bir grup (<c>op</c> = and/or, <c>items</c>) ya da bir koşul (<c>field</c>, <c>operator</c>,
/// <c>values</c>). Kullanıcı SQL ya da kod yazmaz; ağaç yalnızca kayıtlı alanlara, tipiyle uyumlu operatörlere ve metin değerlere
/// indirgenir.
/// </summary>
public sealed class SegmentCondition
{
    public const string And = "and";
    public const string Or = "or";

    /// <summary>"and" / "or" (grup) ya da null (koşul).</summary>
    public string? Group { get; init; }

    public IReadOnlyList<SegmentCondition> Items { get; init; } = [];

    public SegmentLeaf? Leaf { get; init; }

    public bool IsLeaf => Leaf is not null;
}

/// <summary>Koşul ağacının ayrıştırılması, doğrulanması ve kanonik yazımı. Saf sınıf: veritabanına dokunmaz.</summary>
public static class SegmentConditions
{
    public const int MaxDepth = 4;
    public const int MaxLeaves = 25;
    public const int MaxValueLength = 200;
    public const int MaxDays = 3650;

    // Türkçe karakterler olduğu gibi yazılır: taslak ve sürüm metni insan tarafından okunabilir kalır.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Metni ağaca çevirir. Biçim hatası, bilinmeyen anahtar, derinlik ve koşul sayısı sınırı burada denetlenir.</summary>
    public static (SegmentCondition? Condition, IReadOnlyList<ConditionError> Errors) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, [new ConditionError("", "Koşul boş olamaz.")]);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return (null, [new ConditionError("", "Koşul geçerli bir JSON değil.")]);
        }

        var errors = new List<ConditionError>();
        var leaves = 0;
        var condition = ParseNode(root, "", 1, errors, ref leaves);
        if (leaves > MaxLeaves)
        {
            errors.Add(new ConditionError("", $"En fazla {MaxLeaves} koşul kullanılabilir."));
        }

        return errors.Count == 0 ? (condition, []) : (null, errors);
    }

    /// <summary>
    /// Ağacı kayıtlı alanlara karşı doğrular: alan var mı (ve hedefe uygun mu), operatör tipe uygun mu, değer sayısı ve biçimi doğru mu.
    /// Yetkisiz ya da kaynağı olmayan alan "bulunamadı" ile aynı mesajı alır (alanın varlığı sızdırılmaz).
    /// </summary>
    public static IReadOnlyList<ConditionError> Validate(
        SegmentCondition condition, IReadOnlyDictionary<string, SegmentFieldDefinition> available, SegmentTarget target)
    {
        var errors = new List<ConditionError>();
        ValidateNode(condition, "", available, target, errors);
        return errors;
    }

    public static IEnumerable<SegmentLeaf> Leaves(SegmentCondition condition) =>
        condition.IsLeaf ? [condition.Leaf!] : condition.Items.SelectMany(Leaves);

    /// <summary>Kanonik JSON: aynı ağaç her zaman aynı metni verir (sürüm karşılaştırması ve saklama için).</summary>
    public static string Serialize(SegmentCondition condition) => ToNode(condition).ToJsonString(WriteOptions);

    public static JsonNode ToNode(SegmentCondition condition)
    {
        if (condition.Leaf is { } leaf)
        {
            return new JsonObject
            {
                ["field"] = leaf.Field,
                ["operator"] = leaf.Operator.ToString().ToLowerInvariant(),
                ["values"] = new JsonArray([.. leaf.Values.Select(v => (JsonNode?)JsonValue.Create(v))]),
            };
        }

        return new JsonObject
        {
            ["op"] = condition.Group,
            ["items"] = new JsonArray([.. condition.Items.Select(ToNode)]),
        };
    }

    private static SegmentCondition? ParseNode(JsonNode? node, string path, int depth, List<ConditionError> errors, ref int leaves)
    {
        if (node is not JsonObject obj)
        {
            errors.Add(new ConditionError(path, "Koşul bir nesne olmalıdır."));
            return null;
        }

        if (depth > MaxDepth)
        {
            errors.Add(new ConditionError(path, $"İç içe grup derinliği en fazla {MaxDepth} olabilir."));
            return null;
        }

        var isGroup = obj.ContainsKey("op") || obj.ContainsKey("items");
        var isLeaf = obj.ContainsKey("field") || obj.ContainsKey("operator") || obj.ContainsKey("values");
        if (isGroup == isLeaf)
        {
            errors.Add(new ConditionError(path, "Düğüm ya da grup (op, items) ya da koşul (field, operator, values) olmalıdır."));
            return null;
        }

        var allowed = isGroup ? new[] { "op", "items" } : ["field", "operator", "values"];
        foreach (var key in obj.Select(p => p.Key).Where(k => !allowed.Contains(k)))
        {
            errors.Add(new ConditionError(Join(path, key), "Bilinmeyen anahtar."));
        }

        return isGroup ? ParseGroup(obj, path, depth, errors, ref leaves) : ParseLeaf(obj, path, errors, ref leaves);
    }

    private static SegmentCondition? ParseGroup(JsonObject obj, string path, int depth, List<ConditionError> errors, ref int leaves)
    {
        var op = obj["op"] is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim().ToLowerInvariant() : null;
        if (op is not (SegmentCondition.And or SegmentCondition.Or))
        {
            errors.Add(new ConditionError(Join(path, "op"), "Grup operatörü 'and' ya da 'or' olmalıdır."));
        }

        if (obj["items"] is not JsonArray array || array.Count == 0)
        {
            errors.Add(new ConditionError(Join(path, "items"), "Grup en az bir koşul içermelidir."));
            return null;
        }

        var items = new List<SegmentCondition>();
        for (var i = 0; i < array.Count; i++)
        {
            if (ParseNode(array[i], $"{Join(path, "items")}[{i}]", depth + 1, errors, ref leaves) is { } child)
            {
                items.Add(child);
            }
        }

        return op is SegmentCondition.And or SegmentCondition.Or ? new SegmentCondition { Group = op, Items = items } : null;
    }

    private static SegmentCondition? ParseLeaf(JsonObject obj, string path, List<ConditionError> errors, ref int leaves)
    {
        leaves++;
        var field = obj["field"] is JsonValue f && f.TryGetValue<string>(out var fieldText) ? fieldText.Trim() : null;
        if (string.IsNullOrEmpty(field))
        {
            errors.Add(new ConditionError(Join(path, "field"), "Alan zorunludur."));
        }

        SegmentOperator? op = null;
        if (obj["operator"] is JsonValue o && o.TryGetValue<string>(out var opText)
            && Enum.TryParse<SegmentOperator>(opText.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsDigit(opText.Trim()[0]))
        {
            op = parsed;
        }
        else
        {
            errors.Add(new ConditionError(Join(path, "operator"), "Geçersiz operatör."));
        }

        var values = new List<string>();
        if (obj["values"] is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var s))
                {
                    values.Add(s.Trim());
                }
                else if (array[i] is JsonValue number && number.TryGetValue<decimal>(out var d))
                {
                    values.Add(d.ToString(CultureInfo.InvariantCulture));
                }
                else if (array[i] is JsonValue flag && flag.TryGetValue<bool>(out var b))
                {
                    values.Add(b ? "true" : "false");
                }
                else
                {
                    errors.Add(new ConditionError($"{Join(path, "values")}[{i}]", "Değer metin, sayı ya da evet/hayır olmalıdır."));
                }
            }
        }
        else if (obj.ContainsKey("values") && obj["values"] is not null)
        {
            errors.Add(new ConditionError(Join(path, "values"), "Değerler bir liste olmalıdır."));
        }

        return string.IsNullOrEmpty(field) || op is null
            ? null
            : new SegmentCondition { Leaf = new SegmentLeaf(field, op.Value, values) };
    }

    private static void ValidateNode(
        SegmentCondition node, string path, IReadOnlyDictionary<string, SegmentFieldDefinition> available, SegmentTarget target, List<ConditionError> errors)
    {
        if (!node.IsLeaf)
        {
            for (var i = 0; i < node.Items.Count; i++)
            {
                ValidateNode(node.Items[i], $"{Join(path, "items")}[{i}]", available, target, errors);
            }

            return;
        }

        var leaf = node.Leaf!;
        if (!available.TryGetValue(leaf.Field, out var field) || field.Target != target)
        {
            errors.Add(new ConditionError(Join(path, "field"), "Alan bulunamadı ya da bu segment türünde kullanılamıyor."));
            return;
        }

        if (!SegmentOperators.For(field.Type).Contains(leaf.Operator))
        {
            errors.Add(new ConditionError(Join(path, "operator"), $"'{field.Label}' alanında bu operatör kullanılamaz."));
            return;
        }

        var (min, max) = SegmentOperators.ValueCount(leaf.Operator);
        if (leaf.Values.Count < min || leaf.Values.Count > max)
        {
            errors.Add(new ConditionError(Join(path, "values"), min == max ? $"Bu operatör {min} değer ister." : $"Bu operatör {min}-{max} değer ister."));
            return;
        }

        var errorsBefore = errors.Count;
        for (var i = 0; i < leaf.Values.Count; i++)
        {
            if (ValueError(field, leaf.Operator, leaf.Values[i]) is { } message)
            {
                errors.Add(new ConditionError($"{Join(path, "values")}[{i}]", message));
            }
        }

        if (leaf.Operator == SegmentOperator.Between && errors.Count == errorsBefore && field.Type is SegmentFieldType.Date or SegmentFieldType.Number
            && Compare(field.Type, leaf.Values[0], leaf.Values[1]) > 0)
        {
            errors.Add(new ConditionError(Join(path, "values"), "Başlangıç değeri bitişten büyük olamaz."));
        }
    }

    private static string? ValueError(SegmentFieldDefinition field, SegmentOperator op, string value)
    {
        if (value.Length is 0 or > MaxValueLength)
        {
            return $"Değer 1-{MaxValueLength} karakter olmalıdır.";
        }

        if (field.Type == SegmentFieldType.Relation)
        {
            return IsDays(value) ? null : $"Pencere 1-{MaxDays} arası gün sayısı olmalıdır.";
        }

        return field.Type switch
        {
            SegmentFieldType.Text => null,
            SegmentFieldType.Choice => field.Options.Count == 0 || field.Options.ContainsKey(value) ? null : "Listede olmayan değer.",
            SegmentFieldType.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? null : "Sayı olmalıdır.",
            SegmentFieldType.Bool => value is "true" or "false" ? null : "true ya da false olmalıdır.",
            SegmentFieldType.Id => Guid.TryParse(value, out var id) && id != Guid.Empty ? null : "Geçerli bir kimlik olmalıdır.",
            SegmentFieldType.Date when op is SegmentOperator.LastDays or SegmentOperator.NextDays => IsDays(value) ? null : $"Gün sayısı 1-{MaxDays} arası olmalıdır.",
            SegmentFieldType.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Tarih yyyy-AA-gg biçiminde olmalıdır.",
            _ => "Desteklenmeyen alan tipi.",
        };
    }

    private static bool IsDays(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= MaxDays;

    private static int Compare(SegmentFieldType type, string a, string b) => type == SegmentFieldType.Date
        ? DateOnly.ParseExact(a, "yyyy-MM-dd", CultureInfo.InvariantCulture).CompareTo(DateOnly.ParseExact(b, "yyyy-MM-dd", CultureInfo.InvariantCulture))
        : decimal.Parse(a, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b, CultureInfo.InvariantCulture));

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
