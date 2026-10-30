using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Infrastructure.Platform;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Api.Crm;

/// <summary>"QuoteExpiry" yapılandırma bölümü.</summary>
public sealed class QuoteExpiryOptions
{
    public const string SectionName = "QuoteExpiry";

    /// <summary>Arka plan işi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="QuoteExpiryRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 300;
}

/// <summary>
/// Bir tenant için teklif süre dolumu turunu çalıştırır (<see cref="IQuoteExpiry"/>). İş "crm.quote-expiry" sistem kimliğiyle ve kendi tenant
/// kapsamında çalışır; CRM modülü o tenant için kapalıysa hiçbir şey yapılmaz.
/// </summary>
public sealed class QuoteExpiryRunner(IServiceScopeFactory scopes, TimeProvider time)
{
    public async Task<QuoteExpiryRun> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;
        if (!await services.GetRequiredService<ICurrentAccess>().IsModuleEnabledAsync(CrmModule.ModuleKey, cancellationToken))
        {
            return new QuoteExpiryRun(0, 0);
        }

        var cause = new EventEnvelope(
            Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, CrmModule.Entities.Quote, Guid.Empty, null, Guid.Empty,
            ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
        services.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.quote-expiry");
        return await services.GetRequiredService<IQuoteExpiry>().RunAsync(time.GetUtcNow(), cancellationToken);
    }
}

/// <summary>
/// Teklif süre dolumunu düzenli aralıklarla çalıştırır (<c>QuoteExpiry:WorkerEnabled</c> ile kapatılır). Kurulumu tamamlanmış her tenant ayrı
/// çalışır; biri başarısız olursa diğerleri etkilenmez ve bir sonraki turda yeniden denenir.
/// </summary>
public sealed class QuoteExpiryWorker(
    IServiceScopeFactory scopes, QuoteExpiryRunner runner, IOptions<QuoteExpiryOptions> options, ILogger<QuoteExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                foreach (var tenantId in await Events.EventDispatchWorker.ActiveTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        var run = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (run.Warning is not null)
                        {
                            logger.LogWarning("Tenant {TenantId}: {Warning}", tenantId, run.Warning);
                        }

                        if (run.Expired > 0 || run.RemindersCreated > 0)
                        {
                            logger.LogInformation("Tenant {TenantId}: {Expired} teklifin süresi doldu, {Reminders} hatırlatma açıldı.", tenantId, run.Expired, run.RemindersCreated);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} teklif süre dolumu bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Teklif süre dolumu çalıştırılamadı.");
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
