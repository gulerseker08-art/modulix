namespace Modulix.Shared.Orders;

// Teslimat ve hizmet başlangıcı API sözleşmeleri (ADR-0023, dilim 5). Durum, gerçekleşen tarih/kişi ve teslim alan bilgisi sunucu tarafından
// eylemlerle yazılır; genel PUT yalnızca planlanan tarih, not ve gecikme gerekçesini değiştirir. Teslim edilen miktar yalnızca teslim eylemiyle artar.

public static class OrderFulfillmentAccess
{
    public const string FulfillmentRecord = "orders.fulfillment.record";
    public const string DeliveryEntityKey = "orders.delivery";
    public const string ServiceStartEntityKey = "orders.service-start";

    public static readonly IReadOnlyList<string> AllowedEvidenceTypes = ["application/pdf", "image/png", "image/jpeg"];
    public const int MaxEvidenceBytes = 2 * 1024 * 1024;
}

public sealed record DeliveryDto(
    Guid Id,
    long Version,
    Guid OrderId,
    Guid? JobId,
    string Number,
    string Status,
    DateOnly PlannedDate,
    DateOnly? ActualDate,
    string? ReceiverName,
    string? ReceiverNote,
    string? FailureReason,
    Guid? RescheduledFromId,
    string? RescheduleReason,
    string? Notes,
    DateTimeOffset? DeliveredAt,
    Guid? DeliveredBy,
    Guid? CreatedBy);

public sealed record SaveDeliveryRequest(long Version, DateOnly? PlannedDate = null, string? Notes = null);

public sealed record ServiceStartDto(
    Guid Id,
    long Version,
    Guid OrderId,
    Guid OrderLineId,
    string Status,
    DateOnly? PlannedStartDate,
    DateTimeOffset? ActualStartAt,
    Guid? StartedBy,
    DateTimeOffset? CustomerConfirmedAt,
    string? CustomerConfirmedBy,
    string? ConfirmationNote,
    string? DelayReason,
    DateTimeOffset? CompletedAt,
    Guid? CompletedBy,
    Guid? CreatedBy);

public sealed record SaveServiceStartRequest(long Version, DateOnly? PlannedStartDate = null, string? DelayReason = null);

public sealed record QuantityDto(Guid LineId, decimal Quantity);

public sealed record DeliveryLineDto(Guid OrderLineId, string Name, string? Unit, decimal Planned, decimal Delivered);

public sealed record DeliveryEvidenceDto(Guid Id, string FileName, string ContentType, int SizeBytes, string Sha256, string? Description, DateTimeOffset At, Guid? UploadedBy);

public sealed record FulfillmentActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

public sealed record DeliveryViewDto(
    Guid Id, long Version, Guid OrderId, Guid? JobId, string Number, string Status, DateOnly PlannedDate, DateOnly? ActualDate, string? ReceiverName, string? ReceiverNote,
    string? FailureReason, Guid? RescheduledFromId, string? RescheduleReason, string? Notes, DateTimeOffset? DeliveredAt, Guid? DeliveredBy, bool IsOverdue,
    IReadOnlyList<DeliveryLineDto> Lines, IReadOnlyList<DeliveryEvidenceDto> Evidence, IReadOnlyList<FulfillmentActionDto> Actions);

public sealed record ProductLineDto(Guid LineId, string Name, string? Unit, decimal Ordered, decimal Effective, decimal Delivered, decimal Remaining, decimal PlannedOpen);

public sealed record ServiceStartViewDto(
    Guid Id, long Version, string Status, DateOnly? PlannedStartDate, DateTimeOffset? ActualStartAt, Guid? StartedBy, DateTimeOffset? CustomerConfirmedAt, string? CustomerConfirmedBy,
    string? ConfirmationNote, string? DelayReason, DateTimeOffset? CompletedAt, Guid? CompletedBy);

public sealed record ServiceLineDto(
    Guid LineId, string Name, string Kind, decimal Quantity, string? RecurringPeriod, ServiceStartViewDto? Start, IReadOnlyList<string> Blockers,
    IReadOnlyList<FulfillmentActionDto> Actions, bool OngoingObligation, bool IsOverdue);

public sealed record FulfillmentDto(
    Guid OrderId, string OrderNumber, string Status, string? ContractNumber, IReadOnlyList<ProductLineDto> Products, IReadOnlyList<DeliveryViewDto> Deliveries,
    IReadOnlyList<ServiceLineDto> Services);

/// <summary>Teslimat planı: kalemler boşsa her ürün kaleminin kalan (planlanmamış) miktarı planlanır.</summary>
public sealed record PlanDeliveryRequest(DateOnly PlannedDate, IReadOnlyList<QuantityDto>? Lines = null, Guid? JobId = null, string? Notes = null);

/// <summary>
/// Teslimat eylemi. deliver: teslim alan kişi zorunlu, gerçekleşen tarih (boşsa bugün) ve kalem miktarları (boşsa planlanan); fail/cancel: gerekçe zorunlu;
/// reschedule: yeni planlanan tarih ve gerekçe zorunlu.
/// </summary>
public sealed record DeliveryActionRequest(
    long Version, string? Reason = null, string? ReceiverName = null, string? ReceiverNote = null, DateOnly? ActualDate = null, IReadOnlyList<QuantityDto>? Lines = null,
    DateOnly? PlannedDate = null);

public sealed record PlanServiceRequest(Guid OrderLineId, DateOnly? PlannedStartDate = null);

public sealed record ServiceActionRequest(long Version, string? ConfirmedBy = null, string? Note = null);
