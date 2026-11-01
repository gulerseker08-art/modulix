using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Modules.Support;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;

namespace Modulix.Api.Support;

/// <summary>"SupportJobs" yapılandırma bölümü.</summary>
public sealed class SupportJobsOptions
{
    public const string SectionName = "SupportJobs";

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="SupportJobsRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 300;
}

/// <summary>
/// Bir tenant için SLA zamanlanmış işini (<see cref="ISlaScheduler"/>) çalıştırır. İş "support.scheduler" sistem kimliğiyle ve kendi tenant kapsamında çalışır;
/// destek modülü o tenant için kapalıysa hiçbir şey yapılmaz. Birden fazla uygulama örneği aynı anda çalışabilir: tur, veritabanında tenant başına oturum kilidiyle
/// (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek turu atlar. Aynı turun iki kez çalışması da güvenlidir (takipler kayıt üzerindeki tur işaretiyle bir kez üretilir).
/// </summary>
public sealed class SupportJobsRunner(IServiceScopeFactory scopes, TimeProvider time)
{
    public const string LockName = "modulix.support.jobs";

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>Tur özeti; <c>Skipped</c>: başka bir örnek aynı tenant için çalıştığından tur atlandı. <c>Run</c> boşsa tur çalışmadı.</summary>
    public async Task<(SlaSchedulerRun? Run, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;
        if (!await services.GetRequiredService<ICurrentAccess>().IsModuleEnabledAsync(SupportModule.ModuleKey, cancellationToken))
        {
            return (null, false);
        }

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return (null, true);
        }

        try
        {
            var cause = new EventEnvelope(
                Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, SupportModule.Entities.Ticket, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
            services.GetRequiredService<EventHandlingScope>().Begin(cause, "support.scheduler");
            var run = await services.GetRequiredService<ISlaScheduler>().RunAsync(cancellationToken);
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
            return (run, false);
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
/// SLA zamanlanmış işini düzenli aralıklarla çalıştırır (<c>SupportJobs:WorkerEnabled</c> ile kapatılır). Destek modülü açık her tenant ayrı çalışır; biri başarısız
/// olursa diğerleri etkilenmez ve bir sonraki turda (kaçırılanlar dahil) yeniden denenir.
/// </summary>
public sealed class SupportJobsWorker(IServiceScopeFactory scopes, SupportJobsRunner runner, IOptions<SupportJobsOptions> options, ILogger<SupportJobsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                foreach (var tenantId in await SupportTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        var (run, _) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (run is { } r && r.Breaches + r.Escalations + r.AutoClosed > 0)
                        {
                            logger.LogInformation("Tenant {TenantId} SLA işleri: {Breaches} ihlal işaretlendi, {Escalations} eskalasyon eylemi uygulandı, {AutoClosed} talep otomatik kapatıldı.", tenantId, r.Breaches, r.Escalations, r.AutoClosed);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} SLA işleri bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "SLA işleri çalıştırılamadı.");
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

    /// <summary>Destek modülü açık olan aktif tenant'lar (tek platform sorgusu).</summary>
    internal static async Task<IReadOnlyList<Guid>> SupportTenantIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ModulixDbContext>();
        return await (
            from tenant in db.Tenants.AsNoTracking()
            join entitlement in db.ModuleEntitlements.AsNoTracking() on tenant.Id equals entitlement.TenantId
            where tenant.Status == TenantStatus.Active && entitlement.ModuleKey == SupportModule.ModuleKey && entitlement.IsEnabled
            select tenant.Id)
            .ToListAsync(cancellationToken);
    }
}
