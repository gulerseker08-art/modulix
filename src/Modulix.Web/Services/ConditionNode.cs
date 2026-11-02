using System.Text.Json.Nodes;

namespace Modulix.Web.Services;

/// <summary>
/// Koşul düzenleyicisinin çalışma modeli: bir grup (<see cref="Group"/> "and"/"or") ya da bir koşul. Sunucudaki koşul ağacıyla aynı
/// biçime çevrilir (<see cref="ToJson"/>); doğrulama sunucudadır, burada yalnızca düzenleme kolaylığı vardır.
/// </summary>
public sealed class ConditionNode
{
    public string? Group { get; set; }

    public List<ConditionNode> Items { get; } = [];

    public string Field { get; set; } = string.Empty;

    public string Operator { get; set; } = string.Empty;

    public List<string> Values { get; } = [];

    public bool IsGroup => Group is not null;

    public static ConditionNode NewGroup(string kind = "and") => new() { Group = kind };

    public static ConditionNode NewLeaf() => new();

    /// <summary>"listedekilerden biri" için virgülle ayrılmış metin.</summary>
    public string ListText
    {
        get => string.Join(", ", Values);
        set
        {
            Values.Clear();
            Values.AddRange(value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    public string ValueAt(int index) => index < Values.Count ? Values[index] : string.Empty;

    public void SetValue(int index, string value)
    {
        while (Values.Count <= index) { Values.Add(string.Empty); }
        Values[index] = value;
    }

    public JsonObject ToJson()
    {
        if (!IsGroup)
        {
            return new JsonObject
            {
                ["field"] = Field,
                ["operator"] = Operator,
                ["values"] = new JsonArray([.. Values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (JsonNode?)JsonValue.Create(v.Trim()))]),
            };
        }

        return new JsonObject { ["op"] = Group, ["items"] = new JsonArray([.. Items.Select(i => (JsonNode?)i.ToJson())]) };
    }

    /// <summary>Kayıtlı taslak JSON'unu modele çevirir; okunamıyorsa null (düzenleyici boş bir grupla başlar).</summary>
    public static ConditionNode? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try { return FromNode(JsonNode.Parse(json)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static ConditionNode? FromNode(JsonNode? node)
    {
        if (node is not JsonObject obj) { return null; }
        if (obj["op"] is JsonValue op && op.TryGetValue<string>(out var group))
        {
            var result = NewGroup(group);
            if (obj["items"] is JsonArray items)
            {
                foreach (var item in items) { if (FromNode(item) is { } child) { result.Items.Add(child); } }
            }

            return result;
        }

        var leaf = NewLeaf();
        leaf.Field = obj["field"]?.GetValue<string>() ?? string.Empty;
        leaf.Operator = obj["operator"]?.GetValue<string>() ?? string.Empty;
        if (obj["values"] is JsonArray values) { leaf.Values.AddRange(values.Select(v => v?.ToString() ?? string.Empty)); }
        return leaf;
    }
}
