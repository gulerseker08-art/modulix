using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Api.Contracts;

/// <summary>"ContractJobs" yapılandırma bölümü.</summary>
public sealed class ContractJobsOptions
{
    public const string SectionName = "ContractJobs";

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="ContractJobsRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 900;
}

/// <summary>
/// Bir tenant için sözleşme zamanlanmış işlerini (<see cref="IContractScheduler"/>) çalıştırır. İş "contracts.scheduler" sistem kimliğiyle ve kendi tenant kapsamında çalışır;
/// sözleşme modülü o tenant için kapalıysa hiçbir şey yapılmaz. Birden fazla uygulama örneği aynı anda çalışabilir: tur, veritabanında tenant başına oturum kilidiyle
/// (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek turu atlar. Aynı turun iki kez çalışması da güvenlidir (dönem kararı, hatırlatma ve eskalasyon benzersiz kayıtlarla
/// bir kez üretilir). Sistem kimliği altındaki e-posta kuyruğu satırları turun sonunda kaydedilir.
/// </summary>
public sealed class ContractJobsRunner(IServiceScopeFactory scopes, TimeProvider time)
{
    public const string LockName = "modulix.contracts.jobs";

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>Tur özeti; <c>Skipped</c>: başka bir örnek aynı tenant için çalıştığından tur atlandı. <c>Run</c> boşsa tur çalışmadı.</summary>
    public async Task<(ContractSchedulerRun? Run, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;
        if (!await services.GetRequiredService<ICurrentAccess>().IsModuleEnabledAsync(ContractsModule.ModuleKey, cancellationToken))
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
                Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, ContractsModule.Entities.Contract, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
            services.GetRequiredService<EventHandlingScope>().Begin(cause, "contracts.scheduler");
            var run = await services.GetRequiredService<IContractScheduler>().RunAsync(cancellationToken);
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
/// Sözleşme zamanlanmış işlerini düzenli aralıklarla çalıştırır (<c>ContractJobs:WorkerEnabled</c> ile kapatılır). Sözleşme modülü açık her tenant ayrı çalışır; biri başarısız
/// olursa diğerleri etkilenmez ve bir sonraki turda (kaçırılanlar dahil) yeniden denenir.
/// </summary>
public sealed class ContractJobsWorker(IServiceScopeFactory scopes, ContractJobsRunner runner, IOptions<ContractJobsOptions> options, ILogger<ContractJobsWorker> logger) : BackgroundService
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
                foreach (var tenantId in await ContractTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        var (run, skipped) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (run is { } r && (r.AutoRenewed + r.RemindersCreated + r.EscalationsCreated + r.PeriodsExpired + r.Terminated + r.Activated + r.SignaturesExpired + r.ApprovalReminders + r.CommitmentsBreached) > 0)
                        {
                            logger.LogInformation(
                                "Tenant {TenantId} sözleşme işleri: {Activated} aktif, {Expired} süresi doldu, {Terminated} fesih, {Auto} otomatik uzatma, {Reminders} hatırlatma, {Escalations} eskalasyon, {Signatures} imza süresi, {Approvals} onay hatırlatması, {Breached} taahhüt ihlali.",
                                tenantId, r.Activated, r.PeriodsExpired, r.Terminated, r.AutoRenewed, r.RemindersCreated, r.EscalationsCreated, r.SignaturesExpired, r.ApprovalReminders, r.CommitmentsBreached);
                        }

                        _ = skipped;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} sözleşme işleri bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Sözleşme işleri çalıştırılamadı.");
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

    /// <summary>Sözleşme modülü açık olan aktif tenant'lar (tek platform sorgusu).</summary>
    internal static async Task<IReadOnlyList<Guid>> ContractTenantIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ModulixDbContext>();
        return await (
            from tenant in db.Tenants.AsNoTracking()
            join entitlement in db.ModuleEntitlements.AsNoTracking() on tenant.Id equals entitlement.TenantId
            where tenant.Status == TenantStatus.Active && entitlement.ModuleKey == ContractsModule.ModuleKey && entitlement.IsEnabled
            select tenant.Id)
            .ToListAsync(cancellationToken);
    }
}
