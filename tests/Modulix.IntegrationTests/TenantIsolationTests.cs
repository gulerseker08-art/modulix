using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// Görev 6: tenant izolasyonu iki katmanda (ADR-0002).
/// Katman 1 (uygulama): sorgu filtresi ve kaydetme kuralları.
/// Katman 2 (veritabanı): SQL Server Row-Level Security; filtre atlansa bile korur.
/// </summary>
public class TenantIsolationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Query_filter_returns_only_active_tenant_rows()
    {
        var (a, b, key) = await TwoTenantsWithSameSettingAsync();

        using var scope = factory.OpenScope(a);
        var rows = await Db(scope).TenantSettings.Where(s => s.Key == key).ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(a, row.TenantId);
        Assert.Equal("a", row.Value);
        Assert.NotEqual(b, row.TenantId);
    }

    [Fact]
    public async Task Row_level_security_hides_other_tenant_rows_even_without_query_filter()
    {
        var (a, _, key) = await TwoTenantsWithSameSettingAsync();

        using var scope = factory.OpenScope(a);
        var rows = await Db(scope).TenantSettings.IgnoreQueryFilters().Where(s => s.Key == key).ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(a, row.TenantId);
    }

    [Fact]
    public async Task Without_active_tenant_no_tenant_rows_are_visible()
    {
        // ADR-0020: bütün tenant'lar tek veritabanındadır; aktif tenant yoksa oturum değeri yazılmaz ve Row-Level
        // Security hiçbir tenant satırı göstermez (sorgu filtresi atlansa bile).
        var (_, _, key) = await TwoTenantsWithSameSettingAsync();

        using var scope = factory.OpenScope(tenantId: null);
        var count = await Db(scope).TenantSettings.IgnoreQueryFilters().CountAsync(s => s.Key == key);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Writing_without_active_tenant_is_rejected()
    {
        using var scope = factory.OpenScope(tenantId: null);
        var db = Db(scope);
        db.TenantSettings.Add(new TenantSetting(UniqueKey(), "x"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_rows_share_one_table_and_are_separated_by_tenant_id()
    {
        // Fiziksel kanıt: iki tenant'ın satırı aynı tabloda, farklı TenantId ile durur; oturum tenant'ı hangisi yazılırsa
        // yalnızca o satır görünür (ADR-0002 katman 2).
        var (a, b, key) = await TwoTenantsWithSameSettingAsync();

        const string sql = "SELECT COUNT(*) FROM [core].[TenantSettings] WHERE [Key] = @key AND [TenantId] = @tenant";
        var aSeesA = await factory.ScalarAsync<int>(a, sql, ("@key", key), ("@tenant", a));
        var aSeesB = await factory.ScalarAsync<int>(a, sql, ("@key", key), ("@tenant", b));
        var bSeesB = await factory.ScalarAsync<int>(b, sql, ("@key", key), ("@tenant", b));

        Assert.Equal(1, aSeesA);
        Assert.Equal(0, aSeesB);
        Assert.Equal(1, bSeesB);
    }

    [Fact]
    public async Task Tenant_id_is_assigned_from_active_tenant()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var setting = new TenantSetting(UniqueKey(), "x");

        using (var scope = factory.OpenScope(a))
        {
            var db = Db(scope);
            db.TenantSettings.Add(setting);
            await db.SaveChangesAsync();
        }

        Assert.Equal(a, setting.TenantId);
    }

    [Fact]
    public async Task Database_blocks_insert_for_another_tenant_even_with_raw_sql()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;

        using var scope = factory.OpenScope(a);
        var key = UniqueKey();

        await Assert.ThrowsAsync<SqlException>(() => Db(scope).Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [core].[TenantSettings] ([Id], [TenantId], [Key], [Value], [UpdatedAt]) VALUES ({Guid.NewGuid()}, {b}, {key}, N'x', SYSDATETIMEOFFSET())"));
    }

    [Fact]
    public async Task Database_blocks_moving_a_row_to_another_tenant()
    {
        var (a, b, key) = await TwoTenantsWithSameSettingAsync();

        using var scope = factory.OpenScope(a);

        await Assert.ThrowsAsync<SqlException>(() => Db(scope).Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [core].[TenantSettings] SET [TenantId] = {b} WHERE [Key] = {key}"));
    }

    [Fact]
    public async Task Raw_sql_update_cannot_touch_other_tenant_rows()
    {
        var (a, b, key) = await TwoTenantsWithSameSettingAsync();

        using (var scope = factory.OpenScope(a))
        {
            await Db(scope).Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [core].[TenantSettings] SET [Value] = N'degisti' WHERE [Key] = {key}");
        }

        using (var scope = factory.OpenScope(b))
        {
            var row = await Db(scope).TenantSettings.SingleAsync(s => s.Key == key);
            Assert.Equal("b", row.Value);
        }
    }

    [Fact]
    public async Task Application_cannot_switch_tenant_inside_a_connection()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;

        using var scope = factory.OpenScope(a);

        await Assert.ThrowsAsync<SqlException>(() => Db(scope).Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sys.sp_set_session_context @key = N'TenantId', @value = {b}"));
    }

    [Fact]
    public async Task Application_user_cannot_disable_the_security_policy()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        using var scope = factory.OpenScope(a);

        await Assert.ThrowsAsync<SqlException>(() => Db(scope).Database.ExecuteSqlRawAsync(
            $"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = OFF)"));
    }

    [Fact]
    public async Task Every_tenant_table_is_protected_by_row_level_security()
    {
        // Modül tabloları dahil: ITenantEntity olan her tablo politikada olmalı (migration'da TenantIsolationSql.AddTable).
        var tenant = await factory.CreateTenantAsync();
        using var scope = factory.OpenScope(tenant.Id);
        var tenantTables = Db(scope).Model.GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType))
            .Select(t => $"{t.GetSchema()}.{t.GetTableName()}")
            .ToHashSet();
        Assert.Contains("crm.Companies", tenantTables);

        var options = scope.ServiceProvider.GetRequiredService<ModulixDatabaseOptions>();
        await using var connection = new SqlConnection(options.MigrationsConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT s.name + '.' + t.name, COUNT(*)
            FROM sys.security_predicates p
            JOIN sys.tables t ON t.object_id = p.target_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            GROUP BY s.name, t.name
            """,
            connection);

        var protectedTables = new Dictionary<string, int>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                protectedTables[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        // Her tabloda üç koşul: okuma filtresi, ekleme ve güncelleme engeli.
        var missing = tenantTables.Where(t => protectedTables.GetValueOrDefault(t) != 3).Order().ToList();
        Assert.True(missing.Count == 0, $"Row-Level Security politikasında olmayan tenant tablosu: {string.Join(", ", missing)}");
    }

    private async Task<(Guid A, Guid B, string Key)> TwoTenantsWithSameSettingAsync()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        var key = UniqueKey();

        foreach (var (tenant, value) in new[] { (a, "a"), (b, "b") })
        {
            using var scope = factory.OpenScope(tenant);
            var db = Db(scope);
            db.TenantSettings.Add(new TenantSetting(key, value));
            await db.SaveChangesAsync();
        }

        return (a, b, key);
    }

    private static ModulixDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

    private static string UniqueKey() => $"test.{Guid.NewGuid():N}";
}
