namespace Modulix.Core.Contracts.Sales;

// Kabul edilen teklifin sipariş/sözleşme gibi sonraki modüllere devri için entegrasyon sözleşmesi (ADR-0019, dilim 7). Sipariş ya da sözleşme
// modülü henüz yoktur; bu sözleşme onların CRM'e bağlanmadan (yalnızca Core.Contracts üzerinden) kabul edilmiş teklifi okuyabilmesi içindir.

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
    string? RecurringPeriod);

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
