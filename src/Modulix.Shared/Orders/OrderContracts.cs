namespace Modulix.Shared.Orders;

// Sipariş modülü API sözleşmeleri (ADR-0023). Durum, kaynak teklif, kabul kanıtı, ticari snapshot ve toplam alanları sunucu tarafından yazılır;
// istekte gönderilmez. Tutar alanları hassastır (orders.financials.read): izni olmayan kullanıcıya null döner.

public sealed record OrderDto(
    Guid Id,
    long Version,
    string Number,
    string Title,
    Guid PartyId,
    Guid? PersonId,
    Guid? OwnerId,
    Guid SourceQuoteId,
    string SourceQuoteNumber,
    int SourceQuoteRevisionNo,
    string? SourceQuoteSha256,
    Guid? SourceOpportunityId,
    Guid? ContractId,
    string? ContractNumber,
    string ConversionMode,
    DateTimeOffset AcceptedAt,
    string AcceptanceMethod,
    Guid? AcceptedByUserId,
    string? AcceptedByName,
    string? AcceptanceNote,
    DateTimeOffset? QuoteValidUntil,
    string Status,
    string HandoverStatus,
    string FulfillmentStatus,
    string? StatusReason,
    string? CustomerName,
    string? BillingAddress,
    string? DeliveryAddress,
    string Currency,
    bool PriceIncludesTax,
    string? ReportingCurrency,
    decimal? ExchangeRate,
    DateOnly? ExchangeRateDate,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? CommercialTerms,
    string? Commitments,
    string? OperationsNotes,
    string? Location,
    DateOnly? PlannedStartDate,
    DateOnly? PlannedDeliveryDate,
    decimal? NetTotal,
    decimal? TaxTotal,
    decimal? GrossTotal,
    Guid? CreatedBy,
    Guid? UpdatedBy,
    System.Text.Json.Nodes.JsonObject? Custom = null);

/// <summary>Taslak sipariş düzenleme isteği. Yalnızca ticari kopya olmayan düzenlenebilir alanlar vardır; boş bırakılan alan değişmez.</summary>
public sealed record SaveOrderRequest(
    long Version,
    string? Title = null,
    Guid? PersonId = null,
    Guid? OwnerId = null,
    Guid? ContractId = null,
    string? ContractNumber = null,
    string? DeliveryAddress = null,
    string? Location = null,
    DateOnly? PlannedStartDate = null,
    DateOnly? PlannedDeliveryDate = null,
    string? PaymentTerms = null,
    string? DeliveryTerms = null,
    string? CommercialTerms = null,
    string? Commitments = null,
    string? OperationsNotes = null);

public sealed record OrderLineDto(
    Guid Id,
    long Version,
    Guid OrderId,
    int Position,
    string Kind,
    Guid? ParentSourceLineKey,
    string? GroupName,
    Guid? ProductId,
    string? ProductCode,
    string Name,
    string? Description,
    decimal Quantity,
    decimal AcceptedQuantity,
    string? UnitName,
    decimal? UnitPrice,
    decimal? ListAmount,
    decimal? LineDiscount,
    decimal TaxRatePercent,
    decimal? Net,
    decimal? Tax,
    decimal? Gross,
    string? RecurringPeriod,
    Guid SourceQuoteId,
    Guid SourceQuoteLineKey,
    decimal DeliveredQuantity,
    decimal CancelledQuantity,
    bool IsCancelled,
    DateOnly? PlannedDate);

public sealed record SaveOrderLineRequest(long Version, string? Description = null, DateOnly? PlannedDate = null);

public sealed record OrderHistoryDto(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

// ---- Tekliften sipariş

public sealed record OrderLineQuantityRequest(Guid LineKey, decimal Quantity);

/// <param name="Mode"><c>Full</c> (varsayılan; teklifin tamamı) ya da <c>Partial</c> (kalem bazında miktar).</param>
public sealed record CreateOrderFromQuoteRequest(
    string? Mode = null,
    IReadOnlyList<OrderLineQuantityRequest>? Lines = null,
    string? Title = null,
    Guid? OwnerId = null,
    string? DeliveryAddress = null,
    string? Location = null,
    DateOnly? PlannedStartDate = null,
    DateOnly? PlannedDeliveryDate = null,
    string? Commitments = null,
    string? OperationsNotes = null);

public sealed record OrderSourceLineDto(
    Guid StableKey, int Position, string Kind, string? ProductCode, string Name, string? Unit, decimal AcceptedQuantity, decimal ConvertedQuantity,
    decimal RemainingQuantity, decimal Net, decimal Gross, string? RecurringPeriod);

public sealed record ExistingOrderDto(Guid Id, string Number, string Status);

public sealed record OrderSourceDto(
    Guid QuoteId, string QuoteNumber, int RevisionNo, string Title, string Currency, decimal NetTotal, decimal GrossTotal, DateTimeOffset AcceptedAt,
    string AcceptanceMethod, string? AcceptedBy, string? DocumentSha256, DateTimeOffset? ValidUntil, string? CustomerName, string? BillingAddress,
    string? DeliveryAddress, string? PaymentTerms, string? DeliveryTerms, string? CommercialTerms, bool CustomerLinked, bool FullyConverted,
    IReadOnlyList<OrderSourceLineDto> Lines, IReadOnlyList<string> Warnings, IReadOnlyList<string> Blockers, IReadOnlyList<ExistingOrderDto> ExistingOrders);

/// <summary>Arayüzün izin ve modül anahtarları (sunucudaki <c>OrdersModule</c> ile aynıdır).</summary>
public static class OrdersAccess
{
    public const string ModuleKey = "orders";
    public const string OrdersRead = "orders.orders.read";
    public const string OrdersCreate = "orders.orders.create";
    public const string OrdersWrite = "orders.orders.write";
    public const string FinancialsRead = "orders.financials.read";
    public const string OrdersApprove = "orders.orders.approve";
    public const string OrdersCancel = "orders.orders.cancel";
    public const string OrdersClose = "orders.orders.close";
    public const string OrdersForceClose = "orders.orders.force-close";

    public const string OrderEntityKey = "orders.order";
}

// ---- Dilim 2: durum geçişleri ve değişiklik talebi

/// <summary>Eylem isteği: kaydın sürümü zorunlu (eşzamanlı çakışmada 409); gerekçe bazı eylemlerde zorunludur.</summary>
public sealed record OrderTransitionRequest(long Version, string? Reason = null);

public sealed record OrderActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <summary>Kalem değişikliği: yeni etkin miktar (yalnızca azaltma; 0 kalemi iptal eder) ve/veya planlanan tarih.</summary>
public sealed record OrderLineChangeDto(Guid LineId, decimal? Quantity = null, DateOnly? PlannedDate = null);

/// <param name="Header">Başlık alanı → yeni değer (boş değer alanı temizler). İzin verilenler: deliveryAddress, location, plannedStartDate, plannedDeliveryDate, paymentTerms, deliveryTerms, commercialTerms, commitments, operationsNotes.</param>
public sealed record OrderChangeSetDto(IReadOnlyDictionary<string, string?> Header, IReadOnlyList<OrderLineChangeDto> Lines);

public sealed record OrderChangeRequestInputDto(string? Reason, IReadOnlyDictionary<string, string?>? Header = null, IReadOnlyList<OrderLineChangeDto>? Lines = null);

public sealed record OrderChangePreviewDto(
    OrderChangeSetDto Changes, IReadOnlyList<string> Operations, IReadOnlyList<string> Deliveries, IReadOnlyList<string> Contracts, decimal? NetDelta, decimal? TaxDelta);

public sealed record OrderChangeRequestDto(
    Guid Id, long Version, Guid OrderId, int Sequence, string Status, string Reason, OrderChangeSetDto Changes, IReadOnlyList<string> Operations,
    IReadOnlyList<string> Deliveries, IReadOnlyList<string> Contracts, decimal? NetDelta, decimal? TaxDelta, Guid? RequestedBy, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, string? DecisionNote, DateTimeOffset CreatedAt);

public sealed record DecideOrderChangeRequest(long Version, string? Note = null);

public sealed record WithdrawOrderChangeRequest(long Version);
