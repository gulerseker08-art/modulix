namespace Modulix.Web.Services;

/// <summary>Fiyatlandırma sabit listelerinin Türkçe etiketleri (ADR-0018); anahtarlar sunucudaki PricingRules ile aynıdır.</summary>
public static class PricingLabels
{
    public static IReadOnlyList<(string Key, string Label)> Scopes { get; } =
        [("Default", "Varsayılan"), ("Customer", "Müşteriye özel"), ("Segment", "Segment / kanal")];

    public static IReadOnlyList<(string Key, string Label)> TierMethods { get; } =
        [("Volume", "Hacim (tüm miktar tek kademe fiyatından)"), ("Graduated", "Kademeli (her aralık kendi fiyatından)")];

    public static IReadOnlyList<(string Key, string Label)> ListStatuses { get; } =
        [("Draft", "Taslak"), ("Published", "Yayımlandı"), ("Archived", "Arşiv")];

    public static IReadOnlyList<(string Key, string Label)> RateSources { get; } =
        [("Manual", "Elle giriş"), ("CentralBank", "Merkez bankası"), ("Import", "İçe aktarma")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string ListBadge(string? status) => status switch
    {
        "Published" => "success",
        "Archived" => "muted",
        _ => "info",
    };

    /// <summary>Hesap hatası anahtarlarının kullanıcıya dönük açıklaması; bilinmeyen anahtar sunucu iletisiyle gösterilir.</summary>
    public static string? ErrorHint(string? code) => code switch
    {
        "price-not-found" => "Bu ürün, tarih, para birimi ve miktar için yayımlanmış bir fiyat yok.",
        "price-conflict" => "Aynı öncelikte birden fazla fiyat listesi çakışıyor; bir liste seçin.",
        "tax-rate-not-found" => "Ürünün vergi kategorisi için geçerli oran tanımlı değil.",
        "exchange-rate-not-found" => "Gösterim para birimi için kayıtlı kur yok.",
        "product-not-sellable" => "Ürün bu tarihte satışa uygun değil.",
        _ => null,
    };
}
