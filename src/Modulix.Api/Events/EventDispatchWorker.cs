using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Modulix.Infrastructure.Events;

namespace Modulix.Api.Events;

/// <summary>
/// Olayları düzenli aralıklarla dağıtır ve abonelere teslim eder (<see cref="EventDispatcher"/>, ADR-0007). Tur dolu
/// biterse beklemeden devam eder. Saatte bir saklama süresi dolan teslimleri ve olayları siler. Şimdilik API süreci
/// içinde çalışır (<c>Events:WorkerEnabled</c> ile kapatılır); kira kuralları ayrı süreçte çalışmaya da uygundur.
/// </summary>
public sealed class EventDispatchWorker(
    IServiceScopeFactory scopes,
    IOptions<EventOptions> options,
    TimeProvider time,
    ILogger<EventDispatchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds));
        DateTimeOffset? lastCleanup = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var round = new EventRound(0, 0);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<EventDispatcher>();
                round = await dispatcher.RunOnceAsync(stoppingToken);
                await dispatcher.MeasureAsync(stoppingToken);

                if (lastCleanup is null || time.GetUtcNow() - lastCleanup >= CleanupInterval)
                {
                    await dispatcher.CleanupAsync(stoppingToken);
                    lastCleanup = time.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Ör. veritabanına ulaşılamadı; bir sonraki turda tekrar denenir.
                logger.LogError(exception, "Olaylar işlenemedi.");
            }

            if (round.Dispatched < EventDispatcher.DispatchBatchSize && round.Delivered < settings.BatchSize)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>/health: dağıtılmamış olay 15 dakikadan eskiyse "degraded" (işleyici durmuş veya yetişemiyor).</summary>
public sealed class EventHealthCheck(EventDispatcher dispatcher) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await dispatcher.MeasureAsync(cancellationToken);
        return backlog.IsDispatchDelayed
            ? HealthCheckResult.Degraded($"En eski dağıtılmamış olay {backlog.UndispatchedAge.TotalMinutes:0} dakikalık.")
            : HealthCheckResult.Healthy();
    }
}
