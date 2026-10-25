using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ç23.2 tablo kapısı: veritabanındaki her tablo sınıflandırılmıştır. Platform şeması dışındaki her tablo tenant verisidir ve
/// Row-Level Security altındadır (okuma filtresi, ekleme ve güncelleme engeli). Platform şemasındaki tablolar RLS dışındadır
/// ve aşağıda gerekçesiyle listelenir. Yeni tablo bu listeye veya RLS politikasına girmeden testi geçemez; model ile
/// migration'lar da ayrışamaz.
/// </summary>
public class TableInventoryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    /// <summary>Platform tabloları (RLS dışında). TenantId taşıyanların neden tenant bağlamı dışında okunduğu yazılıdır.</summary>
    private static readonly Dictionary<string, string> PlatformTables = new()
    {
        ["platform.__EFMigrationsHistory"] = "Migration geçmişi.",
        ["platform.Tenants"] = "Firma listesi; platform yöneticisi ve oturum doğrulaması okur.",
        ["platform.Users"] = "Kimlik hesabı firmadan bağımsızdır; bir kişi birden fazla firmaya üye olabilir.",
        ["platform.UserClaims"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserLogins"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserTokens"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserSessions"] = "Oturumlar kişiye aittir, firmaya değil (Y7a).",
        ["platform.Memberships"] = "TenantId: üyelik, firma seçilmeden önce (giriş, firma listesi) doğrulanır.",
        ["platform.ModuleEntitlements"] = "TenantId: modül hakkını platform yöneticisi yönetir; firma yazamaz.",
        ["platform.OutboxMessages"] = "TenantId: olay işleyicisi bütün firmaları tarar; abone olayın firmasında çalışır (ADR-0007).",
        ["platform.EventDeliveries"] = "TenantId: aynı gerekçe; payload içermez (ADR-0007).",
        ["platform.EmailMessages"] = "TenantId: e-posta işleyicisi bütün firmaların kuyruğunu gönderir.",
    };

    [Fact]
    public async Task Every_table_is_row_level_secured_or_a_listed_platform_table()
    {
        var tables = await TablesAsync();

        var problems = new List<string>();
        foreach (var (table, rls) in tables)
        {
            var platform = table.StartsWith(DatabaseSchemas.Platform + ".", StringComparison.Ordinal);
            if (platform && !PlatformTables.ContainsKey(table))
            {
                problems.Add($"{table}: platform tablosu listede yok; gerekçesiyle TableInventoryTests.PlatformTables'a ekleyin.");
            }
            else if (!platform && rls != 3)
            {
                problems.Add($"{table}: tenant tablosu RLS politikasında değil ({rls}/3 koşul); migration'da TenantIsolationSql.AddTable.");
            }
            else if (platform && rls != 0)
            {
                problems.Add($"{table}: platform tablosu RLS politikasında; tenant verisiyse platform şemasından taşıyın.");
            }
        }

        problems.AddRange(PlatformTables.Keys.Where(t => !tables.ContainsKey(t)).Select(t => $"{t}: listede var, veritabanında yok."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Tenant verisi (ITenantEntity) platform şemasında durmaz; platform dışındaki her entity tenant verisidir.</summary>
    [Fact]
    public void Tenant_entities_and_schemas_agree()
    {
        using var scope = factory.OpenScope(tenantId: null);
        var mismatched = scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Model.GetEntityTypes()
            .Where(t => t.GetTableName() is not null && t.BaseType is null)
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) == (t.GetSchema() == DatabaseSchemas.Platform))
            .Select(t => $"{t.ClrType.Name} ({t.GetSchema()}.{t.GetTableName()})")
            .Order()
            .ToList();

        Assert.True(mismatched.Count == 0, "Şeması tenant sınıfıyla uyuşmayan entity: " + string.Join(", ", mismatched));
    }

    [Fact]
    public void Model_has_no_changes_without_a_migration()
    {
        using var scope = factory.OpenScope(tenantId: null);
        Assert.False(
            scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Database.HasPendingModelChanges(),
            "Modelde migration'ı olmayan değişiklik var: dotnet ef migrations add <Ad> (CLAUDE.md).");
    }

    // Tablo → RLS koşulu sayısı (migration kullanıcısı politikayı görebilir).
    private async Task<Dictionary<string, int>> TablesAsync()
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString(PersistenceSetup.MigrationsConnectionName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT s.name + '.' + t.name,
                   (SELECT COUNT(*) FROM sys.security_predicates p WHERE p.target_object_id = t.object_id)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            """,
            connection);

        var tables = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables[reader.GetString(0)] = reader.GetInt32(1);
        }

        return tables;
    }
}
