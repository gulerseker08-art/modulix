namespace Modulix.Shared.Orders;

// Satış devri API sözleşmeleri (ADR-0023, dilim 3). Durum, tur, gönderen/kabul eden ve takip alanları sunucu tarafından yazılır; istekte gönderilmez.
// PUT tam durum gönderir (platform kuralı): gönderilmeyen düzenlenebilir alan temizlenir.

public static class OrderHandoverAccess
{
    public const string HandoversWrite = "orders.handovers.write";
    public const string HandoversSend = "orders.handovers.send";
    public const string HandoversAccept = "orders.handovers.accept";
}

public sealed record HandoverDto(
    Guid Id,
    long Version,
    Guid OrderId,
    string Status,
    int Round,
    string? Scope,
    string? OutOfScope,
    string? Deliverables,
    string? Commitments,
    string? Location,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    DateOnly? TargetStartDate,
    DateOnly? TargetEndDate,
    string Priority,
    string? RequiredDocuments,
    string? CustomerPrerequisites,
    string? PaymentConditions,
    string? Risks,
    string? SpecialRequests,
    string? Dependencies,
    Guid? ResponsibleUserId,
    string? ResponsibleTeam,
    string? CompletionCriteria,
    string? AcceptanceCriteria,
    string? InfoRequest,
    DateTimeOffset? SentAt,
    Guid? SentBy,
    DateTimeOffset? AcceptedAt,
    Guid? AcceptedBy,
    Guid? OperationsActivityId,
    Guid? SalesActivityId,
    int EscalatedRound);

public sealed record SaveHandoverRequest(
    long Version,
    string? Scope = null,
    string? OutOfScope = null,
    string? Deliverables = null,
    string? Commitments = null,
    string? Location = null,
    string? ContactName = null,
    string? ContactPhone = null,
    string? ContactEmail = null,
    DateOnly? TargetStartDate = null,
    DateOnly? TargetEndDate = null,
    string Priority = "Normal",
    string? RequiredDocuments = null,
    string? CustomerPrerequisites = null,
    string? PaymentConditions = null,
    string? Risks = null,
    string? SpecialRequests = null,
    string? Dependencies = null,
    Guid? ResponsibleUserId = null,
    string? ResponsibleTeam = null,
    string? CompletionCriteria = null,
    string? AcceptanceCriteria = null);

public sealed record HandoverItemDto(Guid Id, long Version, int Position, string Text, bool IsRequired, bool IsDone, Guid? DoneBy, DateTimeOffset? DoneAt, string? Note, bool IsDefault);

/// <summary>Kontrol listesi maddesi: oluşturmada <paramref name="HandoverId"/> ve <paramref name="Text"/>; güncellemede <paramref name="Version"/> ve tamamlandı/not.</summary>
public sealed record SaveHandoverItemRequest(Guid? HandoverId = null, string? Text = null, bool IsDone = false, string? Note = null, long? Version = null);

public sealed record HandoverEventDto(Guid Id, int Round, string Kind, DateTimeOffset At, Guid? ActorId, string? Note);

public sealed record HandoverActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresNote);

public sealed record HandoverDetailDto(
    HandoverDto Handover, IReadOnlyList<HandoverItemDto> Items, IReadOnlyList<string> Missing, IReadOnlyList<HandoverActionDto> Actions, IReadOnlyList<HandoverEventDto> History);

/// <summary>Devir eylemi isteği: kayıt sürümü zorunlu; bilgi isteği ve bilgi sonrası yeniden gönderimde not zorunlu; kabulde sorumlu atanabilir.</summary>
public sealed record HandoverActionRequest(long Version, string? Note = null, Guid? ResponsibleUserId = null);
