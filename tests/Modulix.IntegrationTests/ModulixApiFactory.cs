using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.IntegrationTests;

/// <summary>
/// Gerçek SQL Server'a bağlı API. Bağlantı cümleleri yerelde user-secrets'tan (scripts\db-up.ps1),
/// CI'da ortam değişkenlerinden gelir. Her test kendi tenant ve kullanıcılarını rastgele adlarla oluşturur;
/// testler birbirini ve örnek veriyi etkilemez.
/// </summary>
public sealed class ModulixApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test.Password1";

    // Bu fabrikanın oluşturduğu tenant iş veritabanları ve platform kayıtları; Dispose yalnızca bunları temizler.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> createdDatabases = [];
    private readonly System.Collections.Concurrent.ConcurrentBag<Guid> createdTenants = [];

    // Dispose sırasında Services artık kullanılamaz (ObjectDisposedException; her test sınıfı "Test Class Cleanup Failure" verip çıkış kodunu 1 yapıyordu):
    // temizlik için gereken bağlantı ayarları tenant oluşturulurken alınır.
    private PlatformDatabaseOptions? cleanupOptions;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseEnvironment("Development")

            // E-posta işleyicisi testlerde çalışmaz; e-posta testleri işleyiciyi kendisi çağırır (EmailTests).
            .UseSetting("Email:WorkerEnabled", "false")

            // Olay işleyicisi de çalışmaz; olay testleri işleyiciyi kendisi çağırır (EventTests).
            .UseSetting("Events:WorkerEnabled", "false");

    /// <summary>Çerezleri saklayan, HTTPS üzerinden konuşan istemci (oturum çerezi Secure).</summary>
    public HttpClient CreateBrowserClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    public async Task<Tenant> CreateTenantAsync(string? name = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var tenant = new Tenant(name ?? $"Test {suffix}", $"t-{suffix}");

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Tenant iş veritabanını kur (ADR-0009); gerçek akıştaki gibi fiziksel olarak ayrı bir veritabanı oluşur.
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var databaseName = await provisioning.PrepareAsync(tenant.Id, CancellationToken.None);
        await provisioning.CompleteAsync(tenant.Id, CancellationToken.None);
        cleanupOptions ??= Services.GetRequiredService<PlatformDatabaseOptions>();
        createdDatabases.Add(databaseName);
        createdTenants.Add(tenant.Id);
        return tenant;
    }

    /// <summary>
    /// Yalnızca bu test çalıştırmasının oluşturduğu tenant veritabanlarını siler (talimat Bölüm 9: temizleme yalnızca
    /// testin oluşturduğu veritabanlarını hedefler). En iyi çaba: hata olursa test başarısını etkilemez.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !createdDatabases.IsEmpty && cleanupOptions is { } options)
        {
            try
            {

                // Platform kayıtlarını (üyelik, modül hakkı, tenant) temizle; böylece düşen tenant DB'leri platform
                // defterinde "Active" yetim kayıt bırakmaz (talimat Bölüm 9: yalnızca testin oluşturdukları).
                using (var platform = new SqlConnection(options.AppConnectionString))
                {
                    platform.Open();
                    foreach (var tenant in createdTenants.Distinct())
                    {
                        try
                        {
                            using var command = platform.CreateCommand();
                            command.CommandText =
                                "DELETE FROM [platform].[Memberships] WHERE [TenantId] = @t;" +
                                "DELETE FROM [platform].[ModuleEntitlements] WHERE [TenantId] = @t;" +
                                "DELETE FROM [platform].[Tenants] WHERE [Id] = @t;";
                            command.Parameters.AddWithValue("@t", tenant);
                            command.ExecuteNonQuery();
                        }
                        catch (SqlException)
                        {
                        }
                    }
                }

                var master = new SqlConnectionStringBuilder(options.MigrationsConnectionString) { InitialCatalog = "master" }.ConnectionString;
                using var connection = new SqlConnection(master);
                connection.Open();
                foreach (var database in createdDatabases.Distinct())
                {
                    try
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN " +
                            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
                        command.ExecuteNonQuery();
                    }
                    catch (SqlException)
                    {
                        // Temizlik en iyi çabadır; kalan test veritabanı db-reset ile toplu silinebilir.
                    }
                }
            }
            catch (SqlException)
            {
            }
        }

        base.Dispose(disposing);
    }

    public async Task<ModulixUser> CreateUserAsync(params Tenant[] memberOf)
    {
        var email = $"u-{Guid.NewGuid():N}@test.modulix";
        var user = new ModulixUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Test Kullanıcı" };

        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var tenant in memberOf)
        {
            db.Memberships.Add(new Membership(tenant.Id, user.Id));
        }

        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// İstek benzeri bir kapsam açar. <paramref name="tenantId"/> verilirse o tenant aktif sayılır
    /// (gerçek istekte bunu sunucudaki üyelik doğrulaması yapar); null ise aktif tenant yoktur.
    /// </summary>
    public IServiceScope OpenScope(Guid? tenantId)
    {
        var scope = Services.CreateScope();
        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current =
                new TenantContext(id, Guid.NewGuid(), Guid.NewGuid());
        }

        return scope;
    }

    public async Task DisableMembershipAsync(Guid userId, Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var membership = await db.Memberships.SingleAsync(m => m.UserId == userId && m.TenantId == tenantId);
        membership.Disable();
        await db.SaveChangesAsync();
    }

    public async Task CloseTenantAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Close();
        await db.SaveChangesAsync();
    }

    public async Task SetModuleAsync(Guid tenantId, string moduleKey, bool enabled)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModuleEntitlements>()
            .SetEnabledAsync(tenantId, moduleKey, enabled, CancellationToken.None);
    }

    public async Task<Guid> GetMembershipIdAsync(Guid userId, Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.Memberships.Where(m => m.UserId == userId && m.TenantId == tenantId).Select(m => m.Id).SingleAsync();
    }

    /// <summary>Tenant'ın platform defterindeki fiziksel veritabanı adı (ADR-0009); kurulmadıysa <c>null</c>.</summary>
    public async Task<string?> TenantDatabaseNameAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.DatabaseName).SingleOrDefaultAsync();
    }

    /// <summary>
    /// Belirli bir tenant veritabanında (fiziksel) tek değer okur; izolasyonun fiziksel olduğunu kanıtlamak için.
    /// Row-Level Security'yi geçmek için önce oturum tenant'ı yazılır (<paramref name="sessionTenantId"/>): aranan kaydın
    /// tenant'ı verildiğinde, kayıt o tenant'ın veritabanında görünür, başka tenant'ın veritabanında görünmez.
    /// </summary>
    public async Task<T?> ScalarInTenantDatabaseAsync<T>(
        string databaseName, Guid sessionTenantId, string sql, params (string Name, object Value)[] parameters)
    {
        using var scope = Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<PlatformDatabaseOptions>();
        await using var connection = new SqlConnection(TenantDatabaseNaming.ConnectionFor(options.AppConnectionString, databaseName));
        await connection.OpenAsync();

        await using (var session = connection.CreateCommand())
        {
            session.CommandText = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @t;";
            session.Parameters.AddWithValue("@t", sessionTenantId);
            await session.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>Provisioning servisini açığa çıkarır (idempotentlik ve yeniden deneme testleri için).</summary>
    public ITenantProvisioningService Provisioning(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

    /// <summary>
    /// Tenant'a işletici şirket ekler (ADR-0008). Fabrikanın <see cref="CreateTenantAsync"/>'i varsayılan şirket oluşturmaz
    /// (gerçek akış platform yönetimindedir); şirket testleri ihtiyaçlarını bu yardımcıyla kurar.
    /// </summary>
    public async Task<Company> CreateCompanyAsync(Guid tenantId, string? name = null, bool isDefault = false)
    {
        using var scope = OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var company = new Company(name ?? $"Şirket {Guid.NewGuid():N}"[..24], isDefault);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    /// <summary>Kullanıcıyı tenant'ın yöneticisi yapar (sistem rolü): ADR-0008'e göre bütün şirketlere erişir.</summary>
    public async Task MakeAdministratorAsync(Guid tenantId, params ModulixUser[] users)
    {
        using var scope = OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        foreach (var user in users)
        {
            var membershipId = await GetMembershipIdAsync(user.Id, tenantId);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [system.Id], CancellationToken.None));
        }
    }

    /// <summary>Üyeliğe bir işletici şirket görevi verir (ADR-0008); geçici vekâlet için tarih, varsayılan şube verilebilir.</summary>
    public async Task AssignCompanyAsync(
        Guid tenantId, Guid membershipId, Guid companyId, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null, Guid? branchId = null)
    {
        using var scope = OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.MembershipCompanies.Add(new MembershipCompany(membershipId, companyId, validFrom, validUntil, branchId));
        await db.SaveChangesAsync();
    }

    /// <summary>Şirkete bir şube ekler (ADR-0008).</summary>
    public async Task<Branch> CreateBranchAsync(Guid tenantId, Guid companyId, string? name = null)
    {
        using var scope = OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var branch = new Branch(companyId, name ?? $"Şube {Guid.NewGuid():N}"[..24]);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return branch;
    }

    /// <summary>Tenant'ta rol oluşturur ve verilen kullanıcılara (üyelikleri üzerinden) atar.</summary>
    public Task<RoleSummary> CreateRoleAsync(Guid tenantId, IReadOnlyCollection<string> permissions, params ModulixUser[] assignTo) =>
        CreateRoleAsync(tenantId, permissions, RecordScope.All, assignTo);

    /// <summary>Kayıt kapsamlı rol (Y8); verilen kullanıcıların rolleri bu rolle değiştirilir.</summary>
    public async Task<RoleSummary> CreateRoleAsync(
        Guid tenantId, IReadOnlyCollection<string> permissions, RecordScope recordScope, params ModulixUser[] assignTo)
    {
        using var scope = OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var role = await roles.CreateAsync($"Rol {Guid.NewGuid():N}"[..20], permissions, CancellationToken.None, scope: recordScope);

        foreach (var user in assignTo)
        {
            var membershipId = await GetMembershipIdAsync(user.Id, tenantId);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [role.Id], CancellationToken.None));
        }

        return role;
    }
}
