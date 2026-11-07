using Modulix.Infrastructure.Approvals;

namespace Modulix.Api.Approvals;

/// <summary>Süresi geçen onay adımlarını düzenli aralıkla işler (<c>Approvals:WorkerEnabled</c> ile kapatılır). Bir firmanın hatası diğerlerini etkilemez.</summary>
public sealed class ApprovalEscalationWorker(ApprovalEscalationRunner runner, IConfiguration configuration, ILogger<ApprovalEscalationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Approvals:WorkerEnabled", true))
        {
            return;
        }

        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (Guid tenantId in await runner.TenantIdsAsync(stoppingToken))
                {
                    try
                    {
                        int count = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (count > 0)
                        {
                            logger.LogInformation("Tenant {TenantId}: süresi geçen {Count} onay adımı işlendi.", tenantId, count);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} onay süre aşımı turu çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Onay süre aşımı turu başlatılamadı.");
            }
        }
    }
}
