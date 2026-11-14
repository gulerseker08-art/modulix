namespace Modulix.Web.Services;

/// <summary>Fiyatlandırma sabit listelerinin Türkçe etiketleri (ADR-0018); anahtarlar sunucudaki PricingRules ile aynıdır.</summary>
public static class PricingLabels
{
    private static readonly (string Key, string Label)[] ScopeSource =
        [("Default", "Varsayılan"), ("Customer", "Müşteriye özel"), ("Segment", "Segment / kanal")];

    private static readonly (string Key, string Label)[] TierMethodSource =
        [("Volume", "Hacim (tüm miktar tek kademe fiyatından)"), ("Graduated", "Kademeli (her aralık kendi fiyatından)")];

    private static readonly (string Key, string Label)[] ListStatusSource =
        [("Draft", "Taslak"), ("Published", "Yayımlandı"), ("Archived", "Arşiv")];

    private static readonly (string Key, string Label)[] RateSourceSource =
        [("Manual", "Elle giriş"), ("CentralBank", "Merkez bankası"), ("Import", "İçe aktarma")];

    private static readonly (string Key, string Label)[] ErrorHintSource =
    [
        ("price-not-found", "Bu ürün, tarih, para birimi ve miktar için yayımlanmış bir fiyat yok."),
        ("price-conflict", "Aynı öncelikte birden fazla fiyat listesi çakışıyor; bir liste seçin."),
        ("tax-rate-not-found", "Ürünün vergi kategorisi için geçerli oran tanımlı değil."),
        ("exchange-rate-not-found", "Gösterim para birimi için kayıtlı kur yok."),
        ("product-not-sellable", "Ürün bu tarihte satışa uygun değil."),
    ];

    public const string ScopeGroup = "crm.pricelist-scope";
    public const string TierMethodGroup = "crm.pricelist-tier";
    public const string ListStatusGroup = "crm.pricelist-status";
    public const string ErrorHintGroup = "crm.priceCalculator.error";
    public const string RateSourceGroup = "crm.rate-source";

    // Türkçe ad kaynak metindir (ADR-0034); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
    // Özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir).
    public static IReadOnlyList<(string Key, string Label)> Scopes => Translate(ScopeGroup, ScopeSource);

    public static IReadOnlyList<(string Key, string Label)> TierMethods => Translate(TierMethodGroup, TierMethodSource);

    public static IReadOnlyList<(string Key, string Label)> ListStatuses => Translate(ListStatusGroup, ListStatusSource);

    /// <summary>Hesap hatası açıklamaları (anahtar: hata kodu); <see cref="ErrorHint"/> bunlardan okur.</summary>
    public static IReadOnlyList<(string Key, string Label)> ErrorHints => Translate(ErrorHintGroup, ErrorHintSource);

    // Kur kaynakları Vergi ve kur ekranındadır (ADR-0034, dilim 9b).
    public static IReadOnlyList<(string Key, string Label)> RateSources => Translate(RateSourceGroup, RateSourceSource);

    private static IReadOnlyList<(string Key, string Label)> Translate(string group, (string Key, string Label)[] source) =>
        [.. source.Select(x => (x.Key, CrmText.Of(group, x.Key, x.Label)))];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string ListBadge(string? status) => status switch
    {
        "Published" => "success",
        "Archived" => "muted",
        _ => "info",
    };

    /// <summary>Hesap hatası anahtarlarının kullanıcıya dönük açıklaması; bilinmeyen anahtar sunucu iletisiyle gösterilir.</summary>
    public static string? ErrorHint(string? code) =>
        code is null ? null : ErrorHints.FirstOrDefault(x => x.Key == code).Label;
}
