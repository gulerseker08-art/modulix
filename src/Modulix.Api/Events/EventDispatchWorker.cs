using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Platform;

namespace Modulix.Api.Events;

/// <summary>
/// Olayları düzenli aralıklarla dağıtır ve abonelere teslim eder (<see cref="EventDispatcher"/>, ADR-0007). Tur dolu
/// biterse beklemeden devam eder. Saatte bir saklama süresi dolan teslimleri ve olayları siler. Şimdilik API süreci
/// içinde çalışır (<c>Events:WorkerEnabled</c> ile kapatılır); kira kuralları ayrı süreçte çalışmaya da uygundur.
///
/// Tek veritabanında (ADR-0020 Dilim 3) her turda önce tek bir sorguyla işi olan tenant'lar bulunur; yalnızca onların
/// bağlamında dağıtım ve teslim yapılır. Tenant sayısı büyüse de boş tenant'lar için sorgu çalışmaz.
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
                var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
                foreach (var tenantId in await dispatcher.TenantsWithPendingWorkAsync(stoppingToken))
                {
                    try
                    {
                        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
                        var tenantRound = await tenantScope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(stoppingToken);
                        round = new EventRound(round.Dispatched + tenantRound.Dispatched, round.Delivered + tenantRound.Delivered);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        // Bir tenant'ın turunda hata olursa diğerleri etkilenmez; bir sonraki turda tekrar denenir.
                        logger.LogWarning(exception, "Tenant {TenantId} olayları bu turda işlenemedi.", tenantId);
                    }
                }

                // Gözlemleme ve temizlik bütün tenant'lar için tek sorgudur (tablolar RLS dışı).
                await dispatcher.MeasureAllTenantsAsync(stoppingToken);
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

/// <summary>
/// /health: herhangi bir tenant'ta dağıtılmamış olay 15 dakikadan eskiyse "degraded" (işleyici durmuş veya yetişemiyor).
/// Bütün tenant'lar tek sorguyla ölçülür (ADR-0020 Dilim 3).
/// </summary>
public sealed class EventHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var backlog = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().MeasureAllTenantsAsync(cancellationToken);
        return backlog.IsDispatchDelayed
            ? HealthCheckResult.Degraded($"En eski dağıtılmamış olay {backlog.UndispatchedAge.TotalMinutes:0} dakikalık.")
            : HealthCheckResult.Healthy();
    }
}
