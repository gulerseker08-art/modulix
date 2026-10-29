namespace Modulix.Modules.Crm.Domain;

// Ürün/hizmet kataloğu (ADR-0018). Kayıt boru hattından geçer; tablolar tenant iş veritabanındadır (RLS'li).

/// <summary>Ürün kategorisi; üst kategori verilirse alt kategoridir (döngü reddedilir).</summary>
public sealed class ProductCategory : CrmEntity
{
    public const int NameMaxLength = 100;

    public string Name { get; internal set; } = string.Empty;

    public Guid? ParentId { get; internal set; }

    public bool IsActive { get; internal set; } = true;
}

/// <summary>
/// Ölçü birimi. <see cref="GroupKey"/> anlam grubudur (adet, ağırlık, uzunluk, süre); <see cref="FactorToBase"/> grubun temel birimine
/// katsayıdır (temel birimde 1, pozitif). Ürün tarafından kullanılan birimin grubu ve katsayısı değişmez (geçmiş hesaplar korunur).
/// </summary>
public sealed class MeasureUnit : CrmEntity
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 60;

    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    public string GroupKey { get; internal set; } = CatalogRules.UnitGroups[0];

    public decimal FactorToBase { get; internal set; } = 1m;
}

/// <summary>
/// Ürün ya da hizmet kartı. Kod tenant içinde benzersizdir. Kullanılmış ürün silinmez, pasifleştirilir; pasif ve satıştan kaldırılmış
/// ürün yeni satışta kullanılamaz (<see cref="CatalogRules.IsSellable"/>). Maliyet yetkiyle sınırlı hassas alandır.
/// </summary>
public sealed class Product : CrmEntity
{
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 200;
    public const int TextMaxLength = 2000;
    public const int ShortMaxLength = 100;

    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary><see cref="CatalogRules.Kinds"/>.</summary>
    public string Kind { get; internal set; } = CatalogRules.KindProduct;

    /// <summary><see cref="CatalogRules.Statuses"/>.</summary>
    public string Status { get; internal set; } = CatalogRules.StatusDraft;

    public Guid? CategoryId { get; internal set; }

    public ProductCategory? Category { get; internal set; }

    public Guid BaseUnitId { get; internal set; }

    public MeasureUnit BaseUnit { get; internal set; } = null!;

    public string? ExternalCode { get; internal set; }

    public string? Barcode { get; internal set; }

    public string? Brand { get; internal set; }

    public string? Model { get; internal set; }

    public string? Tags { get; internal set; }

    /// <summary>Satış başlangıcı (dahil) ve bitişi (dahil gün); boşsa sınırsız.</summary>
    public DateOnly? SaleStart { get; internal set; }

    public DateOnly? SaleEnd { get; internal set; }

    /// <summary>Vergi kategorisi anahtarı; oran vergi tanımından gelir (kodda sabit oran yoktur).</summary>
    public string? TaxCategory { get; internal set; }

    public bool PriceIncludesTax { get; internal set; }

    /// <summary>Miktarın izinli ondalık hanesi (0 = tam sayı).</summary>
    public int QuantityPrecision { get; internal set; }

    /// <summary>Maliyet (hassas, <c>crm.product-costs.read</c>); boşsa bilinmiyor, 0 değil.</summary>
    public decimal? Cost { get; internal set; }

    public string CostCurrency { get; internal set; } = "TRY";

    /// <summary>Hizmet süresi (dakika), teslim şekli ve ücretlendirme (tek seferlik / dönemsel).</summary>
    public int? ServiceDurationMinutes { get; internal set; }

    public string? DeliveryMode { get; internal set; }

    public string ChargeType { get; internal set; } = CatalogRules.ChargeOneTime;

    public string? RecurringPeriod { get; internal set; }
}

/// <summary>Katalog sabitleri ve saf kuralları.</summary>
public static class CatalogRules
{
    public const string KindProduct = "Product";
    public const string KindService = "Service";
    public const string StatusDraft = "Draft";
    public const string StatusActive = "Active";
    public const string StatusInactive = "Inactive";
    public const string StatusWithdrawn = "Withdrawn";
    public const string ChargeOneTime = "OneTime";
    public const string ChargeRecurring = "Recurring";

    public static IReadOnlyList<string> Kinds { get; } = [KindProduct, KindService];

    public static IReadOnlyList<string> Statuses { get; } = [StatusDraft, StatusActive, StatusInactive, StatusWithdrawn];

    public static IReadOnlyList<string> ChargeTypes { get; } = [ChargeOneTime, ChargeRecurring];

    public static IReadOnlyList<string> RecurringPeriods { get; } = ["Monthly", "Quarterly", "Yearly"];

    public static IReadOnlyList<string> DeliveryModes { get; } = ["OnSite", "Remote", "Shipped", "Digital"];

    /// <summary>Birim anlam grupları: adet, ağırlık, uzunluk, süre, hacim, alan.</summary>
    public static IReadOnlyList<string> UnitGroups { get; } = ["count", "weight", "length", "time", "volume", "area"];

    /// <summary>
    /// Durum geçişi: taslak → aktif/pasif/kaldırılmış; aktif ↔ pasif; aktif/pasif → satıştan kaldırılmış. Satıştan kaldırılmış
    /// son durumdur (yeniden satışa açmak yeni kart ister).
    /// </summary>
    public static bool CanTransition(string from, string to) =>
        from == to
        || (from, to) switch
        {
            (StatusDraft, _) => true,
            (StatusActive, StatusInactive or StatusWithdrawn) => true,
            (StatusInactive, StatusActive or StatusWithdrawn) => true,
            _ => false,
        };

    /// <summary>Yeni satışta kullanılabilir mi? Yalnızca aktif ve satış tarihi aralığında olan ürün; geçmiş belgeler etkilenmez.</summary>
    public static bool IsSellable(string status, DateOnly? saleStart, DateOnly? saleEnd, DateOnly on) =>
        status == StatusActive && (saleStart is null || on >= saleStart) && (saleEnd is null || on <= saleEnd);
}
