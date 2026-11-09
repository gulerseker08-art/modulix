using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Modulix.Core.Contracts.Automation;

// Kural ve iş akışı altyapısının koşul ve tetikleyici modeli (ADR-0027). Tipli ve doğrulanan ağaçtır; serbest SQL, C#, betik
// veya çalıştırılabilir ifade içermez. Kural sürümlerinde JSON olarak saklanır.

/// <summary>Alanın (ve aksiyon parametresinin) veri tipi; hangi operatörlerin geçerli olduğunu belirler.</summary>
public enum AutomationFieldType
{
    Text = 1,
    Number = 2,

    /// <summary>Tutar + para birimi. Farklı para birimleri karşılaştırılmaz, toplanmaz ve dönüştürülmez.</summary>
    Money = 3,

    Boolean = 4,

    /// <summary>Takvim günü (tenant saat diliminde).</summary>
    Date = 5,

    /// <summary>UTC anı.</summary>
    DateTime = 6,

    /// <summary>Kalıcı seçenek anahtarı.</summary>
    Choice = 7,

    /// <summary>Başka kaydın kalıcı kimliği (<see cref="Guid"/>); görünen metin karşılaştırılmaz (ADR-0016).</summary>
    Lookup = 8,
}

public enum AutomationOperator
{
    Eq = 1,
    Ne = 2,
    Gt = 3,
    Gte = 4,
    Lt = 5,
    Lte = 6,
    Between = 7,
    Contains = 8,
    StartsWith = 9,
    In = 10,
    NotIn = 11,
    IsEmpty = 12,
    IsNotEmpty = 13,
    Before = 14,
    After = 15,

    /// <summary>Bugünden itibaren N gün içinde (bugün dahil).</summary>
    WithinNextDays = 16,

    /// <summary>Son N gün içinde (bugün dahil).</summary>
    WithinLastDays = 17,

    /// <summary>Bugünden en az N gün önce.</summary>
    OlderThanDays = 18,

    /// <summary>Alan bu olayda değişti (yalnızca değişiklik bilgisi olan tetikleyicilerde).</summary>
    Changed = 19,

    /// <summary>Alan değişti ve yeni değeri verilen değer.</summary>
    ChangedTo = 20,

    /// <summary>Alan değişti ve eski değeri verilen değer.</summary>
    ChangedFrom = 21,
}

/// <summary>Koşulda alanın hangi değerine bakılır. Eski değer yalnızca olayın farkı varsa bilinir.</summary>
public enum AutomationValueSource
{
    New = 1,
    Old = 2,
}

public enum ConditionLogic
{
    And = 1,
    Or = 2,
}

public enum RelationQuantifier
{
    Exists = 1,
    NotExists = 2,
    Count = 3,
}

public static class AutomationOperators
{
    private static readonly AutomationOperator[] Change = [AutomationOperator.Changed, AutomationOperator.ChangedTo, AutomationOperator.ChangedFrom];

    private static readonly Dictionary<AutomationFieldType, AutomationOperator[]> ByType = new()
    {
        [AutomationFieldType.Text] =
        [
            AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.Contains, AutomationOperator.StartsWith,
            AutomationOperator.In, AutomationOperator.NotIn, AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.Number] =
        [
            AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.Gt, AutomationOperator.Gte, AutomationOperator.Lt,
            AutomationOperator.Lte, AutomationOperator.Between, AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.Money] =
        [
            AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.Gt, AutomationOperator.Gte, AutomationOperator.Lt,
            AutomationOperator.Lte, AutomationOperator.Between, AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.Boolean] = [AutomationOperator.Eq],
        [AutomationFieldType.Date] =
        [
            AutomationOperator.Before, AutomationOperator.After, AutomationOperator.Between, AutomationOperator.WithinNextDays,
            AutomationOperator.WithinLastDays, AutomationOperator.OlderThanDays, AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.DateTime] =
        [
            AutomationOperator.Before, AutomationOperator.After, AutomationOperator.Between, AutomationOperator.WithinNextDays,
            AutomationOperator.WithinLastDays, AutomationOperator.OlderThanDays, AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.Choice] =
        [
            AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.In, AutomationOperator.NotIn,
            AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
        [AutomationFieldType.Lookup] =
        [
            AutomationOperator.Eq, AutomationOperator.Ne, AutomationOperator.In, AutomationOperator.NotIn,
            AutomationOperator.IsEmpty, AutomationOperator.IsNotEmpty,
        ],
    };

    /// <summary>Tipte geçerli operatörler (değişiklik operatörleri dahil).</summary>
    public static IReadOnlyList<AutomationOperator> For(AutomationFieldType type) => [.. ByType[type], .. Change];

    public static bool IsChange(AutomationOperator op) => op is AutomationOperator.Changed or AutomationOperator.ChangedTo or AutomationOperator.ChangedFrom;

    /// <summary>Operatörün istediği değer sayısı (en az, en çok).</summary>
    public static (int Min, int Max) ValueCount(AutomationOperator op) => op switch
    {
        AutomationOperator.IsEmpty or AutomationOperator.IsNotEmpty or AutomationOperator.Changed => (0, 0),
        AutomationOperator.Between => (2, 2),
        AutomationOperator.In or AutomationOperator.NotIn => (1, 50),
        _ => (1, 1),
    };
}

/// <summary>Koşul ağacının düğümü: grup, yaprak ya da sınırlı ilişki sorgusu.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ConditionLeaf), "leaf")]
[JsonDerivedType(typeof(ConditionRelation), "relation")]
public abstract record ConditionNode;

/// <summary>İç içe AND/OR grubu. Boş grup geçersizdir.</summary>
public sealed record ConditionGroup(ConditionLogic Logic, IReadOnlyList<ConditionNode> Items) : ConditionNode;

/// <summary>
/// Tek koşul. <see cref="Field"/> alan anahtarıdır (<see cref="AutomationFieldDefinition.Key"/>). Değerler metin olarak gelir,
/// alan tipine göre çözülür (sayı: noktalı ondalık, tarih: yyyy-MM-dd, an: ISO 8601, lookup: kimlik). Para alanında
/// <see cref="Currency"/> zorunludur.
/// </summary>
public sealed record ConditionLeaf(string Field, AutomationOperator Operator, IReadOnlyList<string> Values) : ConditionNode
{
    public AutomationValueSource Source { get; init; } = AutomationValueSource.New;

    public string? Currency { get; init; }
}

/// <summary>
/// Modülün beyan ettiği ilişki üzerinde sınırlı sorgu (var/yok/sayı). <see cref="Where"/> yalnızca hedef kayıt türünün alanlarına
/// yaprak ve grup koyar; ikinci seviye ilişki yoktur. Sayı karşılaştırması için <see cref="CountOperator"/> Eq/Ne/Gt/Gte/Lt/Lte olur.
/// </summary>
public sealed record ConditionRelation(string Relation, RelationQuantifier Quantifier) : ConditionNode
{
    public ConditionNode? Where { get; init; }

    public AutomationOperator CountOperator { get; init; } = AutomationOperator.Gte;

    public int CountValue { get; init; } = 1;
}

/// <summary>Kuralı çalıştıran olay ya da zamanlama.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RecordCreatedTrigger), "recordCreated")]
[JsonDerivedType(typeof(FieldChangedTrigger), "fieldChanged")]
[JsonDerivedType(typeof(StatusTransitionTrigger), "statusTransition")]
[JsonDerivedType(typeof(ScheduledTrigger), "scheduled")]
[JsonDerivedType(typeof(DateRelativeTrigger), "dateRelative")]
[JsonDerivedType(typeof(InactivityTrigger), "inactivity")]
[JsonDerivedType(typeof(DomainEventTrigger), "domainEvent")]
[JsonDerivedType(typeof(ManualTrigger), "manual")]
public abstract record AutomationTrigger;

public sealed record RecordCreatedTrigger : AutomationTrigger;

/// <summary>Listedeki alanlardan biri değişince.</summary>
public sealed record FieldChangedTrigger(IReadOnlyList<string> Fields) : AutomationTrigger;

/// <summary>Seçim alanı <paramref name="From"/> değerinden (boşsa herhangi bir değerden) <paramref name="To"/> değerine geçince.</summary>
public sealed record StatusTransitionTrigger(string Field, string? From, string To) : AutomationTrigger;

/// <summary>Günlük tarama; <see cref="TimeOfDay"/> tenant saat diliminde "SS:dd".</summary>
public sealed record ScheduledTrigger(string TimeOfDay = "06:00") : AutomationTrigger;

/// <summary>Tarih alanından <see cref="OffsetDays"/> gün önce (negatif) ya da sonra (pozitif); tenant takvim günüyle.</summary>
public sealed record DateRelativeTrigger(string Field, int OffsetDays) : AutomationTrigger;

/// <summary>Modülün beyan ettiği ilişkide (ör. aktivite) <see cref="Days"/> gündür hareket yoksa.</summary>
public sealed record InactivityTrigger(string Relation, int Days) : AutomationTrigger;

/// <summary>Modülün beyan ettiği iş olayı (<see cref="AutomationEventDefinition.Key"/>).</summary>
public sealed record DomainEventTrigger(string EventKey) : AutomationTrigger;

/// <summary>Yetkili kullanıcının elle çalıştırması.</summary>
public sealed record ManualTrigger : AutomationTrigger;

/// <summary>Kuralın bir aksiyon adımı. <see cref="Id"/> sürüm içinde tekildir; tekilleştirme anahtarının parçasıdır.</summary>
public sealed record AutomationActionStep(string Id, string Action, JsonObject Parameters)
{
    /// <summary>
    /// Adım koşulu (akışta dallanma, ADR-0027 Dilim 4c-5): doluysa adım yalnızca bu koşul sağlanırsa çalışır, sağlanmazsa atlanır ve
    /// akış sıradaki adımla sürer. Adıma sıra geldiğinde kaydın <b>güncel</b> değerleriyle değerlendirilir (önceki adımların ve beklemenin
    /// ardından); bu yüzden eski değer ve değişiklik operatörleri kullanılamaz. Boşsa adım her zaman çalışır.
    /// </summary>
    public ConditionNode? When { get; init; }
}

/// <summary>Bir kural sürümünün içeriği: ne zaman, hangi koşulda, ne yapılır.</summary>
public sealed record AutomationDefinition(AutomationTrigger Trigger, ConditionNode? Condition, IReadOnlyList<AutomationActionStep> Actions);

/// <summary>Kural tanımının JSON biçimi (kural sürümlerinde saklanır, API'de gider gelir).</summary>
public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize(AutomationDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static string Serialize(ConditionNode node) => JsonSerializer.Serialize<ConditionNode>(node, Options);

    /// <summary>Tanımı çözer; biçim hatalıysa <see cref="FormatException"/> (iletisi kullanıcıya gösterilebilir).</summary>
    public static AutomationDefinition Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AutomationDefinition>(json, Options)
                ?? throw new FormatException("Kural tanımı boş.");
        }
        catch (JsonException)
        {
            throw new FormatException("Kural tanımı çözülemedi: biçim geçersiz.");
        }
    }

    public static AutomationDefinition Deserialize(JsonElement element) => Deserialize(element.GetRawText());
}
