using System.Diagnostics.Metrics;

namespace Modulix.Infrastructure.Events;

/// <summary>
/// Olay kuyruğunun durumu (Ç14.5): en eski dağıtılmamış olayın ve en eski bekleyen teslimin zamanı, başarısız
/// (dead-letter) teslim sayısı. Zamanlar yoksa kuyruk boştur.
/// </summary>
public sealed record EventBacklog(DateTimeOffset MeasuredAt, DateTimeOffset? OldestUndispatched, DateTimeOffset? OldestPending, int Failed)
{
    /// <summary>Dağıtılmamış olay bu süreden eskiyse sağlık "degraded" olur.</summary>
    public static readonly TimeSpan DispatchDelayLimit = TimeSpan.FromMinutes(15);

    public TimeSpan UndispatchedAge => OldestUndispatched is { } at ? MeasuredAt - at : TimeSpan.Zero;

    public TimeSpan PendingAge => OldestPending is { } at ? MeasuredAt - at : TimeSpan.Zero;

    public bool IsDispatchDelayed => UndispatchedAge > DispatchDelayLimit;
}

/// <summary>
/// "Modulix.Events" ölçümleri: kuyruk yaşları ve başarısız teslim sayısı (işleyici her turda ölçer) ile biten teslim
/// sayısı (abonelik ve sonuç etiketiyle). Etiketlerde tenant, kayıt veya payload yoktur.
/// </summary>
public sealed class EventMetrics
{
    public const string MeterName = "Modulix.Events";

    private readonly Counter<long> completed;
    private EventBacklog? last;

    public EventMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        completed = meter.CreateCounter<long>("modulix.events.deliveries.completed", description: "Biten teslim denemeleri (sonuca göre).");
        meter.CreateObservableGauge(
            "modulix.events.undispatched.age", () => last?.UndispatchedAge.TotalSeconds ?? 0, "s", "En eski dağıtılmamış olayın yaşı.");
        meter.CreateObservableGauge(
            "modulix.events.deliveries.pending.age", () => last?.PendingAge.TotalSeconds ?? 0, "s", "En eski bekleyen teslimin yaşı.");
        meter.CreateObservableGauge(
            "modulix.events.deliveries.failed", () => last?.Failed ?? 0, description: "Başarısız (dead-letter) teslim sayısı.");
    }

    public void Observe(EventBacklog backlog) => last = backlog;

    public void Completed(string subscription, EventDeliveryStatus outcome) =>
        completed.Add(1, new KeyValuePair<string, object?>("subscription", subscription), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
}
