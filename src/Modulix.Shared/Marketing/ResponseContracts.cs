namespace Modulix.Shared.Marketing;

// Kampanya yanıtı (ADR-0025, dilim 7). Tür: Interested, Question, MeetingRequest, NotInterested, OptOut, Complaint, Other.
// Durum: New, InProgress, Converted (lead'e dönüştü), Closed. Adres yoktur; yanıt gönderilmiş bir iletiye (teslimat satırı) bağlanır.

public sealed record CampaignResponseDto(
    Guid Id,
    long Version,
    Guid CampaignId,
    Guid? RunId,
    Guid DeliveryId,
    Guid TargetId,
    Guid? PersonId,
    string Kind,
    string? Note,
    DateTimeOffset ReceivedAt,
    string Status,
    Guid? OwnerId,
    Guid? LeadId,
    Guid? ActivityId,
    DateTimeOffset? OptOutAppliedAt,
    string? ClosedReason,
    Guid? CreatedBy,
    Guid? UpdatedBy);

/// <summary>Yanıt kaydı: yalnızca sağlayıcıya verilmiş (kabul edildi ya da sonucu belirsiz) bir iletiye. Kampanya, çalıştırma ve hedef iletiden alınır.</summary>
public sealed record RegisterCampaignResponseRequest(Guid DeliveryId, string Kind, string? Note = null, DateTimeOffset? ReceivedAt = null);

/// <summary>Eylem isteği. Kapatma ve yeniden açmada gerekçe zorunludur.</summary>
public sealed record ResponseActionRequest(long? Version = null, string? Reason = null);
