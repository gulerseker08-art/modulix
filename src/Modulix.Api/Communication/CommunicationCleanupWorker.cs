using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Communication;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Api.Communication;

/// <summary>"CommunicationCleanup" yapılandırma bölümü.</summary>
public sealed class CommunicationCleanupOptions
{
    public const string SectionName = "CommunicationCleanup";

    /// <summary>Arka plan işi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="CommunicationCleanupRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>Temizlik turları arasındaki süre (dakika); varsayılan 6 saat.</summary>
    public int IntervalMinutes { get; set; } = 360;
}

/// <summary>
/// Bir tenant için gönderim günlüğü temizliğini çalıştırır (<see cref="CommunicationCleanup"/>). İş kendi tenant kapsamında çalışır (RLS oturum değeri). Birden fazla uygulama
/// örneği aynı anda çalışabilir: tur, tenant başına oturum kilidiyle (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek turu atlar.
/// </summary>
public sealed class CommunicationCleanupRunner(IServiceScopeFactory scopes)
{
    public const string LockName = "modulix.communication.cleanup";

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <returns>Tur sonucu; kilit başka örnekteyse null.</returns>
    public async Task<CommunicationCleanupResult?> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var tenantScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = tenantScope.ServiceProvider;

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return null;
        }

        try
        {
            return await services.GetRequiredService<CommunicationCleanup>().RunAsync(cancellationToken);
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
/// Gönderim günlüğü temizliğini düzenli aralıklarla çalıştırır (<c>CommunicationCleanup:WorkerEnabled</c> ile kapatılır). Etkin her tenant ayrı çalışır; biri başarısız
/// olursa diğerleri etkilenmez ve bir sonraki turda yeniden denenir.
/// </summary>
public sealed class CommunicationCleanupWorker(
    IServiceScopeFactory scopes, CommunicationCleanupRunner runner, IOptions<CommunicationCleanupOptions> options, ILogger<CommunicationCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, settings.IntervalMinutes));

        // Açılışta bir an beklenir: uygulama ısınırken veritabanına ek yük binmesin.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
                var tenantIds = await db.Tenants.AsNoTracking().Where(t => TenantStatusRules.Operational.Contains(t.Status)).Select(t => t.Id).ToListAsync(stoppingToken);
                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var result = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (result is { Tokens: > 0 } or { FrequencyWindows: > 0 })
                        {
                            logger.LogInformation(
                                "Tenant {TenantId} gönderim günlüğü temizliği: {Tokens} belirteç, {Windows} sıklık penceresi silindi{More}.",
                                tenantId, result.Tokens, result.FrequencyWindows, result.More ? " (devam edecek)" : string.Empty);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} gönderim günlüğü temizliği bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Gönderim günlüğü temizliği çalıştırılamadı.");
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
