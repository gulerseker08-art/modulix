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
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Runs;

namespace Modulix.Api.Marketing;

/// <summary>"MarketingSend" yapılandırma bölümü.</summary>
public sealed class MarketingSendOptions
{
    public const string SectionName = "MarketingSend";

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="MarketingSendRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>İşlenecek iş yoksa tenant'ların yeniden denetlenme aralığı.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Bir turda iş bulunduysa bir sonraki tura kadar bekleme (sıradaki satırlar çabuk gitsin).</summary>
    public int BusyIntervalSeconds { get; set; } = 2;

    public int BatchSize { get; set; } = 100;

    /// <summary>Bir turda bir çalıştırmadan işlenecek en çok grup sayısı; bir tenant'ın uzun çalıştırması diğerlerini tutmasın.</summary>
    public int MaxBatchesPerRun { get; set; } = 5;

    public int MaxRunsPerTenant { get; set; } = 5;
}

/// <summary>
/// Bir tenant için "çalışıyor" durumundaki kampanya çalıştırmalarının sıradaki satırlarını gönderir (<see cref="ICampaignRuns.ProcessRunningAsync"/>). İş
/// "marketing.sender" sistem kimliğiyle ve kendi tenant kapsamında çalışır; pazarlama modülü o tenant için kapalıysa hiçbir şey yapılmaz. İşçi çalıştırmayı
/// başlatmaz ya da onaylamaz. Birden fazla uygulama örneği aynı anda çalışabilir: tur, tenant başına oturum kilidiyle (<c>sp_getapplock</c>) tek örnekte koşar;
/// kilidi alamayan örnek turu atlar (satır talebi zaten kilitli ve atomiktir, kilit yalnızca gereksiz çakışmayı önler).
/// </summary>
public sealed class MarketingSendRunner(IServiceScopeFactory scopes, TimeProvider time, IOptions<MarketingSendOptions> options)
{
    public const string LockName = "modulix.marketing.send";

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>Tur sonuçları; <c>Skipped</c>: başka bir örnek aynı tenant için çalıştığından tur atlandı.</summary>
    public async Task<(IReadOnlyList<RunningProcess> Runs, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;
        if (!await services.GetRequiredService<ICurrentAccess>().IsModuleEnabledAsync(MarketingModule.ModuleKey, cancellationToken))
        {
            return ([], false);
        }

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return ([], true);
        }

        try
        {
            var cause = new EventEnvelope(
                Guid.NewGuid(), tenantId, DomainEventTypes.RecordDeleted, 1, MarketingModule.Entities.CampaignRun, Guid.Empty, null, Guid.Empty,
                ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
            services.GetRequiredService<EventHandlingScope>().Begin(cause, "marketing.sender");
            var settings = options.Value;
            var runs = await services.GetRequiredService<ICampaignRuns>()
                .ProcessRunningAsync(settings.BatchSize, settings.MaxBatchesPerRun, settings.MaxRunsPerTenant, cancellationToken);
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
            return (runs, false);
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
/// Çalışan kampanya çalıştırmalarını düzenli aralıklarla işler (<c>MarketingSend:WorkerEnabled</c> ile kapatılır). Pazarlama modülü açık her tenant ayrı çalışır; biri
/// başarısız olursa diğerleri etkilenmez. Bir turda iş bulunduysa kısa, bulunmadıysa uzun beklenir. Aynı hata ileti her turda yinelenmez: yalnızca değişince günlüğe yazılır.
/// </summary>
public sealed class MarketingSendWorker(
    IServiceScopeFactory scopes, MarketingSendRunner runner, IOptions<MarketingSendOptions> options, ILogger<MarketingSendWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, string> lastErrors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var idle = TimeSpan.FromSeconds(Math.Max(5, settings.PollIntervalSeconds));
        var busy = TimeSpan.FromSeconds(Math.Max(1, settings.BusyIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                foreach (var tenantId in await MarketingTenantIdsAsync(scope.ServiceProvider, stoppingToken))
                {
                    try
                    {
                        var (runs, _) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        foreach (var run in runs)
                        {
                            worked |= run.Processed > 0;
                            Report(tenantId, run);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} pazarlama gönderimi bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pazarlama gönderim turu çalıştırılamadı.");
            }

            try
            {
                await Task.Delay(worked ? busy : idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Report(Guid tenantId, RunningProcess run)
    {
        if (run.Error is { } error)
        {
            if (!lastErrors.TryGetValue(run.RunId, out var previous) || previous != error)
            {
                lastErrors[run.RunId] = error;
                logger.LogWarning("Tenant {TenantId} çalıştırma {RunId} işlenemedi: {Error}", tenantId, run.RunId, error);
            }

            return;
        }

        lastErrors.Remove(run.RunId);
        if (run.Completed)
        {
            logger.LogInformation("Tenant {TenantId} çalıştırma {RunId} tamamlandı ({Processed} satır işlendi).", tenantId, run.RunId, run.Processed);
        }
    }

    /// <summary>Pazarlama modülü açık, etkin tenant'lar; tek platform sorgusudur.</summary>
    internal static async Task<IReadOnlyList<Guid>> MarketingTenantIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ModulixDbContext>();
        return await (
            from tenant in db.Tenants.AsNoTracking()
            join entitlement in db.ModuleEntitlements.AsNoTracking() on tenant.Id equals entitlement.TenantId
            where tenant.Status == TenantStatus.Active && entitlement.ModuleKey == MarketingModule.ModuleKey && entitlement.IsEnabled
            select tenant.Id)
            .ToListAsync(cancellationToken);
    }
}
