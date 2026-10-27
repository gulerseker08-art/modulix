using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>
/// Tenant iş veritabanını kurar (ADR-0009): benzersiz DB adı atar, veritabanını oluşturup tenant migration'larını
/// uygular, uygulama kullanıcısına yetki verir ve durum makinesini yürütür. DB oluşturma/migration yetkisi
/// (migrator) uygulama veri erişim yetkisinden (app) ayrıdır. Kurulum idempotent'tir, eşzamanlı ikinci kurulumu
/// engeller, başarısızlıkta veritabanını otomatik silmez ve güvenle yeniden denenebilir.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>
    /// Tenant DB'sini oluşturur ve migrate eder; durumu <c>Provisioning</c> yapar. Zaten <c>Active</c> ise işlem
    /// yapmaz (idempotent). Çağıran bundan sonra tenant kapsamında başlangıç verisini yükler ve
    /// <see cref="CompleteAsync"/> ile tamamlar.
    /// </summary>
    Task<string> PrepareAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sağlık kontrolünden sonra tenant'ı <c>Active</c> yapar; iş API'lerine erişim bundan sonra açılır.</summary>
    Task CompleteAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Tenant DB'sine uygulama bağlantısıyla erişilebildiğini doğrular.</summary>
    Task<bool> IsHealthyAsync(Guid tenantId, CancellationToken cancellationToken);
}

internal sealed class TenantProvisioningService(
    PlatformDbContext platform,
    PlatformDatabaseOptions options,
    ITenantDatabaseRegistry registry,
    ModuleCatalog modules) : ITenantProvisioningService
{
    public async Task<string> PrepareAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var current = await platform.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant bulunamadı: {tenantId}.");

        if (current.ProvisioningStatus == TenantProvisioningStatus.Active && current.DatabaseName is { } active)
        {
            return active; // İdempotent: zaten kurulu.
        }

        // Atomik eşzamanlılık kilidi: yalnızca tek istek Pending/Failed → Provisioning geçişini kazanır; ikinci istek
        // 0 satır günceller. Değişiklik izleyiciyi atladığı için sonrasında yeniden okunur.
        var claimed = await platform.Set<Tenant>()
            .Where(t => t.Id == tenantId
                && (t.ProvisioningStatus == TenantProvisioningStatus.Pending
                    || t.ProvisioningStatus == TenantProvisioningStatus.Failed))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ProvisioningStatus, TenantProvisioningStatus.Provisioning), cancellationToken);
        if (claimed == 0)
        {
            var now = await platform.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken);
            return now.ProvisioningStatus == TenantProvisioningStatus.Active && now.DatabaseName is { } ready
                ? ready
                : throw new InvalidOperationException($"Tenant {tenantId} için kurulum zaten sürüyor.");
        }

        registry.Invalidate(tenantId);
        var tenant = await platform.Set<Tenant>().SingleAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant.DatabaseName is null)
        {
            tenant.AssignDatabase(TenantDatabaseNaming.Create());
            await platform.SaveChangesAsync(cancellationToken);
            registry.Invalidate(tenantId);
        }

        var databaseName = tenant.DatabaseName!;

        try
        {
            await CreateAndMigrateAsync(databaseName, cancellationToken);
        }
        catch
        {
            tenant.MarkProvisioningFailed();
            await platform.SaveChangesAsync(CancellationToken.None);
            registry.Invalidate(tenantId);
            throw; // Veritabanı silinmez; yeniden denenebilir.
        }

        return databaseName;
    }

    public async Task CompleteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await platform.Set<Tenant>().SingleAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant.ProvisioningStatus == TenantProvisioningStatus.Active)
        {
            return;
        }

        if (!await IsHealthyAsync(tenantId, cancellationToken))
        {
            tenant.MarkProvisioningFailed();
            await platform.SaveChangesAsync(cancellationToken);
            registry.Invalidate(tenantId);
            throw new InvalidOperationException($"Tenant {tenantId} veritabanı sağlık kontrolünü geçemedi.");
        }

        var appliedVersion = await LastAppliedMigrationAsync(
            TenantDatabaseNaming.ConnectionFor(options.MigrationsConnectionString, tenant.DatabaseName!), cancellationToken);
        tenant.MarkProvisioned(appliedVersion);
        await platform.SaveChangesAsync(cancellationToken);
        registry.Invalidate(tenantId);
    }

    public async Task<bool> IsHealthyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await platform.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant?.DatabaseName is not { } databaseName)
        {
            return false;
        }

        try
        {
            await using var connection = new SqlConnection(
                TenantDatabaseNaming.ConnectionFor(options.AppConnectionString, databaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tenant DB'sini oluşturur (yoksa), tenant migration'larını migrator ile uygular ve uygulama kullanıcısına yetki
    /// verir. EF'in <c>Migrate</c>'i veritabanı yoksa oluşturur.
    /// </summary>
    private async Task CreateAndMigrateAsync(string databaseName, CancellationToken cancellationToken)
    {
        var migratorConnection = TenantDatabaseNaming.ConnectionFor(options.MigrationsConnectionString, databaseName);
        await using (var context = PersistenceSetup.CreateContext(migratorConnection, modules))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }

        await GrantApplicationAccessAsync(migratorConnection, cancellationToken);
    }

    /// <summary>
    /// Yeni tenant DB'sinde uygulama kullanıcısını (app login) oluşturur ve yalnızca veri yetkisi verir (DDL yok);
    /// böylece uygulama Row-Level Security'yi kapatamaz. Entegre kimlik doğrulamada login adı olmaz; o durumda atlanır.
    /// </summary>
    private async Task GrantApplicationAccessAsync(string migratorConnection, CancellationToken cancellationToken)
    {
        var appLogin = new SqlConnectionStringBuilder(options.AppConnectionString).UserID;
        if (string.IsNullOrWhiteSpace(appLogin))
        {
            return;
        }

        await using var connection = new SqlConnection(migratorConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = TenantDatabaseProvisioningSql.GrantApplicationAccess(appLogin);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<string?> LastAppliedMigrationAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP 1 [MigrationId] FROM [{DatabaseSchemas.Core}].[__EFMigrationsHistory] ORDER BY [MigrationId] DESC;";
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}

/// <summary>
/// Yeni tenant veritabanında uygulama kullanıcısına yetki veren SQL (ADR-0009). docker/sqlserver/init.sql'deki
/// platform DB kurulumuyla aynı mantık; migrator (db_owner) tarafından her tenant DB'sinde çalıştırılır.
/// Login adı sabit karakter kümesiyle sınırlı olduğundan güvenle gömülür.
/// </summary>
internal static class TenantDatabaseProvisioningSql
{
    private const string AppRole = "modulix_app_role";

    public static string GrantApplicationAccess(string appLogin)
    {
        if (!IsSafeIdentifier(appLogin))
        {
            throw new ArgumentException("Uygulama login adı beklenmeyen karakter içeriyor.", nameof(appLogin));
        }

        var grants = string.Join(
            "\n",
            DatabaseSchemas.All.Select(schema =>
                $"IF SCHEMA_ID(N'{schema}') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[{schema}] TO [{AppRole}];"));

        // Audit tenant verisidir ama uygulama tarafından değiştirilemez (append-only): role buradan, migration'dan sonra
        // oluşturulduğu için DENY migration'da değil burada uygulanır.
        return $"""
            IF USER_ID(N'{appLogin}') IS NULL CREATE USER [{appLogin}] FOR LOGIN [{appLogin}];
            IF DATABASE_PRINCIPAL_ID(N'{AppRole}') IS NULL CREATE ROLE [{AppRole}];
            IF IS_ROLEMEMBER(N'{AppRole}', N'{appLogin}') = 0 ALTER ROLE [{AppRole}] ADD MEMBER [{appLogin}];
            {grants}
            IF OBJECT_ID(N'[core].[AuditLog]', N'U') IS NOT NULL DENY UPDATE, DELETE ON [core].[AuditLog] TO [{AppRole}];
            """;
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length is > 0 and <= 128 && value.All(c => char.IsLetterOrDigit(c) || c == '_');
}
