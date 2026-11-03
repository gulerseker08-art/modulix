using Microsoft.Extensions.Options;
using Modulix.Infrastructure.Automation;

namespace Modulix.Api.Automation;

/// <summary>
/// Kural çalışmalarını düzenli aralıklarla işler (<c>Automation:WorkerEnabled</c> ile kapatılır, <c>Automation:Enabled</c> platform acil
/// durdurma anahtarıdır). Kural yeteneği beyan eden bir modülü açık her tenant ayrı çalışır; biri başarısız olursa diğerleri etkilenmez.
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
