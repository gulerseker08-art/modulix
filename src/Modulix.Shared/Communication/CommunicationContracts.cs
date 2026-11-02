namespace Modulix.Shared.Communication;

// Ortak iletişim tercihi API sözleşmeleri (/api/communication, ADR-0025). Kanal, amaç, durum ve neden adlarla taşınır
// (Email/Phone/Sms/WhatsApp; Marketing/Transactional/Service; Unknown/Granted/Denied; HardBounce/Complaint/InvalidAddress/OptOut/Manual).

public sealed record PreferenceDto(
    Guid Id,
    string Channel,
    string Address,
    string Purpose,
    string Status,
    string Source,
    string? EvidenceRef,
    Guid? PartyId,
    Guid? PersonId,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record PreferencePageDto(IReadOnlyList<PreferenceDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>İzin ya da ret kaydı. <c>Source</c> boşsa "manual"; API'den yalnızca manual, form, preference-center ve import kullanılır.</summary>
public sealed record SaveConsentRequest(
    string Channel,
    string Address,
    string Purpose,
    string Status,
    string? Source,
    string? EvidenceRef,
    string? Reason,
    Guid? PartyId,
    Guid? PersonId);

public sealed record ConsentEventDto(
    Guid Id,
    string FromStatus,
    string ToStatus,
    string Source,
    string? EvidenceRef,
    string? Reason,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record CheckCommunicationRequest(string Channel, string Purpose, string Address);

/// <summary><c>Outcome</c>: Allowed, Denied, Unknown. <c>Code</c> kararlıdır (ör. granted, denied, unknown, suppressed:opt-out).</summary>
public sealed record CommunicationDecisionDto(string Outcome, string Code, string Message);

public sealed record SuppressionDto(
    Guid Id,
    string Channel,
    string Address,
    string Reason,
    string Source,
    string? Note,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool IsActive,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LiftedAt,
    Guid? LiftedBy,
    string? LiftReason);

public sealed record SuppressionPageDto(IReadOnlyList<SuppressionDto> Items, int Page, int PageSize, int TotalCount);

public sealed record SaveSuppressionRequest(string Channel, string Address, string Reason, string? Note, DateTimeOffset? EndsAt);

public sealed record LiftSuppressionRequest(string Reason);
