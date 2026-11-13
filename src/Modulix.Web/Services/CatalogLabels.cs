namespace Modulix.Web.Services;

/// <summary>Ürün kataloğu sabit listelerinin Türkçe etiketleri (ADR-0018); anahtarlar sunucudaki CatalogRules ile aynıdır.</summary>
public static class CatalogLabels
{
    private static readonly (string Key, string Label)[] KindSource = [("Product", "Ürün"), ("Service", "Hizmet")];

    private static readonly (string Key, string Label)[] StatusSource =
        [("Draft", "Taslak"), ("Active", "Aktif"), ("Inactive", "Pasif"), ("Withdrawn", "Satıştan kaldırıldı")];

    private static readonly (string Key, string Label)[] ChargeTypeSource = [("OneTime", "Tek seferlik"), ("Recurring", "Periyodik")];

    private static readonly (string Key, string Label)[] RecurringPeriodSource =
        [("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    private static readonly (string Key, string Label)[] DeliveryModeSource =
        [("OnSite", "Yerinde"), ("Remote", "Uzaktan"), ("Shipped", "Sevkiyatla"), ("Digital", "Dijital")];

    // Ürün türü ve periyot adları teklif kalemi tablolarıyla aynıdır; aynı anahtarlar kullanılır (ADR-0034, dilim 8c).
    public const string KindGroup = QuoteLabels.LineKindGroup;
    public const string StatusGroup = "crm.product-status";
    public const string ChargeTypeGroup = "crm.product-charge";
    public const string RecurringPeriodGroup = QuoteLabels.PeriodGroup;
    public const string DeliveryModeGroup = "crm.product-delivery";

    // Türkçe ad kaynak metindir (ADR-0034); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
    // Özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir).
    public static IReadOnlyList<(string Key, string Label)> Kinds => Translate(KindGroup, KindSource);

    public static IReadOnlyList<(string Key, string Label)> Statuses => Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> ChargeTypes => Translate(ChargeTypeGroup, ChargeTypeSource);

    public static IReadOnlyList<(string Key, string Label)> RecurringPeriods => Translate(RecurringPeriodGroup, RecurringPeriodSource);

    public static IReadOnlyList<(string Key, string Label)> DeliveryModes => Translate(DeliveryModeGroup, DeliveryModeSource);

    // Ölçü birimi grupları Ölçü birimleri ekranına aittir; o ekranın çeviri diliminde sözlüğe taşınacak.
    public static IReadOnlyList<(string Key, string Label)> UnitGroups { get; } =
        [("count", "Adet"), ("weight", "Ağırlık"), ("length", "Uzunluk"), ("time", "Süre"), ("volume", "Hacim"), ("area", "Alan")];

    private static IReadOnlyList<(string Key, string Label)> Translate(string group, (string Key, string Label)[] source) =>
        [.. source.Select(x => (x.Key, CrmText.Of(group, x.Key, x.Label)))];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string Badge(string? status) => status switch
    {
        "Active" => "success",
        "Inactive" => "muted",
        "Withdrawn" => "danger",
        _ => "info",
    };
}
