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

// İletişim konuları (ADR-0025, dilim 14). Konu yalnızca daraltır: pazarlama iznini vermez. Anahtar küçük harf, rakam ve tire (ör. haber-bulteni).

public sealed record TopicDto(Guid Id, string Key, string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt);

/// <summary>Konuyu oluşturur ya da günceller (anahtar değişmez).</summary>
public sealed record SaveTopicRequest(string Key, string Name, bool IsActive);

public sealed record TopicPreferenceDto(
    Guid Id, string Channel, string Address, string TopicKey, bool OptedOut, string Source, Guid? ChangedBy, DateTimeOffset ChangedAt);

public sealed record TopicPreferencePageDto(IReadOnlyList<TopicPreferenceDto> Items, int Page, int PageSize, int TotalCount);

/// <summary><c>OptedOut</c> doluysa iletişim noktası konudan çıkar; boşsa konuya döner. <c>Source</c> boşsa "manual".</summary>
public sealed record SaveTopicPreferenceRequest(string Channel, string Address, string TopicKey, bool OptedOut, string? Source);

// Oturumsuz tercih merkezi (ADR-0025, dilim 6). Adres maskelidir; durum: subscribed, unsubscribed, blocked, no-consent.

public sealed record PublicCommunicationTokenRequest(string Token);

/// <summary>Tercih merkezindeki bir konu: <c>Subscribed</c> kapalıysa kişi konudan çıkmıştır; <c>CanChange</c> kişinin değiştirip değiştiremeyeceğidir.</summary>
public sealed record PublicTopicDto(string Key, string Name, bool Subscribed, bool CanChange);

public sealed record PublicTopicRequest(string Token, string TopicKey, bool Subscribed);

/// <param name="Topics">Açık konular; yalnızca durum "subscribed" iken dolu olabilir.</param>
public sealed record PublicCommunicationView(
    string Channel, string MaskedAddress, string State, bool CanUnsubscribe, bool CanResubscribe, string? Message, string TenantName, DateTimeOffset ExpiresAt,
    IReadOnlyList<PublicTopicDto>? Topics = null);
