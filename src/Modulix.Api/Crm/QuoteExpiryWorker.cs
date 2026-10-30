using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Infrastructure.Persistence;
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
/// kapsamında çalışır; CRM modülü o tenant için kapalıysa hiçbir şey yapılmaz. Birden fazla uygulama örneği aynı anda çalışabilir: tur, tenant
/// veritabanında oturum kilidiyle (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek turu atlar (durum geçişleri zaten sürüm
/// denetimlidir, kilit yalnızca gereksiz çakışmayı ve çift bildirimi önler). Kuyruğa yazılan sorumlu e-postaları turun sonunda kaydedilir.
/// </summary>
public sealed class QuoteExpiryRunner(IServiceScopeFactory scopes, TimeProvider time)
{
    public const string LockName = "modulix.crm.quote-expiry";

    /// <summary>Kilit kaynağı tenant başınadır: tek veritabanında (ADR-0020) bir tenant'ın turu diğerini engellemez.</summary>
    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

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

        // Kilit ayrı bir bağlantıda, tur boyunca tutulur; kayıt yazımları kendi bağlantılarında olağan akışla devam eder.
        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return new QuoteExpiryRun(0, 0, "Başka bir uygulama örneği aynı tenant için çalışıyor; tur atlandı.", Skipped: true);
        }

        try
        {
            var cause = new EventEnvelope(
                Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, CrmModule.Entities.Quote, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
            services.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.quote-expiry");
            var run = await services.GetRequiredService<IQuoteExpiry>().RunAsync(time.GetUtcNow(), cancellationToken);

            // Sistem kimliği kapsamında e-posta kuyruğu, teslimle birlikte kaydedilmek üzere bağlama eklenir (EfEmailQueue); burada teslim yok,
            // bu yüzden kuyruk satırları turun sonunda açıkça kaydedilir.
            await services.GetRequiredService<PlatformDbContext>().SaveChangesAsync(cancellationToken);
            return run;
        }
        finally
        {
            await ReleaseLockAsync(lockConnection, tenantId, cancellationToken);
        }
    }

    private static async Task<bool> TryLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        command.Parameters.AddWithValue("@name", LockResource(tenantId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @name, @LockOwner = N'Session';";
            command.Parameters.AddWithValue("@name", LockResource(tenantId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Bağlantı kapanınca oturum kilidi zaten düşer.
        }
    }
}

/// <summary>
/// Teklif süre dolumunu düzenli aralıklarla çalıştırır (<c>QuoteExpiry:WorkerEnabled</c> ile kapatılır). Açık her tenant ayrı
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
                        if (run.Warning is not null && !run.Skipped)
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
