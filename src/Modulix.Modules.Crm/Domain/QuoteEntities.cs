namespace Modulix.Modules.Crm.Domain;

// Teklif (ADR-0019). Başlık ve kalemler tenant iş veritabanındadır (RLS'li). Toplamlar ve durumlar sunucu tarafından yazılır;
// istemciden gelen toplam kabul edilmez (QuoteCalculator tek kaynaktır).

/// <summary>
/// Teklif sürümü. Aynı <see cref="FamilyId"/> altındaki sürümler revizyonlardır (V1, V2...); fırsata değil, ticari teklife aittir.
/// Ticari durum, iç onay ve gönderim durumu ayrı alanlardır. Sunulmuş sürümün ticari içeriği yerinde değişmez.
/// </summary>
public sealed class Quote : CrmEntity, IOwnedEntity
{
    public const int NumberMaxLength = 30;
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int StatusMaxLength = 20;
    public const int CurrencyLength = 3;
    public const int ReasonMaxLength = 500;

    /// <summary>Tenant içinde benzersiz numara (TK-20261029-AB12CD); revizyonlar aynı numarayı <see cref="RevisionNo"/> ile paylaşır.</summary>
    public string QuoteNumber { get; internal set; } = string.Empty;

    /// <summary>Teklif ailesi: ilk sürümün kimliği. Tüm revizyonlar aynı aileyi taşır.</summary>
    public Guid FamilyId { get; internal set; }

    public int RevisionNo { get; internal set; } = 1;

    public string Title { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public Guid CompanyId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    /// <summary>İsteğe bağlı; fırsat olmadan da teklif açılır, bir fırsata birden çok teklif bağlanabilir.</summary>
    public Guid? OpportunityId { get; internal set; }

    public Guid? PriceListId { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    /// <summary><see cref="Quotes.QuoteStatus"/>.</summary>
    public string Status { get; internal set; } = Quotes.QuoteStatus.Draft;

    /// <summary><see cref="Quotes.QuoteApprovalStatus"/>.</summary>
    public string ApprovalStatus { get; internal set; } = Quotes.QuoteApprovalStatus.NotRequired;

    /// <summary><see cref="Quotes.QuoteDeliveryStatus"/>.</summary>
    public string DeliveryStatus { get; internal set; } = Quotes.QuoteDeliveryStatus.NotSent;

    /// <summary>Ret, iptal ve revizyon gerekçesi (son geçişin nedeni).</summary>
    public string? StatusReason { get; internal set; }

    public DateOnly IssueDate { get; internal set; }

    /// <summary>Geçerlilik bitişi (anı). Kabul anında yeniden denetlenir.</summary>
    public DateTimeOffset? ValidUntil { get; internal set; }

    public string Currency { get; internal set; } = "TRY";

    /// <summary>Raporlama para birimi ve kur: yalnızca doluysa kur sabitlenmiştir (kaynak, gün ile birlikte).</summary>
    public string? ReportingCurrency { get; internal set; }

    public decimal? ExchangeRate { get; internal set; }

    public DateOnly? ExchangeRateDate { get; internal set; }

    public bool PriceIncludesTax { get; internal set; }

    /// <summary>Teklifin hesaplandığı kural sürümü; tenant ayarı sonradan değişse de tarihsel teklif bu sürümle okunur.</summary>
    public int CalculationPolicyVersion { get; internal set; } = 1;

    /// <summary><see cref="Core.Pricing.QuoteDiscountKind"/> adı (None, Percent, Amount).</summary>
    public string DocumentDiscountKind { get; internal set; } = "None";

    public decimal DocumentDiscountValue { get; internal set; }

    public string? PaymentTerms { get; internal set; }

    public string? DeliveryTerms { get; internal set; }

    public string? CommercialTerms { get; internal set; }

    /// <summary>Müşteriye hiçbir çıktıda gösterilmeyen iç not.</summary>
    public string? InternalNotes { get; internal set; }

    // ---- Sunucu hesabı (QuoteCalculator); istemci yazamaz.

    public decimal ListTotal { get; internal set; }

    public decimal DiscountTotal { get; internal set; }

    public decimal NetTotal { get; internal set; }

    public decimal TaxTotal { get; internal set; }

    public decimal GrossTotal { get; internal set; }

    /// <summary>Seçilmemiş opsiyonel/alternatif kalemlerin ana toplamdan ayrı net tutarı.</summary>
    public decimal OptionalNet { get; internal set; }

    /// <summary>Tekrarlayan tutarların dönem başına özeti (JSON); sözleşme süresi olmadan toplam üretilmez.</summary>
    public string? RecurringSummary { get; internal set; }

    public DateTimeOffset? CalculatedAt { get; internal set; }

    // ---- Yaşam döngüsü zamanları

    public DateTimeOffset? PresentedAt { get; internal set; }

    public DateTimeOffset? DecidedAt { get; internal set; }

    /// <summary>Sunulduğu anda sabitlenen müşteri ünvanı; sonradan firma kartı değişse de belge aynı kalır.</summary>
    public string? CustomerNameSnapshot { get; internal set; }

    public const int ServiceScopeSnapshotMaxLength = 8000;

    /// <summary>
    /// Sunulduğu anda sabitlenen hizmet kapsamı (ADR-0029 Dilim F3): bağlı fırsatın o andaki kapsamı, faaliyet adlarıyla birlikte JSON dizi
    /// (<c>[{"code","name"}]</c>). Katalogda ad değişse ya da faaliyet pasife alınsa da sunulmuş teklif aynı kalır. Fırsat yoksa ya da kapsamı
    /// boşsa null; yalnızca sunum eylemiyle yazılır, revizyon ve kopyada taşınmaz (yeni taslak sunulurken yeniden sabitlenir).
    /// </summary>
    public string? ServiceScopeSnapshot { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Teklif kalemi. Ürün kodu/adı/açıklaması, birim fiyat ve vergi oranı kalemde kopya olarak durur; katalog değişse de teklif değişmez.
/// <see cref="StableKey"/> revizyonlar arasında aynı kalemi izler (revizyon karşılaştırması).
/// </summary>
public sealed class QuoteLine : CrmEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int TextMaxLength = 2000;
    public const int KindMaxLength = 20;

    public Guid QuoteId { get; internal set; }

    public Guid StableKey { get; internal set; }

    public int Position { get; internal set; }

    public string? GroupName { get; internal set; }

    /// <summary><see cref="Quotes.QuoteLineKind"/>.</summary>
    public string Kind { get; internal set; } = Quotes.QuoteLineKind.Product;

    public Guid? ProductId { get; internal set; }

    public string? ProductCode { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public decimal Quantity { get; internal set; } = 1m;

    public Guid? UnitId { get; internal set; }

    public string? UnitName { get; internal set; }

    public decimal UnitPrice { get; internal set; }

    public string DiscountKind { get; internal set; } = "None";

    public decimal DiscountValue { get; internal set; }

    public decimal TaxRatePercent { get; internal set; }

    public string? TaxCode { get; internal set; }

    /// <summary><see cref="Core.Pricing.QuoteLineRole"/> adı.</summary>
    public string Role { get; internal set; } = "Standard";

    public bool IsSelected { get; internal set; }

    /// <summary>Paket bileşeniyse paket ana satırının <see cref="StableKey"/> değeri.</summary>
    public Guid? ParentStableKey { get; internal set; }

    /// <summary>Boşsa tek seferlik; doluysa dönem (Monthly, Yearly...).</summary>
    public string? RecurringPeriod { get; internal set; }

    /// <summary>Yetkili kullanıcıya özel maliyet bilgisi; yetkisiz yanıtlara hiç konmaz.</summary>
    public decimal? UnitCost { get; internal set; }

    // ---- Sunucu hesabı

    public bool Included { get; internal set; } = true;

    public decimal ListAmount { get; internal set; }

    public decimal LineDiscount { get; internal set; }

    public decimal DocumentDiscountShare { get; internal set; }

    public decimal Net { get; internal set; }

    public decimal Tax { get; internal set; }

    public decimal Gross { get; internal set; }
}

/// <summary>Teklif tarihçesi: her geçiş (kim, ne zaman, hangi durumdan hangisine, gerekçe). Eklenir, değişmez.</summary>
public sealed class QuoteEvent : CrmEntity
{
    public Guid QuoteId { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public string? Reason { get; internal set; }
}
