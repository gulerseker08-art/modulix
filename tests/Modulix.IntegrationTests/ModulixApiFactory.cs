using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
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

namespace Modulix.IntegrationTests;

/// <summary>
/// Gerçek SQL Server'a bağlı API. Bağlantı cümleleri yerelde user-secrets'tan (scripts\db-up.ps1),
/// CI'da ortam değişkenlerinden gelir. Her test kendi tenant ve kullanıcılarını rastgele adlarla oluşturur;
/// testler birbirini ve örnek veriyi etkilemez.
/// </summary>
public sealed class ModulixApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test.Password1";

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
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public async Task<ModulixUser> CreateUserAsync(params Tenant[] memberOf)
    {
        var email = $"u-{Guid.NewGuid():N}@test.modulix";
        var user = new ModulixUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Test Kullanıcı" };

        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var membership = await db.Memberships.SingleAsync(m => m.UserId == userId && m.TenantId == tenantId);
        membership.Disable();
        await db.SaveChangesAsync();
    }

    public async Task CloseTenantAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await db.Memberships.Where(m => m.UserId == userId && m.TenantId == tenantId).Select(m => m.Id).SingleAsync();
    }

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

    /// <summary>Üyeliğe bir işletici şirket görevi verir (ADR-0008).</summary>
    public async Task AssignCompanyAsync(Guid tenantId, Guid membershipId, Guid companyId)
    {
        using var scope = OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.MembershipCompanies.Add(new MembershipCompany(membershipId, companyId));
        await db.SaveChangesAsync();
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
