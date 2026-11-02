namespace Modulix.Core.Contracts.Marketing;

// Segment alan kaynağı sözleşmesi (ADR-0025, K3). Pazarlama modülü başka modüllerin tablolarına dokunmaz; bir modül segmentte
// kullanılabilecek alanlarını bu sözleşmeyle bildirir ve "bu koşulu sağlayan hedef kimlikleri" bir sorgu olarak döndürür.
// Segment motoru koşul ağacını bu sorguların kümeler cebrine çevirir (AND = kesişim, OR = birleşim).

/// <summary>Segmentin hedef kayıt türü. Hedef kimliği: firma → ortak taraf (<c>core.Parties</c>) kimliği, kişi → ortak kişi kimliği, lead → lead kimliği.</summary>
public enum SegmentTarget
{
    Company = 1,
    Person = 2,
    Lead = 3,
}

public enum SegmentFieldType
{
    Text = 1,
    Choice = 2,
    Number = 3,
    Date = 4,
    Bool = 5,
    Id = 6,

    /// <summary>İlişkili kayıt var/yok (isteğe bağlı gün penceresiyle).</summary>
    Relation = 7,
}

/// <summary>Koşul operatörleri. Hangi tipte hangisinin geçerli olduğu <see cref="SegmentOperators"/>'ta tanımlıdır.</summary>
public enum SegmentOperator
{
    Eq = 1,
    Ne = 2,
    Contains = 3,
    In = 4,
    Between = 5,
    Empty = 6,
    NotEmpty = 7,

    /// <summary>Son N gün (bugün dahil; tenant saat diliminde gün sınırı).</summary>
    LastDays = 8,

    /// <summary>Bugünden itibaren N gün içinde (bugün dahil).</summary>
    NextDays = 9,

    Before = 10,
    After = 11,
    Exists = 12,
    NotExists = 13,
}

public static class SegmentOperators
{
    private static readonly Dictionary<SegmentFieldType, SegmentOperator[]> ByType = new()
    {
        [SegmentFieldType.Text] = [SegmentOperator.Eq, SegmentOperator.Ne, SegmentOperator.Contains, SegmentOperator.In, SegmentOperator.Empty, SegmentOperator.NotEmpty],
        [SegmentFieldType.Choice] = [SegmentOperator.Eq, SegmentOperator.Ne, SegmentOperator.In, SegmentOperator.Empty, SegmentOperator.NotEmpty],
        [SegmentFieldType.Number] = [SegmentOperator.Eq, SegmentOperator.Ne, SegmentOperator.Between, SegmentOperator.Empty, SegmentOperator.NotEmpty],
        [SegmentFieldType.Date] =
        [
            SegmentOperator.Between, SegmentOperator.Before, SegmentOperator.After, SegmentOperator.LastDays, SegmentOperator.NextDays,
            SegmentOperator.Empty, SegmentOperator.NotEmpty,
        ],
        [SegmentFieldType.Bool] = [SegmentOperator.Eq],
        [SegmentFieldType.Id] = [SegmentOperator.Eq, SegmentOperator.In, SegmentOperator.Empty, SegmentOperator.NotEmpty],
        [SegmentFieldType.Relation] = [SegmentOperator.Exists, SegmentOperator.NotExists],
    };

    public static IReadOnlyList<SegmentOperator> For(SegmentFieldType type) => ByType[type];

    /// <summary>Operatörün istediği değer sayısı (en az, en çok).</summary>
    public static (int Min, int Max) ValueCount(SegmentOperator op) => op switch
    {
        SegmentOperator.Empty or SegmentOperator.NotEmpty => (0, 0),
        SegmentOperator.Between => (2, 2),
        SegmentOperator.In => (1, 50),
        SegmentOperator.Exists or SegmentOperator.NotExists => (0, 1),
        _ => (1, 1),
    };
}

/// <summary>
/// Segmentte kullanılabilen alan. <see cref="Key"/> modül önekli ve kararlıdır (ör. "crm.company.city"). <see cref="ReadEntityKey"/>:
/// alanın ait olduğu kayıt türü; çağıranın bu türü okuma izni (ve modülün tenant için açık olması) yoksa alan o kişiye
/// sunulmaz, o alanla filtreleme/sayım/önizleme yapılamaz (alan görme yetkisi olmayan, alan üzerinden üyelik öğrenemez).
/// </summary>
public sealed record SegmentFieldDefinition(
    string Key, string Label, string Group, SegmentTarget Target, SegmentFieldType Type, string ReadEntityKey)
{
    /// <summary>Seçim alanında izin verilen değerler (anahtar → görünen ad); boşsa serbest.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Kullanıcıya gösterilen açıklama (NULL davranışı, birim, pencere).</summary>
    public string? Hint { get; init; }
}

/// <summary>Tek koşul: alan, operatör ve (operatöre göre) değerler. Değerler metin olarak gelir, kaynak tipine göre çözer.</summary>
public sealed record SegmentLeaf(string Field, SegmentOperator Operator, IReadOnlyList<string> Values);

/// <summary>
/// Değerlendirme bağlamı. <see cref="Owners"/>: çağıranın veri kapsamı (null = hepsi); kaynak sorumlusu olan kayıtları buna göre
/// kısıtlar. <see cref="Today"/>: tenant saat dilimindeki bugün; <see cref="StartOfDay"/> bir günün başlangıç anını UTC olarak verir
/// (gün sınırları saat dilimine göredir, yaz saatine duyarlıdır).
/// </summary>
public sealed class SegmentEvaluationContext(IReadOnlySet<Guid>? owners, DateOnly today, Func<DateOnly, DateTimeOffset> startOfDay)
{
    public IReadOnlySet<Guid>? Owners { get; } = owners;

    public DateOnly Today { get; } = today;

    public DateTimeOffset StartOfDay(DateOnly day) => startOfDay(day);
}

/// <summary>
/// Bir modülün segment alan kaynağı. Modül <c>ConfigureServices</c>'te scoped kaydeder. Kaynak yalnızca kendi tablolarını okur
/// ve kayıt boru hattı dışında yazmaz. Tenant filtresi ve RLS kaynağın kullandığı veri erişimiyle uygulanır.
/// </summary>
public interface ISegmentFieldSource
{
    string ModuleKey { get; }

    IReadOnlyList<SegmentFieldDefinition> Fields { get; }

    /// <summary>
    /// Koşulu sağlayan hedef kimliklerinin sorgusu (ertelenmiş; çalıştırılmaz). Alan <see cref="Fields"/> içinde ve operatör tipine
    /// uygun, değerler geçerlidir (motor doğrular); kaynak yine de değer çözümünde başarısız olursa <see cref="FormatException"/> fırlatır.
    /// NULL: eşitlik, içerir, aralık ve tarih operatörleri boş değerle eşleşmez; <c>Ne</c> boş değeri de kapsar.
    /// </summary>
    IQueryable<Guid> Match(SegmentFieldDefinition field, SegmentLeaf leaf, SegmentEvaluationContext context);
}
