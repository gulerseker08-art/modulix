using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Communication;

public sealed record ConsentChange(
    CommunicationChannel Channel,
    string Address,
    CommunicationPurpose Purpose,
    ConsentStatus Status,
    string Source,
    string? EvidenceRef,
    string? Reason,
    Guid? PartyId = null,
    Guid? PersonId = null);

public sealed record PreferenceView(
    Guid Id,
    CommunicationChannel Channel,
    string Address,
    CommunicationPurpose Purpose,
    ConsentStatus Status,
    string Source,
    string? EvidenceRef,
    Guid? PartyId,
    Guid? PersonId,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record ConsentEventView(
    Guid Id,
    ConsentStatus FromStatus,
    ConsentStatus ToStatus,
    string Source,
    string? EvidenceRef,
    string? Reason,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record PreferenceQuery(
    CommunicationChannel? Channel, CommunicationPurpose? Purpose, ConsentStatus? Status, string? Address, int Page = 1, int PageSize = 25);

public sealed record SuppressionRequest(
    CommunicationChannel Channel, string Address, SuppressionReason Reason, string Source, string? Note = null, DateTimeOffset? EndsAt = null);

public sealed record SuppressionView(
    Guid Id,
    CommunicationChannel Channel,
    string Address,
    SuppressionReason Reason,
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

public sealed record SuppressionQuery(CommunicationChannel? Channel, string? Address, bool? ActiveOnly, int Page = 1, int PageSize = 25);

public sealed record CommunicationPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// İletişim tercihi ve engel yönetimi (ADR-0025). Çağıran izni API'de denetlenir; servis tenant'ı her çağrıda oturumdan alır.
/// Her değişiklik denetim kaydına yazılır (adres maskeli). Doğrulama hatası <see cref="ArgumentException"/>, bulunamayan
/// kayıt null döner.
/// </summary>
public interface ICommunicationAdministration
{
    Task<CommunicationPage<PreferenceView>> ListPreferencesAsync(PreferenceQuery query, CancellationToken cancellationToken);

    /// <summary>Güncel durumu (ve değiştiyse) olay satırını yazar. Aynı durumun aynı kaynakla yeniden kaydı olay üretmez.</summary>
    Task<PreferenceView> RecordConsentAsync(ConsentChange change, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConsentEventView>?> EventsAsync(Guid preferenceId, CancellationToken cancellationToken);

    Task<CommunicationPage<SuppressionView>> ListSuppressionsAsync(SuppressionQuery query, CancellationToken cancellationToken);

    /// <summary>Aynı kanal, adres ve nedenle aktif engel varsa onu döndürür (yeni satır açılmaz).</summary>
    Task<SuppressionView> SuppressAsync(SuppressionRequest request, CancellationToken cancellationToken);

    /// <summary>Engeli gerekçeyle kaldırır (satır silinmez). Zaten kaldırılmışsa değişmeden döner.</summary>
    Task<SuppressionView?> LiftAsync(Guid suppressionId, string reason, CancellationToken cancellationToken);
}
