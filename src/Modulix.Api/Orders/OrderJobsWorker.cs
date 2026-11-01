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
using Modulix.Modules.Orders;
using Modulix.Modules.Orders.Lifecycle;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;

namespace Modulix.Api.Orders;

/// <summary>"OrderJobs" yapılandırma bölümü.</summary>
public sealed class OrderJobsOptions
{
    public const string SectionName = "OrderJobs";

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="OrderJobsRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 900;
}

/// <summary>
/// Bir tenant için sipariş zamanlanmış işlerini (<see cref="IOrderScheduler"/>) çalıştırır. İş "orders.scheduler" sistem kimliğiyle ve kendi tenant kapsamında çalışır;
/// sipariş modülü o tenant için kapalıysa hiçbir şey yapılmaz. Birden fazla uygulama örneği aynı anda çalışabilir: tur, veritabanında tenant başına oturum kilidiyle
/// (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek turu atlar. Aynı turun iki kez çalışması da güvenlidir (takipler kayıt üzerindeki tur işaretiyle bir kez üretilir).
/// </summary>
public sealed class OrderJobsRunner(IServiceScopeFactory scopes, TimeProvider time)
{
    public const string LockName = "modulix.orders.jobs";

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>Tur özeti; <c>Skipped</c>: başka bir örnek aynı tenant için çalıştığından tur atlandı. <c>Run</c> boşsa tur çalışmadı.</summary>
    public async Task<(OrderSchedulerRun? Run, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;
        if (!await services.GetRequiredService<ICurrentAccess>().IsModuleEnabledAsync(OrdersModule.ModuleKey, cancellationToken))
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
                Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, OrdersModule.Entities.Order, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
            services.GetRequiredService<EventHandlingScope>().Begin(cause, "orders.scheduler");
            var run = await services.GetRequiredService<IOrderScheduler>().RunAsync(cancellationToken);
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
/// Sipariş zamanlanmış işlerini düzenli aralıklarla çalıştırır (<c>OrderJobs:WorkerEnabled</c> ile kapatılır). Sipariş modülü açık her tenant ayrı çalışır; biri başarısız
/// olursa diğerleri etkilenmez ve bir sonraki turda (kaçırılanlar dahil) yeniden denenir.
/// </summary>
public sealed class OrderJobsWorker(IServiceScopeFactory scopes, OrderJobsRunner runner, IOptions<OrderJobsOptions> options, ILogger<OrderJobsWorker> logger) : BackgroundService
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
                foreach (var tenantId in await OrderTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        var (run, _) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (run is { } r && r.HandoverEscalations + r.UnassignedJobAlerts + r.OverdueJobAlerts > 0)
                        {
                            logger.LogInformation("Tenant {TenantId} sipariş işleri: {Escalations} devir kabul gecikmesi, {Unassigned} atama bekleyen iş, {Overdue} geciken iş takibi.", tenantId, r.HandoverEscalations, r.UnassignedJobAlerts, r.OverdueJobAlerts);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} sipariş işleri bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Sipariş işleri çalıştırılamadı.");
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

    /// <summary>Sipariş modülü açık olan aktif tenant'lar (tek platform sorgusu).</summary>
    internal static async Task<IReadOnlyList<Guid>> OrderTenantIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ModulixDbContext>();
        return await (
            from tenant in db.Tenants.AsNoTracking()
            join entitlement in db.ModuleEntitlements.AsNoTracking() on tenant.Id equals entitlement.TenantId
            where tenant.Status == TenantStatus.Active && entitlement.ModuleKey == OrdersModule.ModuleKey && entitlement.IsEnabled
            select tenant.Id)
            .ToListAsync(cancellationToken);
    }
}
