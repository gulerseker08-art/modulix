namespace Modulix.Shared.Crm;

// Kabul edilen teklifin devir görünümü (ADR-0019, dilim 7). Alan adları sunucudaki entegrasyon sözleşmesiyle (IAcceptedQuoteSource) aynıdır.
// Maliyet ve iç not yoktur.

public sealed record AcceptedQuoteLineDto(
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

public sealed record AcceptedQuoteDto(
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
    IReadOnlyList<AcceptedQuoteLineDto> Lines);
