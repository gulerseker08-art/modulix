namespace Modulix.Shared.Marketing;

// Kampanya içeriği, çalıştırma ve teslimat (ADR-0025, dilim 5). Çalıştırma durumu: Draft, PendingApproval, Approved, Running, Paused, Completed, Cancelled.
// Teslimat durumu: Queued, Sending, Accepted (sağlayıcı kabul etti; "teslim edildi" değildir), Failed, Skipped, Unknown (sonuç belirsiz), Cancelled.

public sealed record CampaignContentDto(Guid Id, long Version, Guid CampaignId, int VersionNo, string? Subject, string Body, Guid? CreatedBy);

/// <summary>İçerik sürümleri değişmez: her kayıt yeni bir sürümdür. Metin düzdür; bağlantılar yalnızca http/https olabilir. E-postada konu zorunludur.</summary>
public sealed record SaveCampaignContentRequest(long Version = 0, Guid? CampaignId = null, string? Subject = null, string? Body = null);

public sealed record CampaignRunDto(
    Guid Id,
    long Version,
    Guid CampaignId,
    Guid? SnapshotId,
    Guid? ContentId,
    DateTimeOffset? ScheduledAt,
    Guid? OwnerId,
    string Status,
    string? Note,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? CreatedBy,
    Guid? UpdatedBy);

/// <summary>Anlık görüntü, içerik ve planlanan zaman yalnızca taslakta değişir. Planlanan zaman UTC'dir; boşsa onaydan sonra hemen başlatılabilir.</summary>
public sealed record SaveCampaignRunRequest(
    long Version = 0, Guid? CampaignId = null, Guid? SnapshotId = null, Guid? ContentId = null, DateTimeOffset? ScheduledAt = null, Guid? OwnerId = null);

/// <summary>Eylem isteği. Ret, taslağa alma ve iptalde gerekçe zorunludur.</summary>
public sealed record RunActionRequest(long? Version = null, string? Reason = null);

public sealed record RunSummaryDto(
    string Status, int Total, IReadOnlyDictionary<string, int> Statuses, IReadOnlyDictionary<string, int> Codes, bool ProviderConfigured, string Provider,
    bool UnsubscribeLinkAvailable);

/// <summary>Test gönderimi sonucu (dilim 11): alıcı yalnızca çağıranın kendi hesap e-postasıdır, adres dönmez.</summary>
public sealed record TestSendResultDto(string Outcome, string? Code);

public sealed record ProcessResultDto(int Processed, int Accepted, int Failed, int Skipped, int Unknown, int Requeued, int Remaining, bool Completed);

public sealed record CampaignDeliveryDto(Guid Id, Guid TargetId, Guid? PersonId, string Status, string? Code, int Attempts, string? ProviderMessageId, DateTimeOffset UpdatedAt);

public sealed record CampaignDeliveryPageDto(IReadOnlyList<CampaignDeliveryDto> Items, int Page, int PageSize, int TotalCount);
