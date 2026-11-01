using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Orders.Domain;

// Sipariş modülü entity'leri (ADR-0023). Alanlar kayıt depolarından (Records) doğrulanarak yazılır. Müşteri ve kişiler ortak kayıttır
// (core.party / core.person); CRM tablolarına bağ yoktur. Ticari bilgiler teklif kabul anındaki kopyadır (snapshot).

/// <summary>Tüm sipariş kayıtlarının ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class OrdersEntity : ITenantEntity, ICompanyShared
{
    protected OrdersEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri, JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Bir kullanıcıya atanabilen kayıt. Sorumlu, aktif tenant'ın etkin üyesi olmalıdır.</summary>
public interface IOwnedEntity
{
    Guid? OwnerId { get; }
}

/// <summary>
/// Satış siparişi. Yalnızca kabul edilmiş teklifin bir revizyonundan oluşur; ticari bilgiler o andaki kopyadır ve sonradan katalog, müşteri
/// kartı ya da teklif değişse de sipariş değişmez. Ticari durum, satış devri durumu ve gerçekleşme durumu ayrı alanlardır; durumlar yalnızca
/// iş akışı eylemleriyle yazılır. Sipariş tutarı tahsilat, fatura ya da muhasebeleştirilmiş gelir değildir.
/// </summary>
public sealed class SalesOrder : OrdersEntity, IOwnedEntity
{
    public const int NumberMaxLength = 30;
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int NameMaxLength = 200;
    public const int StatusMaxLength = 20;
    public const int ReasonMaxLength = 500;
    public const int CurrencyLength = 3;
    public const int AddressMaxLength = 1000;

    /// <summary>Tenant içinde benzersiz numara (SP-20261101-AB12CD).</summary>
    public string Number { get; internal set; } = string.Empty;

    public string Title { get; internal set; } = string.Empty;

    /// <summary>Müşteri: ortak taraf kaydı (core.party, ADR-0006).</summary>
    public Guid PartyId { get; internal set; }

    public Guid? PersonId { get; internal set; }

    /// <summary>Satış sorumlusu. Veri kapsamı (kendi/takım/birim/şirket) bu alana göre uygulanır.</summary>
    public Guid? OwnerId { get; internal set; }

    // ---- Kaynak teklif (CRM tablosuna bağ yoktur; kimlik ve numara anlık görüntüdür)

    public Guid SourceQuoteId { get; internal set; }

    public string SourceQuoteNumber { get; internal set; } = string.Empty;

    public int SourceQuoteRevisionNo { get; internal set; }

    /// <summary>Müşterinin kabul ettiği teklif belgesinin SHA-256 özeti.</summary>
    public string? SourceQuoteSha256 { get; internal set; }

    public Guid? SourceOpportunityId { get; internal set; }

    /// <summary>Mevcutsa sözleşme bağlantısı: kimlik ve numara referansıdır, başka modülün tablosuna bağ ya da doğrulama yoktur.</summary>
    public Guid? ContractId { get; internal set; }

    public string? ContractNumber { get; internal set; }

    /// <summary><see cref="Lifecycle.ConversionMode"/>: Full (teklifin tamamı) ya da Partial (kalem bazında miktar).</summary>
    public string ConversionMode { get; internal set; } = Lifecycle.ConversionMode.Full;

    // ---- Müşteri kabulü (iç onaydan ayrıdır)

    public DateTimeOffset AcceptedAt { get; internal set; }

    /// <summary><see cref="Lifecycle.AcceptanceMethods"/>: Portal ya da Manual.</summary>
    public string AcceptanceMethod { get; internal set; } = Lifecycle.AcceptanceMethods.Manual;

    public Guid? AcceptedByUserId { get; internal set; }

    /// <summary>Kabul eden müşteri tarafı kişi adı (portal kabulü); manuel kabulde boştur.</summary>
    public string? AcceptedByName { get; internal set; }

    public string? AcceptanceNote { get; internal set; }

    /// <summary>Kaynak teklifin geçerlilik bitişi. Geçerlilik kabul anında denetlenmiştir; dönüşümde yeniden denetlenmez.</summary>
    public DateTimeOffset? QuoteValidUntil { get; internal set; }

    // ---- Üç ayrı durum

    /// <summary><see cref="Lifecycle.OrderStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.OrderStatus.Draft;

    /// <summary><see cref="Lifecycle.HandoverStatus"/>.</summary>
    public string HandoverStatus { get; internal set; } = Lifecycle.HandoverStatus.NotStarted;

    /// <summary><see cref="Lifecycle.FulfillmentStatus"/>; kalem ve hizmet kayıtlarından türetilir, elle yazılmaz.</summary>
    public string FulfillmentStatus { get; internal set; } = Lifecycle.FulfillmentStatus.NotStarted;

    public string? StatusReason { get; internal set; }

    // ---- Ticari snapshot

    public string? CustomerName { get; internal set; }

    public string? BillingAddress { get; internal set; }

    public string? DeliveryAddress { get; internal set; }

    public string Currency { get; internal set; } = "TRY";

    public bool PriceIncludesTax { get; internal set; }

    public string? ReportingCurrency { get; internal set; }

    public decimal? ExchangeRate { get; internal set; }

    public DateOnly? ExchangeRateDate { get; internal set; }

    public string? PaymentTerms { get; internal set; }

    public string? DeliveryTerms { get; internal set; }

    public string? CommercialTerms { get; internal set; }

    /// <summary>Müşteriye verilen taahhütler (serbest metin; yapılandırılmış taahhütler sözleşme modülündedir).</summary>
    public string? Commitments { get; internal set; }

    public string? OperationsNotes { get; internal set; }

    /// <summary>Teslimat/hizmet lokasyonu (serbest metin kopya).</summary>
    public string? Location { get; internal set; }

    public DateOnly? PlannedStartDate { get; internal set; }

    public DateOnly? PlannedDeliveryDate { get; internal set; }

    // ---- Sunucu hesabı (kalemlerden); istemci yazamaz. Hassas alan.

    public decimal NetTotal { get; internal set; }

    public decimal TaxTotal { get; internal set; }

    public decimal GrossTotal { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Sipariş kalemi. Ürün/hizmet bilgisi, miktar ve tutarlar teklif kaleminden kopyalanır; katalog değişse de kalem değişmez.
/// <see cref="SourceQuoteLineKey"/> teklif kaleminin kararlı anahtarıdır. Kısmi dönüşümde tutarlar dönüştürülen miktar oranındadır.
/// </summary>
public sealed class SalesOrderLine : OrdersEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int TextMaxLength = 2000;
    public const int UnitMaxLength = 50;
    public const int KindMaxLength = 20;

    public Guid OrderId { get; internal set; }

    public int Position { get; internal set; }

    /// <summary><see cref="Lifecycle.LineKinds"/>: Product (fiziksel teslimat), Service (hizmet başlangıcı), Package, Free.</summary>
    public string Kind { get; internal set; } = Lifecycle.LineKinds.Product;

    /// <summary>Paket bileşeniyse paket ana kaleminin teklif kalemi anahtarı.</summary>
    public Guid? ParentSourceLineKey { get; internal set; }

    public string? GroupName { get; internal set; }

    /// <summary>Katalog ürün kimliği (kopya; yabancı anahtar değildir).</summary>
    public Guid? ProductId { get; internal set; }

    public string? ProductCode { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Siparişe alınan miktar.</summary>
    public decimal Quantity { get; internal set; } = 1m;

    /// <summary>Teklifte kabul edilen toplam miktar (kısmi dönüşümde sipariş miktarından büyük olabilir).</summary>
    public decimal AcceptedQuantity { get; internal set; } = 1m;

    public string? UnitName { get; internal set; }

    /// <summary>İndirim sonrası net birim fiyat.</summary>
    public decimal UnitPrice { get; internal set; }

    public decimal ListAmount { get; internal set; }

    public decimal LineDiscount { get; internal set; }

    public decimal TaxRatePercent { get; internal set; }

    public decimal Net { get; internal set; }

    public decimal Tax { get; internal set; }

    public decimal Gross { get; internal set; }

    public string? RecurringPeriod { get; internal set; }

    public Guid SourceQuoteId { get; internal set; }

    public Guid SourceQuoteLineKey { get; internal set; }

    /// <summary>Teslim edilen miktar (ürün kalemi); gerçekleşme kayıtlarından sunucu yazar.</summary>
    public decimal DeliveredQuantity { get; internal set; }

    /// <summary>
    /// İptal ya da değişiklik talebiyle azaltılan miktar. Etkin miktar <c>Quantity - CancelledQuantity</c>'dir; kalemin kopyalanmış tutarları
    /// değişmez, etkin tutar orantılı hesaplanır. Teslim edilen miktar hiçbir zaman azaltılamaz (<c>Delivered + Cancelled &lt;= Quantity</c>).
    /// </summary>
    public decimal CancelledQuantity { get; internal set; }

    /// <summary>Tamamı iptal edilmiş (teslim edilen yok) kalem; kayıt silinmez.</summary>
    public bool IsCancelled { get; internal set; }

    public decimal EffectiveQuantity => Quantity - CancelledQuantity;

    public DateOnly? PlannedDate { get; internal set; }
}

/// <summary>Sipariş tarihçesi: her geçiş ve önemli olay (kim, ne zaman, hangi durumdan hangisine, gerekçe). Eklenir, değişmez.</summary>
public sealed class SalesOrderEvent : OrdersEntity
{
    public Guid OrderId { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public string? Reason { get; internal set; }
}

/// <summary>
/// Teklif kalemi tüketim defteri: her teklif kalemi için kabul edilen ve siparişe dönüştürülen miktar ve tutar. Aynı kalemin kabul edilen
/// miktarın üzerinde siparişe alınmasını uygulama kontrolüne ek olarak veritabanı kısıtı ile de engeller; eşzamanlı dönüşümde sürüm belirteci
/// ve benzersiz anahtar ikinci isteği reddeder.
/// </summary>
public sealed class QuoteLineConsumption : OrdersEntity
{
    public Guid QuoteId { get; internal set; }

    public Guid LineKey { get; internal set; }

    public decimal AcceptedQuantity { get; internal set; }

    public decimal AcceptedNet { get; internal set; }

    public decimal AcceptedTax { get; internal set; }

    public decimal ConvertedQuantity { get; internal set; }

    public decimal ConvertedNet { get; internal set; }

    public decimal ConvertedTax { get; internal set; }

    public decimal RemainingQuantity => AcceptedQuantity - ConvertedQuantity;
}

/// <summary>
/// Onaylanmış siparişin gerekçeli değişiklik talebi. Talep, önerilen değişiklikleri (<see cref="Changes"/>) ve talep anındaki etki özetini
/// (<see cref="Impact"/>) taşır; onaylanana kadar sipariş değişmez. Sipariş başına aynı anda en fazla bir bekleyen talep vardır.
/// </summary>
public sealed class OrderChangeRequest : OrdersEntity
{
    public const int JsonMaxLength = 4000;

    public Guid OrderId { get; internal set; }

    /// <summary>Sipariş içinde 1'den artan sıra.</summary>
    public int Sequence { get; internal set; }

    /// <summary><see cref="Lifecycle.ChangeRequestStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.ChangeRequestStatus.Pending;

    public string Reason { get; internal set; } = string.Empty;

    /// <summary>Önerilen değişiklikler (JSON: başlık alanları ve kalem değişiklikleri).</summary>
    public string Changes { get; internal set; } = "{}";

    /// <summary>Operasyon, teslimat ve sözleşme üzerindeki etki özeti (JSON; tutar içermez).</summary>
    public string Impact { get; internal set; } = "{}";

    /// <summary>Talebin tutara etkisi (negatif: azalır). Hassas alan.</summary>
    public decimal NetDelta { get; internal set; }

    public decimal TaxDelta { get; internal set; }

    public Guid? RequestedBy { get; internal set; }

    public Guid? DecidedBy { get; internal set; }

    public DateTimeOffset? DecidedAt { get; internal set; }

    public string? DecisionNote { get; internal set; }
}
