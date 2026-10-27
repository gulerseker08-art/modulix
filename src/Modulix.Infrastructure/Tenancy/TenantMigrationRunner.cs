using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>Bir tenant'ın migration sonucu (ADR-0009, migration runner raporu).</summary>
public sealed record TenantMigrationResult(Guid TenantId, string Slug, string? DatabaseName, bool Succeeded, string? Detail);

/// <summary>Migration runner'ın toplu raporu.</summary>
public sealed record TenantMigrationReport(IReadOnlyList<TenantMigrationResult> Results)
{
    public int Succeeded => Results.Count(r => r.Succeeded);

    public int Failed => Results.Count(r => !r.Succeeded);
}

/// <summary>
/// Tenant iş veritabanlarına tenant migration'larını uygular (ADR-0009). Tek tenant veya seçili grup hedeflenebilir;
/// aynı DB'de eşzamanlı migration <c>sp_getapplock</c> ile engellenir; eşzamanlı çalıştırma sayısı sınırlanır;
/// başarısız tenant'lar raporlanır ve yeniden denenebilir. Açılışta otomatik çalıştırılmaz; CLI ('migrate-tenants')
/// veya operatör tetikler. Pilot → grup ilerlemesi için tenant alt kümesi verilebilir.
/// </summary>
public sealed class TenantMigrationRunner(
    PlatformDbContext platform,
    PlatformDatabaseOptions options,
    ITenantDatabaseRegistry registry,
    ModuleCatalog modules)
{
    public const int DefaultMaxConcurrency = 4;

    /// <summary>Hedef tenant'ları seçer: boşsa kurulumu tamamlanmış bütün tenant'lar; doluysa yalnızca verilen slug'lar.</summary>
    public async Task<TenantMigrationReport> RunAsync(
        IReadOnlyCollection<string>? slugs, int maxConcurrency, CancellationToken cancellationToken)
    {
        var query = platform.Set<Tenant>().AsNoTracking()
            .Where(t => t.DatabaseName != null && t.ProvisioningStatus == TenantProvisioningStatus.Active);
        if (slugs is { Count: > 0 })
        {
            var wanted = slugs.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
            query = query.Where(t => wanted.Contains(t.Slug));
        }

        var targets = await query
            .Select(t => new { t.Id, t.Slug, t.DatabaseName })
            .ToListAsync(cancellationToken);

        var results = new List<TenantMigrationResult>(targets.Count);
        var gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 32));
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await MigrateOneAsync(target.Id, target.Slug, target.DatabaseName!, cancellationToken);
                lock (results)
                {
                    results.Add(result);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Platform DbContext thread-safe değildir: şema sürümü güncellemeleri paralel migration'lardan SONRA sırayla yazılır.
        foreach (var result in results.Where(r => r.Succeeded))
        {
            await platform.Set<Tenant>()
                .Where(t => t.Id == result.TenantId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.SchemaVersion, result.Detail), cancellationToken);
            registry.Invalidate(result.TenantId);
        }

        return new TenantMigrationReport([.. results.OrderBy(r => r.Slug)]);
    }

    private async Task<TenantMigrationResult> MigrateOneAsync(Guid tenantId, string slug, string databaseName, CancellationToken cancellationToken)
    {
        var migratorConnection = TenantDatabaseNaming.ConnectionFor(options.MigrationsConnectionString, databaseName);
        try
        {
            // Aynı DB'de eşzamanlı migration kilidi: oturum ömrü boyunca tutulur.
            await using var lockConnection = new SqlConnection(migratorConnection);
            await lockConnection.OpenAsync(cancellationToken);
            if (!await TryAcquireLockAsync(lockConnection, databaseName, cancellationToken))
            {
                return new TenantMigrationResult(tenantId, slug, databaseName, false, "Başka bir migration bu veritabanında sürüyor.");
            }

            await using (var context = PersistenceSetup.CreateContext(migratorConnection, modules))
            {
                var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pending.Any())
                {
                    await context.Database.MigrateAsync(cancellationToken);
                }
            }

            // Uygulanan şema sürümü sonuçta taşınır; platform yazımı paralel turdan sonra sırayla yapılır.
            var version = await TenantProvisioningService.LastAppliedMigrationAsync(migratorConnection, cancellationToken);
            return new TenantMigrationResult(tenantId, slug, databaseName, true, version);
        }
        catch (Exception exception) when (exception is SqlException or DbUpdateException or InvalidOperationException)
        {
            // Başarısız tenant raporlanır; diğerleri etkilenmez ve bu tenant yeniden denenebilir.
            return new TenantMigrationResult(tenantId, slug, databaseName, false, exception.Message);
        }
    }

    private static async Task<bool> TryAcquireLockAsync(SqlConnection connection, string databaseName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;";
        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.Value = $"tenant-migrate:{databaseName}";
        command.Parameters.Add(resource);
        var result = command.CreateParameter();
        result.ParameterName = "@result";
        result.DbType = System.Data.DbType.Int32;
        result.Direction = System.Data.ParameterDirection.Output;
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (int)result.Value! >= 0;
    }
}
