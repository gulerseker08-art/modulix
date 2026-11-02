namespace Modulix.Web.Services;

/// <summary>Pazarlama ekranlarının Türkçe etiketleri (segment hedefi, türü, durumu, koşul operatörleri).</summary>
public static class MarketingLabels
{
    public static readonly (string Key, string Label)[] Targets = [("Company", "Firma"), ("Person", "Kişi"), ("Lead", "Lead")];

    public static readonly (string Key, string Label)[] Kinds = [("Dynamic", "Dinamik (koşuldan)"), ("Static", "Statik (elle seçilen)")];

    public static readonly (string Key, string Label)[] Statuses = [("Draft", "Taslak"), ("Active", "Etkin"), ("Archived", "Arşiv")];

    private static readonly Dictionary<string, string> Operators = new()
    {
        ["eq"] = "eşittir",
        ["ne"] = "eşit değildir (boş dahil)",
        ["contains"] = "içerir",
        ["in"] = "listedekilerden biri",
        ["between"] = "arasında",
        ["empty"] = "boş",
        ["notempty"] = "dolu",
        ["lastdays"] = "son N gün içinde",
        ["nextdays"] = "önümüzdeki N gün içinde",
        ["before"] = "öncesinde",
        ["after"] = "sonrasında",
        ["exists"] = "var",
        ["notexists"] = "yok",
    };

    public static string Label((string Key, string Label)[] list, string key) => list.FirstOrDefault(i => i.Key == key).Label ?? key;

    public static string Operator(string key) => Operators.GetValueOrDefault(key, key);

    public static string StatusBadge(string status) => status switch { "Active" => "won", "Archived" => "cancelled", _ => "lead" };

    /// <summary>Operatörün istediği değer girişi sayısı: (en az, en çok). Sunucudaki <c>SegmentOperators.ValueCount</c> ile aynıdır.</summary>
    public static (int Min, int Max) ValueCount(string op) => op switch
    {
        "empty" or "notempty" => (0, 0),
        "between" => (2, 2),
        "in" => (1, 50),
        "exists" or "notexists" => (0, 1),
        _ => (1, 1),
    };

    /// <summary>Koşul kurallarını anlatan kısa not (NULL ve gün sınırı davranışı).</summary>
    public const string NullNote = "Boş değerler eşittir, içerir ve aralık koşullarıyla eşleşmez; 'eşit değildir' boş değerleri de kapsar. Tarih sınırları firmanın saat dilimine göredir.";
}
