namespace Modulix.Core.Events;

/// <summary>Platform ekranındaki teslim durumu. <see cref="Held"/>: bekliyor ama aynı kaydın önceki teslimi bitmediği için alınamıyor.</summary>
public enum EventDeliveryState
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Held = 5,
}

/// <summary>
/// Teslim süzgeci (platform ekranı). Tarih aralığı teslimin açıldığı zamana göredir: <see cref="From"/> dahil,
/// <see cref="To"/> hariç. Sayfa 1'den başlar.
/// </summary>
public sealed record EventDeliveryQuery(
    Guid? TenantId = null,
    string? Subscription = null,
    string? EventType = null,
    EventDeliveryState? State = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50,
    string? Sort = null)
{
    /// <summary>Başlığa göre sıralama anahtarları (ADR-0036); boşsa en yeni önce.</summary>
    public static readonly IReadOnlyCollection<string> SortKeys =
        ["createdAt", "-createdAt", "subscription", "-subscription", "attemptCount", "-attemptCount"];
}

/// <summary>
/// Bir teslim; payload ve alan değeri içermez. <see cref="HoldingBack"/>: başarısız sıralı teslimin beklettiği (aynı
/// abone, aynı kayıt, sonraki) teslim sayısı.
/// </summary>
public sealed record EventDeliveryItem(
    Guid Id,
    Guid EventId,
    Guid TenantId,
    string TenantName,
    string Subscription,
    string EventType,
    string EntityKey,
    Guid RecordId,
    long? Sequence,
    EventDeliveryState State,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? CompletedAt,
    string? LastError,
    int HoldingBack);

public sealed record EventDeliveryPage(IReadOnlyList<EventDeliveryItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Kuyruğun özeti: bekleyen, bekletilen ve başarısız teslim sayısı; en eski dağıtılmamış olayın ve en eski bekleyen
/// teslimin zamanı (yoksa kuyruk boş).
/// </summary>
public sealed record EventSummary(
    int Pending,
    int Held,
    int Failed,
    DateTimeOffset? OldestUndispatched,
    DateTimeOffset? OldestPending,
    IReadOnlyList<string> Subscriptions,
    IReadOnlyList<string> EventTypes);

/// <summary>
/// Olaylar (ADR-0007, platform ekranı): teslimleri süzer, yeniden dener ve atlar. Yalnızca platform yöneticisine açıktır
/// (API her istekte denetler). Her işlem teslimin tenant'ının denetim kaydına <c>core.event-delivery</c> olarak, işlemi
/// yapan platform yöneticisi adına yazılır.
/// </summary>
public interface IEventAdministration
{
    Task<EventSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<EventDeliveryPage> ListAsync(EventDeliveryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Başarısız veya bekleyen teslimleri hemen yeniden zamanlar; deneme sayacı sıfırlanır. Aynı teslim yeniden çalışır,
    /// yeni teslim açılmaz. Çalışmakta (kiralı) olan veya bitmiş teslim değişmez. Değişen teslim sayısını döner.
    /// <paramref name="ids"/> verilmezse <paramref name="filter"/>'a uyanların hepsi (en fazla <see cref="MaxBulk"/>).
    /// </summary>
    Task<int> RetryAsync(IReadOnlyCollection<Guid>? ids, EventDeliveryQuery? filter, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Başarısız veya bekleyen teslimleri atlar (<c>Skipped</c>): abone çalışmaz; sıralı abonenin aynı kayıttaki sonraki
    /// teslimleri serbest kalır. Değişen teslim sayısını döner.
    /// </summary>
    Task<int> SkipAsync(IReadOnlyCollection<Guid>? ids, EventDeliveryQuery? filter, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Toplu işlemde en fazla teslim.</summary>
    const int MaxBulk = 500;
}
