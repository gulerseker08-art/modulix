namespace Modulix.Shared.Crm;

// Teklif yönetimi (ADR-0019).
// Status: Draft | Presented | Accepted | Rejected | Expired | Cancelled | Superseded (ticari durum).
// ApprovalStatus: NotRequired | Pending | Approved | Rejected | ChangesRequested (iç onay). DeliveryStatus: NotSent | Pending | Processing | Succeeded | Failed.
// Toplamlar yalnızca sunucudan gelir; istek gövdesindeki tutarlar yok sayılır. Maliyet (UnitCost) yalnızca teklif maliyeti izni olana döner.

public sealed record QuoteDto(
    Guid Id,
    long Version,
    string QuoteNumber,
    Guid FamilyId,
    int RevisionNo,
    string Title,
    string? Description,
    Guid CompanyId,
    Guid? ContactId,
    Guid? OpportunityId,
    Guid? PriceListId,
    Guid? OwnerId,
    string Status,
    string ApprovalStatus,
    string DeliveryStatus,
    string? StatusReason,
    DateOnly IssueDate,
    DateTimeOffset? ValidUntil,
    string Currency,
    string? ReportingCurrency,
    decimal? ExchangeRate,
    DateOnly? ExchangeRateDate,
    bool PriceIncludesTax,
    string DocumentDiscountKind,
    decimal DocumentDiscountValue,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? CommercialTerms,
    string? InternalNotes,
    decimal ListTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    decimal OptionalNet,
    string? RecurringSummary,
    DateTimeOffset? CalculatedAt,
    DateTimeOffset? PresentedAt,
    DateTimeOffset? DecidedAt,
    string? CustomerNameSnapshot);

public sealed record SaveQuoteRequest(
    string Title,
    Guid CompanyId,
    string? Description = null,
    Guid? ContactId = null,
    Guid? OpportunityId = null,
    Guid? PriceListId = null,
    Guid? OwnerId = null,
    DateOnly? IssueDate = null,
    DateTimeOffset? ValidUntil = null,
    string Currency = "TRY",
    string? ReportingCurrency = null,
    decimal? ExchangeRate = null,
    DateOnly? ExchangeRateDate = null,
    bool PriceIncludesTax = false,
    string DocumentDiscountKind = "None",
    decimal DocumentDiscountValue = 0m,
    string? PaymentTerms = null,
    string? DeliveryTerms = null,
    string? CommercialTerms = null,
    string? InternalNotes = null,
    long? Version = null);

public sealed record QuoteLineDto(
    Guid Id,
    long Version,
    Guid QuoteId,
    Guid StableKey,
    int Position,
    string? GroupName,
    string Kind,
    Guid? ProductId,
    string? ProductCode,
    string Name,
    string? Description,
    decimal Quantity,
    Guid? UnitId,
    string? UnitName,
    decimal UnitPrice,
    string DiscountKind,
    decimal DiscountValue,
    decimal TaxRatePercent,
    string? TaxCode,
    string Role,
    bool IsSelected,
    Guid? ParentStableKey,
    string? RecurringPeriod,
    decimal? UnitCost,
    bool Included,
    decimal ListAmount,
    decimal LineDiscount,
    decimal DocumentDiscountShare,
    decimal Net,
    decimal Tax,
    decimal Gross);

/// <summary>UnitPrice boşsa ürün satırında fiyat listeden sunucuca hesaplanır; dolu ve listeden farklıysa elle fiyat izni gerekir.</summary>
public sealed record SaveQuoteLineRequest(
    Guid? QuoteId = null,
    string? Name = null,
    string Kind = "Product",
    Guid? ProductId = null,
    decimal Quantity = 1m,
    decimal? UnitPrice = null,
    Guid? UnitId = null,
    string DiscountKind = "None",
    decimal DiscountValue = 0m,
    decimal TaxRatePercent = 0m,
    string? TaxCode = null,
    string Role = "Standard",
    bool IsSelected = false,
    Guid? ParentStableKey = null,
    string? RecurringPeriod = null,
    string? Description = null,
    string? GroupName = null,
    int Position = 0,
    decimal? UnitCost = null,
    long? Version = null);

/// <summary>Durum geçişi isteği; <see cref="Version"/> eşzamanlılık için zorunludur, <see cref="Reason"/> reddetme/iptal/değişiklik isteğinde zorunludur.</summary>
public sealed record QuoteTransitionRequest(long Version, string? Reason = null);

public sealed record ReviseQuoteRequest(string Reason);

/// <summary>Fırsatın esas teklifini seçer (boş = kaldır); <see cref="Version"/> fırsatın sürümüdür.</summary>
public sealed record SetPrimaryQuoteRequest(long Version, Guid? QuoteId);

public sealed record QuoteHistoryDto(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>Kullanıcının görebildiği eylem; Allowed=false ise Message nedenini açıklar.</summary>
public sealed record QuoteActionStateDto(string Action, bool Allowed, string? Code, string? Message);
