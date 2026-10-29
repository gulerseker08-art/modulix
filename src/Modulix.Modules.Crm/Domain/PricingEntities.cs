namespace Modulix.Modules.Crm.Domain;

// Fiyat listeleri, vergi, kur, paket bileşenleri ve indirim politikaları (ADR-0018). Kayıt boru hattından geçer; tenant iş veritabanında (RLS'li).

/// <summary>
/// Vergi oranı. Ürünün <see cref="Product.TaxCategory"/> alanı <see cref="Code"/> ile eşleşir; oran sabit kodlanmaz, tarih aralığıyla tutulur
/// (geçmiş hesaplar eski oranla kalır). Aynı kod için geçerlilik aralıkları çakışamaz.
/// </summary>
public sealed class TaxRate : CrmEntity
{
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 100;

    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Yüzde olarak oran (ör. 20); 0 vergisiz demektir.</summary>
    public decimal RatePercent { get; internal set; }

    /// <summary>Geçerlilik başı (dahil).</summary>
    public DateOnly ValidFrom { get; internal set; }

    /// <summary>Geçerlilik sonu (hariç); boşsa süresiz.</summary>
    public DateOnly? ValidTo { get; internal set; }

    public bool IsActive { get; internal set; } = true;
}

/// <summary>
/// Döviz kuru: 1 <see cref="FromCurrency"/> = <see cref="Rate"/> <see cref="ToCurrency"/>, <see cref="ValidOn"/> gününden itibaren.
/// Kaynak ve not tutulur; kur elle girilebilir. Aynı çift ve gün için tek kayıt vardır. Belgelere uygulanan kur belgede saklanır,
/// sonradan eklenen kur eski belgeyi değiştirmez.
/// </summary>
public sealed class ExchangeRate : CrmEntity
{
    public string FromCurrency { get; internal set; } = string.Empty;

    public string ToCurrency { get; internal set; } = string.Empty;

    public decimal Rate { get; internal set; }

    public DateOnly ValidOn { get; internal set; }

    /// <summary><see cref="PricingRules.RateSources"/>.</summary>
    public string Source { get; internal set; } = PricingRules.RateSourceManual;

    public string? Note { get; internal set; }
}

/// <summary>
/// Fiyat listesi başlığı. Yalnızca <c>Published</c> listeler fiyat seçiminde kullanılır; yayımlanmış liste (başlık ve satırlar) değişmez,
/// yalnızca arşivlenebilir. Değişiklik için yeni liste açılır. Kapsam: <c>Default</c> (tenant varsayılanı), <c>Customer</c> (firmaya özel),
/// <c>Segment</c> (segment/kanal adı eşleşmesi).
/// </summary>
public sealed class PriceList : CrmEntity
{
    public const int NameMaxLength = 150;

    public string Name { get; internal set; } = string.Empty;

    public string Currency { get; internal set; } = "TRY";

    public string Scope { get; internal set; } = PricingRules.ScopeDefault;

    public Guid? CompanyId { get; internal set; }

    public string? Segment { get; internal set; }

    public DateTimeOffset? ValidFrom { get; internal set; }

    public DateTimeOffset? ValidTo { get; internal set; }

    /// <summary><see cref="PricingRules.ListStatuses"/>.</summary>
    public string Status { get; internal set; } = PricingRules.ListDraft;

    /// <summary><see cref="PricingRules.TierMethods"/>.</summary>
    public string TierMethod { get; internal set; } = PricingRules.MethodVolume;

    public DateTimeOffset? PublishedAt { get; internal set; }
}

/// <summary>
/// Fiyat listesi satırı: bir ürünün bir miktar kademesi. Fiyat ürünün <b>temel birimi</b> başınadır; kademe aralığı [Min, Max) temel
/// birim miktarıdır. Ücretsiz satış <see cref="IsFree"/> ile işaretlenir (fiyat 0), eksik fiyattan ayrıdır.
/// </summary>
public sealed class PriceListItem : CrmEntity
{
    public Guid PriceListId { get; internal set; }

    public PriceList? PriceList { get; internal set; }

    public Guid ProductId { get; internal set; }

    public Product? Product { get; internal set; }

    public decimal MinQuantity { get; internal set; }

    public decimal? MaxQuantity { get; internal set; }

    public decimal UnitPrice { get; internal set; }

    public bool IsFree { get; internal set; }
}

/// <summary>Paket bileşeni: <see cref="PackageId"/> ürünü, <see cref="ComponentId"/> ürününden <see cref="Quantity"/> (temel birim) içerir. Döngü reddedilir.</summary>
public sealed class ProductComponent : CrmEntity
{
    public Guid PackageId { get; internal set; }

    public Product? Package { get; internal set; }

    public Guid ComponentId { get; internal set; }

    public Product? Component { get; internal set; }

    public decimal Quantity { get; internal set; } = 1m;
}

/// <summary>
/// İndirim politikası: yüzde ya da tutar (tam biri). Öncelik sırasıyla ardışık uygulanır. <see cref="ApprovalAbovePercent"/> aşan etkin
/// indirim onay gerektirir olarak işaretlenir.
/// </summary>
public sealed class DiscountPolicy : CrmEntity
{
    public const int NameMaxLength = 150;

    public string Name { get; internal set; } = string.Empty;

    public decimal? Percent { get; internal set; }

    public decimal? Amount { get; internal set; }

    public string? Currency { get; internal set; }

    public int Priority { get; internal set; }

    public bool Stackable { get; internal set; } = true;

    public decimal? MaxTotalPercent { get; internal set; }

    public decimal? ApprovalAbovePercent { get; internal set; }

    public DateOnly? ValidFrom { get; internal set; }

    public DateOnly? ValidTo { get; internal set; }

    public bool IsActive { get; internal set; } = true;
}

public static class PricingRules
{
    public const string ScopeDefault = "Default";
    public const string ScopeCustomer = "Customer";
    public const string ScopeSegment = "Segment";
    public const string ListDraft = "Draft";
    public const string ListPublished = "Published";
    public const string ListArchived = "Archived";
    public const string MethodVolume = "Volume";
    public const string MethodGraduated = "Graduated";
    public const string RateSourceManual = "Manual";

    public static IReadOnlyList<string> Scopes { get; } = [ScopeDefault, ScopeCustomer, ScopeSegment];

    public static IReadOnlyList<string> ListStatuses { get; } = [ListDraft, ListPublished, ListArchived];

    public static IReadOnlyList<string> TierMethods { get; } = [MethodVolume, MethodGraduated];

    public static IReadOnlyList<string> RateSources { get; } = [RateSourceManual, "CentralBank", "Import"];

    /// <summary>Taslak → Yayımlandı → Arşiv; geri dönüş yoktur. Aynı durum geçerlidir (başka alan değişirken).</summary>
    public static bool CanTransition(string from, string to) => from == to || (from, to) switch
    {
        (ListDraft, ListPublished) => true,
        (ListDraft, ListArchived) => true,
        (ListPublished, ListArchived) => true,
        _ => false,
    };
}
