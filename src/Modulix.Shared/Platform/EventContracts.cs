namespace Modulix.Shared.Platform;

// Olaylar (ADR-0007) platform ekranı sözleşmeleri: /api/platform/events. Payload ve alan değeri içermez.

public enum EventDeliveryStateDto
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,

    /// <summary>Bekliyor ama aynı kaydın önceki teslimi bitmediği için alınamıyor.</summary>
    Held = 5,
}

/// <summary>Kuyruğun özeti ve süzgeç seçenekleri. Zamanlar boşsa kuyruk boştur.</summary>
public sealed record EventSummaryResponse(
    int Pending,
    int Held,
    int Failed,
    DateTimeOffset? OldestUndispatched,
    DateTimeOffset? OldestPending,
    IReadOnlyList<string> Subscriptions,
    IReadOnlyList<string> EventTypes);

/// <summary><see cref="HoldingBack"/>: başarısız sıralı teslimin beklettiği teslim sayısı.</summary>
public sealed record EventDeliveryDto(
    Guid Id,
    Guid EventId,
    Guid TenantId,
    string TenantName,
    string Subscription,
    string EventType,
    string EntityKey,
    Guid RecordId,
    long? Sequence,
    EventDeliveryStateDto State,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? CompletedAt,
    string? LastError,
    int HoldingBack);

public sealed record EventDeliveryPageResponse(IReadOnlyList<EventDeliveryDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Teslim süzgeci; tarih aralığı gün olarak (UTC), <see cref="To"/> dahil.</summary>
public sealed record EventDeliveryFilterDto(
    Guid? TenantId = null,
    string? Subscription = null,
    string? EventType = null,
    EventDeliveryStateDto? State = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>
/// Yeniden dene / atla. <see cref="Ids"/> verilirse yalnızca onlar, verilmezse <see cref="Filter"/>'a uyanların hepsi
/// (en fazla 500). Çalışmakta veya bitmiş teslim değişmez.
/// </summary>
public sealed record EventDeliveryActionRequest(IReadOnlyList<Guid>? Ids, EventDeliveryFilterDto? Filter);

/// <summary>Değişen teslim sayısı.</summary>
public sealed record EventDeliveryActionResponse(int Changed);

public static class EventStateText
{
    public static string Of(EventDeliveryStateDto state) => state switch
    {
        EventDeliveryStateDto.Pending => "Bekliyor",
        EventDeliveryStateDto.Succeeded => "Başarılı",
        EventDeliveryStateDto.Failed => "Başarısız",
        EventDeliveryStateDto.Skipped => "Atlandı",
        EventDeliveryStateDto.Held => "Bekletiliyor",
        _ => state.ToString(),
    };
}
