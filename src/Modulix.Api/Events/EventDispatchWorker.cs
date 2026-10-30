using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

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
                // ADR-0020 Dilim 1: outbox ve teslimler tek veritabanında tenant bağlamıyla okunur (ADR-0020); işleyici açık her tenant'ın
                // bağlamında ayrı çalışır. Tenant kimliği platform kaydından okunur.
                var cleanup = lastCleanup is null || time.GetUtcNow() - lastCleanup >= CleanupInterval;
                await using var scope = scopes.CreateAsyncScope();
                var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
                foreach (var tenantId in await ActiveTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
                        var dispatcher = tenantScope.ServiceProvider.GetRequiredService<EventDispatcher>();
                        var tenantRound = await dispatcher.RunOnceAsync(stoppingToken);
                        round = new EventRound(round.Dispatched + tenantRound.Dispatched, round.Delivered + tenantRound.Delivered);
                        await dispatcher.MeasureAsync(stoppingToken);
                        if (cleanup)
                        {
                            await dispatcher.CleanupAsync(stoppingToken);
                        }
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

                if (cleanup)
                {
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

    /// <summary>Açık tenant'ların kimlikleri; outbox tenant bağlamında ayrı ayrı işlenir (ADR-0007).</summary>
    internal static async Task<IReadOnlyList<Guid>> ActiveTenantIdsAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        await services.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// /health: herhangi bir tenant'ta dağıtılmamış olay 15 dakikadan eskiyse "degraded" (işleyici durmuş veya yetişemiyor).
/// Outbox tenant bağlamındadır (ADR-0020 Dilim 1); açık her tenant ayrı ölçülür.
/// </summary>
public sealed class EventHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        foreach (var tenantId in await EventDispatchWorker.ActiveTenantIdsAsync(scope.ServiceProvider, cancellationToken))
        {
            try
            {
                await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
                var backlog = await tenantScope.ServiceProvider.GetRequiredService<EventDispatcher>().MeasureAsync(cancellationToken);
                if (backlog.IsDispatchDelayed)
                {
                    return HealthCheckResult.Degraded($"Tenant {tenantId}: en eski dağıtılmamış olay {backlog.UndispatchedAge.TotalMinutes:0} dakikalık.");
                }
            }
            catch (Exception)
            {
                // Bir tenant'ın ölçümünde hata olması sağlık kontrolünü tümden düşürmez; o tenant atlanır.
            }
        }

        return HealthCheckResult.Healthy();
    }
}
