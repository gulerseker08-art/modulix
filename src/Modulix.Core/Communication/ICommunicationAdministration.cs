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

public sealed record TopicView(Guid Id, string Key, string Name, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset ChangedAt);

/// <summary>Konu tercihi (ADR-0025, dilim 14): <see cref="OptedOut"/> doluysa iletişim noktası bu konudan çıkmıştır.</summary>
public sealed record TopicPreferenceChange(CommunicationChannel Channel, string Address, string TopicKey, bool OptedOut, string Source);

public sealed record TopicPreferenceView(
    Guid Id, CommunicationChannel Channel, string Address, string TopicKey, bool OptedOut, string Source, Guid? ChangedBy, DateTimeOffset ChangedAt);

public sealed record TopicPreferenceQuery(CommunicationChannel? Channel, string? Address, string? TopicKey, bool? OptedOut, int Page = 1, int PageSize = 25);

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

    Task<IReadOnlyList<TopicView>> ListTopicsAsync(bool activeOnly, CancellationToken cancellationToken);

    /// <summary>
    /// Konuyu oluşturur ya da adını/durumunu günceller (anahtar değişmez). Anahtar ve ad doğrulanır; kapalı konu yeni kampanyada seçilemez.
    /// Aynı değerle yeniden kayıt denetim kaydı üretmez.
    /// </summary>
    Task<TopicView> SaveTopicAsync(string key, string name, bool isActive, CancellationToken cancellationToken);

    Task<CommunicationPage<TopicPreferenceView>> ListTopicPreferencesAsync(TopicPreferenceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Konu çıkışı ya da konuya dönüşü kaydeder. Konu tanımlı olmalıdır (kapalı olabilir). Konuya dönüş pazarlama izni vermez; reddedilmiş bir çıkışı
    /// yalnızca kişi (tercih merkezi) ya da yetkili kullanıcı (manual, form) geri alabilir. Aynı değerin yeniden kaydı denetim kaydı üretmez.
    /// </summary>
    Task<TopicPreferenceView> RecordTopicPreferenceAsync(TopicPreferenceChange change, CancellationToken cancellationToken);
}
