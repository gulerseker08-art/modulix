using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// Görev 2: veritabanı kurulumunun ADR-0002'ye uyduğunu gerçek SQL Server üzerinde doğrular.
/// Docker'daki SQL Server çalışıyor olmalı (scripts\db-up.ps1).
/// </summary>
public class DatabaseSetupTests
{
    // scripts\db-up.ps1 bağlantı cümlelerini bu kimlikle user-secrets'a yazar.
    private const string UserSecretsId = "modulix-api";

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddUserSecrets(UserSecretsId)
        .AddEnvironmentVariables()
        .Build();

    [Fact]
    public async Task App_user_can_connect()
    {
        await using var connection = await OpenAsync(PersistenceSetup.AppConnectionName);

        Assert.Equal(1, await ScalarAsync<int>(connection, "SELECT 1"));
    }

    [Fact]
    public async Task App_user_is_not_database_owner()
    {
        await using var connection = await OpenAsync(PersistenceSetup.AppConnectionName);

        var isOwner = await ScalarAsync<int>(connection, "SELECT IS_ROLEMEMBER(N'db_owner')");

        Assert.Equal(0, isOwner);
    }

    [Fact]
    public async Task App_user_cannot_change_schema()
    {
        // Uygulama kullanıcısı tablo oluşturabilseydi Row-Level Security'yi de atlayabilirdi.
        await using var connection = await OpenAsync(PersistenceSetup.AppConnectionName);

        await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync(connection, "CREATE TABLE [core].[ShouldNotExist] ([Id] int NOT NULL)"));
    }

    [Fact]
    public async Task All_schemas_exist()
    {
        await using var connection = await OpenAsync(PersistenceSetup.MigrationsConnectionName);

        var existing = new List<string>();
        await using (var command = new SqlCommand("SELECT [name] FROM sys.schemas", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                existing.Add(reader.GetString(0));
            }
        }

        var missing = DatabaseSchemas.All.Except(existing).ToList();
        Assert.True(missing.Count == 0, $"Eksik şema(lar): {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Migrations_are_applied_and_recorded_in_platform_schema()
    {
        await using var connection = await OpenAsync(PersistenceSetup.MigrationsConnectionName);

        var applied = await ScalarAsync<int>(
            connection,
            "SELECT COUNT(*) FROM [platform].[__EFMigrationsHistory]");

        Assert.True(applied >= 1, "Hiç migration uygulanmamış. scripts\\db-up.ps1 çalıştırın.");
    }

    private static async Task<SqlConnection> OpenAsync(string connectionName)
    {
        var connectionString = Configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{connectionName} bulunamadı. Önce scripts\\db-up.ps1 çalıştırın.");
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
