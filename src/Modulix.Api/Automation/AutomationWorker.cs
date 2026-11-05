using Microsoft.Extensions.Options;
using Modulix.Infrastructure.Automation;

namespace Modulix.Api.Automation;

/// <summary>
/// Kural çalışmalarını düzenli aralıklarla işler (<c>Automation:WorkerEnabled</c> ile kapatılır, <c>Automation:Enabled</c> platform acil
/// durdurma anahtarıdır). Kural yeteneği beyan eden bir modülü açık her tenant ayrı çalışır; biri başarısız olursa diğerleri etkilenmez.
/// Saklama süresi dolan bitmiş çalışmalar <c>Automation:RetentionIntervalMinutes</c> aralığıyla temizlenir (<see cref="AutomationRetention"/>).
/// </summary>
public sealed class AutomationWorker(AutomationRunner runner, IOptions<AutomationOptions> options, ILogger<AutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollIntervalSeconds));
        var retentionInterval = TimeSpan.FromMinutes(Math.Max(5, options.Value.RetentionIntervalMinutes));
        var nextRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var clean = DateTimeOffset.UtcNow >= nextRetention;
                foreach (var tenantId in await runner.TenantIdsAsync(stoppingToken))
                {
                    try
                    {
                        var (round, _) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (round is { } r && r.Evaluated + r.StepsRun + r.Failed + r.Cancelled > 0)
                        {
                            logger.LogInformation(
                                "Tenant {TenantId} otomasyon turu: {Evaluated} değerlendirildi ({NotMatched} koşul sağlanmadı), {Steps} adım, {Retrying} yeniden denenecek, {Failed} başarısız, {Cancelled} iptal.",
                                tenantId, r.Evaluated, r.NotMatched, r.StepsRun, r.Retrying, r.Failed, r.Cancelled);
                        }

                        if (clean && await runner.CleanTenantAsync(tenantId, stoppingToken) is { } cleaned && cleaned.Runs + cleaned.Cursors > 0)
                        {
                            logger.LogInformation(
                                "Tenant {TenantId} otomasyon saklama temizliği: {Runs} çalışma, {Steps} adım, {Cursors} tarama imleci silindi{More}.",
                                tenantId, cleaned.Runs, cleaned.Steps, cleaned.Cursors, cleaned.More ? " (devam edecek)" : string.Empty);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} otomasyon turu çalıştırılamadı.", tenantId);
                    }
                }

                if (clean)
                {
                    nextRetention = DateTimeOffset.UtcNow + retentionInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Otomasyon tenant listesi alınamadı.");
            }

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
