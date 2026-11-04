using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ç23.2 / ADR-0020 tablo kapısı: tek veritabanındaki her tablo sınıflandırılmıştır. Platform şemasındaki tablolar
/// (RLS dışında) burada listelenir; geri kalan her tablo Row-Level Security altındadır (okuma filtresi, ekleme ve
/// güncelleme engeli). Yeni tablo bu listeye veya RLS politikasına girmeden testi geçemez; model ile migration'lar da
/// ayrışamaz.
/// </summary>
public class TableInventoryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    /// <summary>RLS dışında kalan tablolar (gerekçesiyle). TenantId taşıyanların neden RLS dışında olduğu yazılıdır.</summary>
    private static readonly Dictionary<string, string> ExemptTables = new()
    {
        ["platform.__EFMigrationsHistory"] = "Migration geçmişi (tek zincir, ADR-0020).",
        ["platform.Tenants"] = "Firma listesi; platform geneli kayıt.",
        ["platform.Users"] = "Kimlik hesabı firmadan bağımsızdır; bir kişi birden fazla firmaya üye olabilir.",
        ["platform.UserClaims"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserLogins"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserTokens"] = "Kimlik (ASP.NET Identity).",
        ["platform.UserSessions"] = "Oturumlar kişiye aittir, firmaya değil (Y7a).",
        ["platform.Memberships"] = "TenantId: üyelik, firma seçilmeden önce (giriş, firma listesi) doğrulanır.",
        ["platform.PlatformAuditLog"] = "Platform işlemlerinin firmalar arası kaydı (ADR-0028); ekleme-yalnız, firma silinse de saklanır. TargetTenantId firma verisi değildir.",
        ["platform.ModuleEntitlements"] ="TenantId: modül hakkını platform yöneticisi yönetir; firma yazamaz.",
        ["platform.Plans"] = "Satış planı tanımı (ADR-0028 Dilim 4): TenantId yok, platform geneli; silinmez, kapatılır.",
        ["platform.PlanModules"] = "Planın modül seti (ADR-0028): TenantId yok.",
        ["platform.PlanLimits"] = "Planın limitleri (ADR-0028): TenantId yok.",
        ["platform.TenantSubscriptions"] = "TenantId: firmanın plan aboneliği; platform yöneticisi yönetir, firma yazamaz. Geçmiş korunur (EndedAt).",
        ["platform.SupportAccessGrants"] = "TenantId: destek erişimi kaydı (ADR-0028 Dilim 7); platform yöneticisi yönetir, firma yazamaz. Süresi dolan/kapatılan kayıt silinmez.",
        ["platform.LimitOverrides"] = "TenantId: limit istisnası; platform yöneticisi yönetir, gerekçe ve bitiş zorunlu. Geri alınır (RevokedAt), silinmez.",

        ["platform.TenantScopeLimits"] = "TenantId: kayıt görünürlük tavanını (ADR-0030) platform yöneticisi yazar; firma yalnızca okur.",
        ["platform.EmailMessages"] = "TenantId (boş olabilir): e-posta kuyruğu merkezidir; giriş anındaki e-postaları da taşır.",
        ["platform.OutboxMessages"] = "TenantId: olay kuyruğu; dağıtım tenant bağlamını açıkça kurar (ADR-0007).",
        ["platform.EventDeliveries"] = "TenantId: aynı gerekçe; payload içermez (ADR-0007).",
        ["platform.ActivityNodes"] = "Sektör/faaliyet kataloğu (ADR-0029): ürün standardı, TenantId yok; platform yöneticisi yönetir.",
        ["platform.Capabilities"] = "Yetenek kataloğu (ADR-0029): TenantId yok.",
        ["platform.CapabilityDependencies"] = "Yetenek bağımlılık grafiği (ADR-0029): TenantId yok.",
        ["platform.ActivityNodeCapabilities"] = "Faaliyet–yetenek eşlemesi (ADR-0029): TenantId yok.",
        ["platform.RulePackages"] = "Alan kuralı paketi (ADR-0029 K8): platform kataloğu, TenantId yok.",
        ["platform.RulePackageVersions"] = "Alan kuralı paketi sürümü (ADR-0029 K8): platform kataloğu, TenantId yok.",
    };

    [Fact]
    public async Task Every_table_is_listed_exempt_or_row_level_secured()
    {
        var tables = await TablesAsync(factory.Services.GetRequiredService<ModulixDatabaseOptions>().MigrationsConnectionString);

        var problems = new List<string>();
        foreach (var (table, rls) in tables)
        {
            if (ExemptTables.ContainsKey(table))
            {
                if (rls != 0)
                {
                    problems.Add($"{table}: muaf tablo RLS politikasında görünüyor.");
                }
            }
            else if (rls != 3)
            {
                problems.Add($"{table}: tenant tablosu RLS politikasında değil ({rls}/3 koşul); migration'da TenantIsolationSql.AddTable, platform tablosuysa ExemptTables'a gerekçesiyle ekleyin.");
            }
        }

        problems.AddRange(ExemptTables.Keys.Where(t => !tables.ContainsKey(t)).Select(t => $"{t}: muaf listede var, veritabanında yok."));
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
    public async Task Model_has_no_changes_without_a_migration()
    {
        using var scope = factory.OpenScope((await factory.CreateTenantAsync()).Id);
        Assert.False(
            scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Database.HasPendingModelChanges(),
            "Modelde migration'ı olmayan değişiklik var: dotnet ef migrations add <Ad> (CLAUDE.md).");
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
