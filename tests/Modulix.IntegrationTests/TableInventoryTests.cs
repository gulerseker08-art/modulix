using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ç23.2 / ADR-0009 tablo kapısı: iki veritabanı türündeki her tablo sınıflandırılmıştır.
/// Platform veritabanında yalnızca listelenen platform tabloları bulunur (RLS dışında). Tenant veritabanında platform
/// şemasındaki kuyruk tabloları dışında her tablo Row-Level Security altındadır (okuma filtresi, ekleme ve güncelleme
/// engeli). Yeni tablo bu listelere veya RLS politikasına girmeden testi geçemez; model ile migration'lar da ayrışamaz.
/// </summary>
public class TableInventoryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    /// <summary>Platform veritabanındaki tablolar (RLS dışında). TenantId taşıyanların neden merkezi olduğu yazılıdır.</summary>
    private static readonly Dictionary<string, string> PlatformTables = new()
    {
        ["platform.__EFMigrationsHistory"] = "Platform migration geçmişi.",
        ["platform.Tenants"] = "Firma listesi + tenant→DB defteri ve kurulum durumu (ADR-0009).",
        ["platform.Users"] = "Kimlik hesabı firmadan bağımsızdır; bir kişi birden fazla firmaya üye olabilir.",
        ["platform.UserClaims"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserLogins"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserTokens"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserSessions"] = "Oturumlar kişiye aittir, firmaya değil (Y7a).",
        ["platform.Memberships"] = "TenantId: üyelik, firma seçilmeden önce (giriş, firma listesi) doğrulanır.",
        ["platform.ModuleEntitlements"] = "TenantId: modül hakkını platform yöneticisi yönetir; firma yazamaz.",
        ["platform.EmailMessages"] = "TenantId (boş olabilir): e-posta kuyruğu merkezidir; giriş anındaki e-postaları da taşır (ADR-0009).",
    };

    /// <summary>Tenant veritabanında RLS dışında kalan tablolar (gerekçesiyle). Tenant DB'si zaten tek tenant içerir.</summary>
    private static readonly Dictionary<string, string> TenantExemptTables = new()
    {
        ["core.__EFMigrationsHistory"] = "Tenant migration geçmişi.",
        ["platform.OutboxMessages"] = "TenantId: tenant DB'sine ait olay kuyruğu; dağıtım tenant bağlamında çalışır (ADR-0007/0009).",
        ["platform.EventDeliveries"] = "TenantId: aynı gerekçe; payload içermez (ADR-0007).",
    };

    [Fact]
    public async Task Platform_database_contains_only_listed_platform_tables()
    {
        var tables = await TablesAsync(factory.Services.GetRequiredService<PlatformDatabaseOptions>().MigrationsConnectionString);

        var problems = new List<string>();
        foreach (var (table, rls) in tables)
        {
            if (!PlatformTables.ContainsKey(table))
            {
                problems.Add($"{table}: platform veritabanında beklenmeyen tablo; tenant verisiyse tenant DB'sine taşıyın, değilse PlatformTables'a ekleyin.");
            }
            else if (rls != 0)
            {
                problems.Add($"{table}: platform tablosu RLS politikasında olmamalı.");
            }
        }

        problems.AddRange(PlatformTables.Keys.Where(t => !tables.ContainsKey(t)).Select(t => $"{t}: listede var, platform veritabanında yok."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Tenant_database_tables_are_row_level_secured_or_listed_exempt()
    {
        var tenant = await factory.CreateTenantAsync();
        var options = factory.Services.GetRequiredService<PlatformDatabaseOptions>();
        var databaseName = (await factory.TenantDatabaseNameAsync(tenant.Id))!;
        var tables = await TablesAsync(TenantDatabaseNaming.ConnectionFor(options.MigrationsConnectionString, databaseName));

        var problems = new List<string>();
        foreach (var (table, rls) in tables)
        {
            if (TenantExemptTables.ContainsKey(table))
            {
                if (rls != 0)
                {
                    problems.Add($"{table}: muaf tablo RLS politikasında görünüyor.");
                }
            }
            else if (rls != 3)
            {
                problems.Add($"{table}: tenant tablosu RLS politikasında değil ({rls}/3 koşul); migration'da TenantIsolationSql.AddTable.");
            }
        }

        problems.AddRange(TenantExemptTables.Keys.Where(t => !tables.ContainsKey(t)).Select(t => $"{t}: muaf listede var, tenant veritabanında yok."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Tenant verisi (ITenantEntity) platform şemasında durmaz; platform dışındaki her entity tenant verisidir.</summary>
    [Fact]
    public async Task Tenant_entities_and_schemas_agree()
    {
        using var scope = factory.OpenScope((await factory.CreateTenantAsync()).Id);
        var mismatched = scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Model.GetEntityTypes()
            .Where(t => t.GetTableName() is not null && t.BaseType is null)
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) == (t.GetSchema() == DatabaseSchemas.Platform))
            .Select(t => $"{t.ClrType.Name} ({t.GetSchema()}.{t.GetTableName()})")
            .Order()
            .ToList();

        Assert.True(mismatched.Count == 0, "Şeması tenant sınıfıyla uyuşmayan entity: " + string.Join(", ", mismatched));
    }

    /// <summary>
    /// Ç22.2 kapsam sınıfı (ADR-0008): her tenant tablosu ya ortaktır (<see cref="ICompanyShared"/>) ya da şirket kapsamlıdır
    /// (<see cref="ICompanyScoped"/>, CompanyId ile); ikisi birden olamaz. Sınıfsız yeni tablo testi kırar.
    /// </summary>
    [Fact]
    public async Task Every_tenant_entity_has_exactly_one_company_scope_class()
    {
        using var scope = factory.OpenScope((await factory.CreateTenantAsync()).Id);
        var problems = new List<string>();
        foreach (var type in scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Model.GetEntityTypes()
                     .Where(t => t.GetTableName() is not null && t.BaseType is null && typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            var shared = typeof(ICompanyShared).IsAssignableFrom(type.ClrType);
            var scoped = typeof(ICompanyScoped).IsAssignableFrom(type.ClrType);
            var name = $"{type.ClrType.Name} ({type.GetSchema()}.{type.GetTableName()})";
            if (shared == scoped)
            {
                problems.Add($"{name}: {(shared ? "hem ortak hem şirket kapsamlı" : "kapsam sınıfı yok; ICompanyShared veya ICompanyScoped uygulayın")}.");
            }
            else if (scoped && type.FindProperty(nameof(ICompanyScoped.CompanyId)) is not { IsNullable: false })
            {
                problems.Add($"{name}: şirket kapsamlı ama CompanyId zorunlu kolon değil.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Tenant_model_has_no_changes_without_a_migration()
    {
        using var scope = factory.OpenScope((await factory.CreateTenantAsync()).Id);
        Assert.False(
            scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Database.HasPendingModelChanges(),
            "Tenant modelinde migration'ı olmayan değişiklik var: dotnet ef migrations add <Ad> --context ModulixDbContext (CLAUDE.md).");
    }

    [Fact]
    public void Platform_model_has_no_changes_without_a_migration()
    {
        using var scope = factory.Services.CreateScope();
        Assert.False(
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.HasPendingModelChanges(),
            "Platform modelinde migration'ı olmayan değişiklik var: dotnet ef migrations add <Ad> --context PlatformDbContext (CLAUDE.md).");
    }

    // Tablo → RLS koşulu sayısı (migration kullanıcısı politikayı görebilir).
    private static async Task<Dictionary<string, int>> TablesAsync(string connectionString)
    {
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
