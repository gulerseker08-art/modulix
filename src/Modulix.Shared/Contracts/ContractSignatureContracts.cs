namespace Modulix.Shared.Contracts;

// ---- Dilim 6: imza (imzacılar, manuel doğrulama, sağlayıcı/simülasyon, olay günlüğü)

/// <summary>İmzacı girdisi. <c>Kind</c>: Internal (tenant üyesi, <c>UserId</c> zorunlu) ya da External (müşteri tarafı; ad zorunlu, e-posta isteğe bağlı, sağlayıcı yönteminde zorunlu).</summary>
public sealed record SignatoryInputDto(string Kind, Guid? UserId = null, string? Name = null, string? Email = null, string? Capacity = null, bool IsRequired = true);

/// <summary>
/// İmza isteği. <c>Method</c>: Manual (imzalı belge yüklenir, yetkili doğrular) ya da Provider (sağlayıcı/simülasyon; ayarda etkin olmalı). <c>Mode</c>: Sequential
/// (sırayla) ya da Parallel. Belge, güncel sürümün kesin (dondurulmuş sürümden üretilmiş) ve örnek olmayan belgesi olmalıdır.
/// </summary>
public sealed record StartSignatureRequest(long Version, Guid DocumentId, string Method, string Mode, IReadOnlyList<SignatoryInputDto> Signatories, int? ValidityDays = null);

/// <summary>Manuel imza kaydı: imzalı belge taraması (PDF/PNG/JPEG, en fazla 10 MB) base64 olarak gelir; doğrulayan yetkili oturumdan belirlenir.</summary>
public sealed record RecordManualSignatureRequest(DateOnly SignedOn, string FileName, string ContentBase64, string? Note = null);

public sealed record SignatureReasonRequest(string Reason);

/// <summary>Simülasyon olayı: <c>Type</c> Viewed, Signed, Declined ya da Expired. Yalnızca simülasyon sağlayıcılı isteklerde.</summary>
public sealed record SimulateSignatureRequest(int SignatoryPosition, string Type, string? Note = null);

public sealed record SignatoryDto(
    Guid Id,
    int Position,
    string Kind,
    Guid? UserId,
    string Name,
    string? Email,
    string? Capacity,
    bool IsRequired,
    string Status,
    DateTimeOffset? SentAt,
    DateTimeOffset? SignedAt,
    string? Assurance,
    Guid? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    Guid? EvidenceFileId,
    string? EvidenceFileName,
    string? DeclineReason,
    int ReminderCount,
    DateTimeOffset? LastReminderAt,
    bool CanSignNow);

public sealed record SignatureEventDto(
    Guid Id, string Source, string EventType, Guid? SignatoryId, string? ProviderEventId, int? Sequence, string Outcome, string? Note, Guid? ActorId, DateTimeOffset At);

public sealed record SignatureRequestDto(
    Guid Id,
    int VersionNo,
    Guid DocumentId,
    string DocumentSha256,
    string Method,
    string? Provider,
    string? ProviderReference,
    bool IsSimulation,
    string Mode,
    string Status,
    DateTimeOffset ExpiresAt,
    bool IsExpired,
    DateTimeOffset? CompletedAt,
    string? CloseReason,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SignatoryDto> Signatories,
    IReadOnlyList<SignatureEventDto> Events);

/// <summary>Sözleşmenin imza özeti; istekler yeniden eskiye sıralıdır. <c>ProviderEnabled</c> bu tenant'ta sağlayıcı yönteminin açık olup olmadığıdır.</summary>
public sealed record ContractSignatureDto(
    string SignatureStatus,
    int CurrentVersionNo,
    IReadOnlyList<SignatureRequestDto> Requests,
    bool ProviderEnabled,
    string? ProviderName,
    bool ProviderIsSimulation,
    int ValidityDays);

/// <summary>Sağlayıcı webhook yanıtı: <c>Outcome</c> Applied, Duplicate, OutOfOrder, Ignored ya da Rejected (hepsi 200; sağlayıcının yeniden denemesi gerekmez).</summary>
public sealed record SignatureWebhookResult(string Outcome);

/// <summary>İmza güvence düzeyleri ve yöntem etiketleri (anahtarlar sunucudaki sabitlerle aynıdır).</summary>
public static class SignatureNames
{
    public const string Manual = "Manual";
    public const string Provider = "Provider";
    public const string Sequential = "Sequential";
    public const string Parallel = "Parallel";
    public const string Internal = "Internal";
    public const string External = "External";
}
