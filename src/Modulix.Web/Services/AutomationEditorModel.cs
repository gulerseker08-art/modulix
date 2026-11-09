using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modulix.Web.Services;

/// <summary>
/// Otomasyon kural düzenleyicisinin (ADR-0027, Dilim 5c) düzenlenebilir modeli. Kural tanımı sunucuda tipli bir ağaçtır ve JSON
/// olarak gidip gelir; Web yalnızca <c>Modulix.Shared</c>'e başvurduğu için burada <see cref="JsonNode"/> ile okunur ve yazılır.
/// Doğrulama (alan var mı, operatör tipe uyar mı, izin) sunucudadır; bu sınıf yalnızca biçimi kurar. Düzenleyicinin gösteremediği
/// parçalar (iç içe koşul, ilişki koşulu, iş olayı tetikleyicisi, alan bağlamalı parametre) <b>silinmez</b>: ham haliyle korunur.
/// </summary>
public sealed class EditorDefinition
{
    public EditorTrigger Trigger { get; set; } = new();

    public EditorCondition Condition { get; set; } = new();

    public List<EditorAction> Actions { get; set; } = [];

    /// <summary>Kaydetmek için gönderilecek tanım (<c>trigger</c>, <c>condition</c>, <c>actions</c>).</summary>
    public JsonObject Build()
    {
        var root = new JsonObject { ["trigger"] = Trigger.Build() };
        if (Condition.Build() is { } condition)
        {
            root["condition"] = condition;
        }

        root["actions"] = new JsonArray([.. Actions.Select(a => (JsonNode?)a.Build())]);
        return root;
    }

    /// <summary>Sunucudan gelen tanımı düzenleyici modeline çevirir; tanınmayan bölümler ham olarak korunur.</summary>
    public static EditorDefinition Parse(JsonElement definition)
    {
        var root = JsonNode.Parse(definition.GetRawText()) as JsonObject ?? new JsonObject();
        return new EditorDefinition
        {
            Trigger = EditorTrigger.Parse(root["trigger"] as JsonObject),
            Condition = EditorCondition.Parse(root["condition"]),
            Actions = [.. (root["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(EditorAction.Parse)],
        };
    }

    /// <summary>Yeni adım için sürüm içinde tekil kimlik (<c>adim1</c>, <c>adim2</c>…).</summary>
    public string NextStepId()
    {
        for (var n = Actions.Count + 1; ; n++)
        {
            var id = $"adim{n}";
            if (Actions.All(a => a.Id != id))
            {
                return id;
            }
        }
    }
}

public sealed class EditorTrigger
{
    public const string RecordCreated = "recordCreated";
    public const string FieldChanged = "fieldChanged";
    public const string StatusTransition = "statusTransition";
    public const string Scheduled = "scheduled";
    public const string DateRelative = "dateRelative";
    public const string Inactivity = "inactivity";
    public const string Manual = "manual";
    public const string DomainEvent = "domainEvent";

    /// <summary>Düzenleyicinin sunduğu tetikleyici türleri (iş olayı ADR-0039 ile yayınlanabilir hâle geldi).</summary>
    public static IReadOnlyList<string> Types { get; } =
        [RecordCreated, FieldChanged, StatusTransition, DomainEvent, Scheduled, DateRelative, Inactivity, Manual];

    public string Type { get; set; } = Manual;

    /// <summary><see cref="FieldChanged"/>: izlenen alanlar.</summary>
    public List<string> Fields { get; set; } = [];

    /// <summary><see cref="StatusTransition"/> ve <see cref="DateRelative"/>: alan anahtarı.</summary>
    public string Field { get; set; } = string.Empty;

    public string? From { get; set; }

    public string To { get; set; } = string.Empty;

    public string TimeOfDay { get; set; } = "06:00";

    /// <summary>Tarihe göre: negatif gün tarihten önce, pozitif sonra.</summary>
    public int OffsetDays { get; set; }

    public string Relation { get; set; } = string.Empty;

    public int Days { get; set; } = 7;

    /// <summary><see cref="DomainEvent"/>: modülün beyan ettiği iş olayının anahtarı (ör. "crm.opportunity.won").</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Düzenleyicinin tanımadığı tetikleyici: ham olarak korunur ve değiştirilemez.</summary>
    public JsonObject? Raw { get; set; }

    public bool IsLocked => Raw is not null;

    /// <summary>Koşulda eski değer ve değişiklik operatörleri yalnızca kayıt güncelleme tetikleyicilerinde geçerlidir.</summary>
    public bool HasChanges => Type is FieldChanged or StatusTransition;

    /// <summary>Tarama tabanlı tetikleyiciler (kayıt kaynağı olan kayıt türü ister).</summary>
    public bool IsScan => Type is Scheduled or DateRelative or Inactivity;

    public static EditorTrigger Parse(JsonObject? node)
    {
        var type = node?["type"]?.GetValue<string>();
        var trigger = new EditorTrigger { Type = type ?? Manual };
        if (node is null)
        {
            return trigger;
        }

        switch (type)
        {
            case RecordCreated or Manual:
                break;
            case FieldChanged:
                trigger.Fields = [.. (node["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>() ?? string.Empty)];
                break;
            case StatusTransition:
                trigger.Field = node["field"]?.GetValue<string>() ?? string.Empty;
                trigger.From = node["from"]?.GetValue<string>();
                trigger.To = node["to"]?.GetValue<string>() ?? string.Empty;
                break;
            case Scheduled:
                trigger.TimeOfDay = node["timeOfDay"]?.GetValue<string>() ?? "06:00";
                break;
            case DateRelative:
                trigger.Field = node["field"]?.GetValue<string>() ?? string.Empty;
                trigger.OffsetDays = node["offsetDays"]?.GetValue<int>() ?? 0;
                break;
            case Inactivity:
                trigger.Relation = node["relation"]?.GetValue<string>() ?? string.Empty;
                trigger.Days = node["days"]?.GetValue<int>() ?? 7;
                break;
            case DomainEvent:
                trigger.EventKey = node["eventKey"]?.GetValue<string>() ?? string.Empty;
                break;
            default:
                trigger.Raw = (JsonObject)node.DeepClone();
                break;
        }

        return trigger;
    }

    public JsonObject Build()
    {
        if (Raw is not null)
        {
            return (JsonObject)Raw.DeepClone();
        }

        var node = new JsonObject { ["type"] = Type };
        switch (Type)
        {
            case FieldChanged:
                node["fields"] = new JsonArray([.. Fields.Select(f => (JsonNode?)JsonValue.Create(f))]);
                break;
            case StatusTransition:
                node["field"] = Field;
                if (!string.IsNullOrWhiteSpace(From))
                {
                    node["from"] = From;
                }

                node["to"] = To;
                break;
            case Scheduled:
                node["timeOfDay"] = TimeOfDay;
                break;
            case DateRelative:
                node["field"] = Field;
                node["offsetDays"] = OffsetDays;
                break;
            case Inactivity:
                node["relation"] = Relation;
                node["days"] = Days;
                break;
            case DomainEvent:
                node["eventKey"] = EventKey;
                break;
        }

        return node;
    }

    /// <summary>Tür değişince önceki türün değerleri taşınmaz; yeni tür varsayılanlarıyla başlar.</summary>
    public void ChangeType(string type)
    {
        Type = type;
        Raw = null;
        Fields = [];
        Field = string.Empty;
        From = null;
        To = string.Empty;
        TimeOfDay = "06:00";
        OffsetDays = 0;
        Relation = string.Empty;
        Days = 7;
        EventKey = string.Empty;
    }
}

public sealed class EditorCondition
{
    public const string And = "and";
    public const string Or = "or";

    public string Logic { get; set; } = And;

    public List<EditorLeaf> Leaves { get; set; } = [];

    /// <summary>
    /// Düzenleyicinin gösteremediği koşul (iç içe grup ya da ilişki sorgusu): ham olarak korunur, düzenlenemez.
    /// Kullanıcı isterse koşulu silip yeniden kurabilir.
    /// </summary>
    public JsonNode? Raw { get; set; }

    public bool IsLocked => Raw is not null;

    public static EditorCondition Parse(JsonNode? node)
    {
        var condition = new EditorCondition();
        switch (node)
        {
            case null:
                return condition;
            case JsonObject { } obj when obj["type"]?.GetValue<string>() == "leaf":
                condition.Leaves = [EditorLeaf.Parse(obj)];
                return condition;
            case JsonObject { } obj when obj["type"]?.GetValue<string>() == "group":
                var items = (obj["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (items.All(i => i["type"]?.GetValue<string>() == "leaf"))
                {
                    condition.Logic = obj["logic"]?.GetValue<string>() == Or ? Or : And;
                    condition.Leaves = [.. items.Select(EditorLeaf.Parse)];
                    return condition;
                }

                break;
        }

        condition.Raw = node.DeepClone();
        return condition;
    }

    /// <summary>Koşul yoksa <c>null</c>; tek yaprak da grup olarak yazılır (anlamı aynıdır).</summary>
    public JsonNode? Build()
    {
        if (Raw is not null)
        {
            return Raw.DeepClone();
        }

        if (Leaves.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["type"] = "group",
            ["logic"] = Logic,
            ["items"] = new JsonArray([.. Leaves.Select(l => (JsonNode?)l.Build())]),
        };
    }
}

public sealed class EditorLeaf
{
    public string Field { get; set; } = string.Empty;

    /// <summary>Operatörün JSON adı (<c>eq</c>, <c>in</c>, <c>withinNextDays</c>…).</summary>
    public string Operator { get; set; } = "eq";

    public List<string> Values { get; set; } = [];

    /// <summary><c>new</c> (yeni değer) ya da <c>old</c> (eski değer; yalnızca güncelleme tetikleyicilerinde).</summary>
    public string Source { get; set; } = "new";

    public string? Currency { get; set; }

    /// <summary>Sunucu sözleşmesindeki operatör adından (<c>Eq</c>) JSON adı (<c>eq</c>).</summary>
    public static string OperatorKey(string serverName) =>
        serverName.Length == 0 ? serverName : char.ToLowerInvariant(serverName[0]) + serverName[1..];

    public static EditorLeaf Parse(JsonObject node) => new()
    {
        Field = node["field"]?.GetValue<string>() ?? string.Empty,
        Operator = node["operator"]?.GetValue<string>() ?? "eq",
        Values = [.. (node["values"] as JsonArray ?? []).Select(v => v?.ToString() ?? string.Empty)],
        Source = node["source"]?.GetValue<string>() ?? "new",
        Currency = node["currency"]?.GetValue<string>(),
    };

    public JsonObject Build()
    {
        var node = new JsonObject
        {
            ["type"] = "leaf",
            ["field"] = Field,
            ["operator"] = Operator,
            ["values"] = new JsonArray([.. Values.Select(v => (JsonNode?)JsonValue.Create(v))]),
        };
        if (Source == "old")
        {
            node["source"] = "old";
        }

        if (!string.IsNullOrWhiteSpace(Currency))
        {
            node["currency"] = Currency;
        }

        return node;
    }

    /// <summary>Operatörün istediği değer sayısı (en az, en çok); sunucudaki <c>AutomationOperators.ValueCount</c> ile aynıdır.</summary>
    public static (int Min, int Max) ValueCount(string op) => op switch
    {
        "isEmpty" or "isNotEmpty" or "changed" => (0, 0),
        "between" => (2, 2),
        "in" or "notIn" => (1, 50),
        _ => (1, 1),
    };

    public static bool IsChange(string op) => op is "changed" or "changedTo" or "changedFrom";
}

public sealed class EditorAction
{
    public string Id { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    /// <summary>Parametre adı → değer (sabit değer ya da <c>{"$field": …}</c> bağlaması). Bilinmeyen parametreler de korunur.</summary>
    public JsonObject Parameters { get; set; } = [];

    /// <summary>Adım koşulu (dallanma, Dilim 4c-5): boşsa adım her zaman çalışır; doluysa yalnızca koşul sağlanınca.</summary>
    public EditorCondition When { get; set; } = new();

    public static EditorAction Parse(JsonObject node) => new()
    {
        Id = node["id"]?.GetValue<string>() ?? string.Empty,
        Action = node["action"]?.GetValue<string>() ?? string.Empty,
        Parameters = node["parameters"] is JsonObject p ? (JsonObject)p.DeepClone() : [],
        When = EditorCondition.Parse(node["when"]),
    };

    public JsonObject Build()
    {
        var node = new JsonObject
        {
            ["id"] = Id,
            ["action"] = Action,
            ["parameters"] = Parameters.DeepClone(),
        };
        if (When.Build() is { } when)
        {
            node["when"] = when;
        }

        return node;
    }

    /// <summary>Parametre alan bağlamasıyla mı verilmiş (<c>{"$field": "anahtar"}</c>)? Bağlanan alanın anahtarını döndürür.</summary>
    public string? BoundField(string name) =>
        Parameters[name] is JsonObject bound ? bound["$field"]?.GetValue<string>() : null;

    /// <summary>Sabit değerin metin hali; yoksa ya da bağlamaysa boş.</summary>
    public string ValueText(string name) => Parameters[name] switch
    {
        JsonValue value => value.ToString(),
        _ => string.Empty,
    };

    /// <summary>
    /// Parametreye sabit değer yazar. Boş metin parametreyi kaldırır. Sayı tipinde tam sayı <c>long</c>, ondalık <c>decimal</c>
    /// yazılır (nokta ayraçlı); çözülemeyen metin olduğu gibi gönderilir ve sunucu doğrulaması iletir.
    /// </summary>
    public void SetValue(string name, string type, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Parameters.Remove(name);
            return;
        }

        Parameters[name] = type switch
        {
            "Number" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) => JsonValue.Create(whole),
            "Number" when decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var dec) => JsonValue.Create(dec),
            "Boolean" when bool.TryParse(text, out var flag) => JsonValue.Create(flag),
            _ => JsonValue.Create(text),
        };
    }
}
