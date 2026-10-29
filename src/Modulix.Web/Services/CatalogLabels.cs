namespace Modulix.Web.Services;

/// <summary>Ürün kataloğu sabit listelerinin Türkçe etiketleri (ADR-0018); anahtarlar sunucudaki CatalogRules ile aynıdır.</summary>
public static class CatalogLabels
{
    public static IReadOnlyList<(string Key, string Label)> Kinds { get; } = [("Product", "Ürün"), ("Service", "Hizmet")];

    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
        [("Draft", "Taslak"), ("Active", "Aktif"), ("Inactive", "Pasif"), ("Withdrawn", "Satıştan kaldırıldı")];

    public static IReadOnlyList<(string Key, string Label)> ChargeTypes { get; } = [("OneTime", "Tek seferlik"), ("Recurring", "Periyodik")];

    public static IReadOnlyList<(string Key, string Label)> RecurringPeriods { get; } =
        [("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public static IReadOnlyList<(string Key, string Label)> DeliveryModes { get; } =
        [("OnSite", "Yerinde"), ("Remote", "Uzaktan"), ("Shipped", "Sevkiyatla"), ("Digital", "Dijital")];

    public static IReadOnlyList<(string Key, string Label)> UnitGroups { get; } =
        [("count", "Adet"), ("weight", "Ağırlık"), ("length", "Uzunluk"), ("time", "Süre"), ("volume", "Hacim"), ("area", "Alan")];

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
