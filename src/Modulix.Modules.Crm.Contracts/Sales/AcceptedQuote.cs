namespace Modulix.Modules.Crm.Contracts.Sales;

// Kabul edilen teklifin sipariş/sözleşme gibi sonraki modüllere devri için entegrasyon sözleşmesi (ADR-0019, dilim 7). Sipariş ya da sözleşme
// modülü henüz yoktur; bu sözleşme onların CRM'e bağlanmadan (yalnızca CRM sözleşme projesi üzerinden) kabul edilmiş teklifi okuyabilmesi içindir.

/// <summary>Kabul edilen teklifin kalemi. Değerler teklifin sunulduğu andaki kopyadır (katalog değişse de aynı kalır).</summary>
public sealed record AcceptedQuoteLine(
    Guid StableKey,
    int Position,
    string? ProductCode,
    string Name,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal TaxRatePercent,
    decimal Net,
    decimal Tax,
    decimal Gross,
    string? RecurringPeriod)
{
    /// <summary>Kalem türü (Product, Service, Package, Free). Sipariş ürün teslimatı ile hizmet başlangıcını ayrı izler.</summary>
    public string Kind { get; init; } = "Product";

    /// <summary>Paket bileşeniyse paket ana kaleminin kararlı anahtarı.</summary>
    public Guid? ParentStableKey { get; init; }

    public string? GroupName { get; init; }

    /// <summary>Katalog ürün kimliği (kopya; sonradan katalog değişse de sipariş değişmez). Yabancı anahtar değildir.</summary>
    public Guid? ProductId { get; init; }

    public decimal ListAmount { get; init; }

    public decimal LineDiscount { get; init; }
}

/// <summary>
/// Kabul edilmiş teklif sürümünün değişmez görünümü. Yalnızca ana toplama giren kalemler (opsiyonel ve alternatifler seçilmişse dahil) bulunur;
/// maliyet ve iç not yoktur. <see cref="DocumentSha256"/> müşterinin gördüğü sabit belgenin özetidir (kabul bu özete bağlıdır).
/// </summary>
public sealed record AcceptedQuote(
    Guid QuoteId,
    string QuoteNumber,
    int RevisionNo,
    string Title,
    Guid CompanyId,
    Guid? ContactId,
    Guid? OpportunityId,
    string Currency,
    bool PriceIncludesTax,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    DateTimeOffset AcceptedAt,
    string? AcceptedBy,
    string? DocumentSha256,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? CommercialTerms,
    IReadOnlyList<AcceptedQuoteLine> Lines)
{
    /// <summary>
    /// Müşterinin ortak taraf kaydı (core.party, ADR-0006). Teklif CRM firmasına bağlıdır; sözleşme gibi CRM'den bağımsız modüller ortak kaydı kullanır.
    /// Boşsa (eski veri) devralan modül müşteriyi başka yoldan seçtirmelidir.
    /// </summary>
    public Guid? PartyId { get; init; }

    /// <summary>Teklifin kişisinin ortak kişi kaydı (core.person); kişi yoksa boş.</summary>
    public Guid? PersonId { get; init; }

    /// <summary>Kabul yöntemi: <c>Portal</c> (müşteri kendi sayfasından, belge özetine bağlı) ya da <c>Manual</c> (satış kullanıcısı kaydetti; belge kanıtı yoktur).</summary>
    public string AcceptanceMethod { get; init; } = "Manual";

    /// <summary>Kabulü kaydeden iç kullanıcı (yalnızca manuel kabulde); portal kabulünde boştur.</summary>
    public Guid? AcceptedByUserId { get; init; }

    /// <summary>Müşterinin kabul ifadesi/gerekçesi (portal ifadesi ya da manuel kabul notu).</summary>
    public string? AcceptanceNote { get; init; }

    /// <summary>Teklifin geçerlilik bitişi. Geçerlilik kabul anında denetlenmiştir; dönüşüm bu tarihe bakmaz, yalnızca kayıt için taşınır.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    public Guid? OwnerId { get; init; }

    /// <summary>Sunulduğu anda sabitlenen müşteri ünvanı.</summary>
    public string? CustomerName { get; init; }

    /// <summary>Firma kartındaki fatura/teslimat adresinin metin kopyası; adres yoksa boş.</summary>
    public string? BillingAddress { get; init; }

    public string? DeliveryAddress { get; init; }

    public string? ReportingCurrency { get; init; }

    public decimal? ExchangeRate { get; init; }

    public DateOnly? ExchangeRateDate { get; init; }
}

/// <summary>
/// Kabul edilmiş teklifi sonraki modüllere verir. Görünürlük çağıranın izin ve veri kapsamıyla belirlenir; teklif bulunamazsa, kapsam dışıysa
/// ya da henüz kabul edilmemişse <c>null</c> döner. Kabul olayı için ayrıca <c>crm.quote</c> kaydının güncelleme olayı yayımlanır
/// (durum <c>Accepted</c>); o olayı dinleyen modül bu arayüzle ayrıntıyı okur.
/// </summary>
public interface IAcceptedQuoteSource
{
    Task<AcceptedQuote?> GetAsync(Guid quoteId, CancellationToken cancellationToken);
}
