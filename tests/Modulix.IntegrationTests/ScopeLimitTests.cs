using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0030 dilim 2a: platform tavanı kayıt türüne göre uygulanır. Tavan okuma tarafında kapsamı indirir (rol kaydı beklenmez),
/// yönetici varsayılan olarak muaftır, kaldırılınca eski görünürlük döner; geçersiz tür reddedilir; bir firmanın tavanı başkasını
/// etkilemez; yalnızca platform yöneticisi yazar ve işlem iki denetim kaydına düşer.
/// </summary>
public class ScopeLimitTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Company = "crm.company";

    [Fact]
    public async Task Limit_narrows_wide_roles_on_the_next_request_and_can_be_lifted()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var viewer = await LoginAsync(setup.Viewer);
        using var admin = await LoginAsync(setup.Admin);

        Assert.Equal(3, await CountCompaniesAsync(viewer));

        // Tavan "yalnızca kendi kayıtları": geniş rol bir sonraki istekte kısılır; rol kaydı değişmez.
        var put = await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)], "Pilot: şube kendi firmasını görsün"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var change = (await put.Content.ReadFromJsonAsync<SetScopeLimitsResponse>())!;
        Assert.Equal(1, change.AffectedRoleCount);
        Assert.Equal(0, await CountCompaniesAsync(viewer));

        // Yönetici varsayılan olarak muaftır; tavan yalnızca yönetici olmayan rolleri sınırlar.
        Assert.Equal(3, await CountCompaniesAsync(admin));

        // Tavan başka türleri etkilemez: kişi hâlâ kendi (ve başkasının olmayan) kayıtlarıyla çalışır, rolün izinleri aynıdır.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/crm/contacts")).StatusCode);

        // Tavan kaldırılınca eski görünürlük döner.
        var lift = await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits", new SetScopeLimitsRequest([]));
        Assert.Equal(HttpStatusCode.OK, lift.StatusCode);
        Assert.Equal(3, await CountCompaniesAsync(viewer));
    }

    [Fact]
    public async Task Limit_can_bind_the_administrator_per_tenant_and_type()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var admin = await LoginAsync(setup.Admin);
        Assert.Equal(3, await CountCompaniesAsync(admin));

        var put = await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own, AppliesToAdministrators: true)]));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Yönetici de bağlandı: yalnızca kendi kayıtlarını görür (kimliğine bağlı kayıt yok).
        Assert.Equal(0, await CountCompaniesAsync(admin));

        // Platform tavanı gevşetince yönetici eski görünürlüğüne döner (kurtarma yolu).
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/tenants/{setup.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);
        Assert.Equal(3, await CountCompaniesAsync(admin));
    }

    [Fact]
    public async Task Invalid_limits_are_rejected_and_nothing_is_written()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var url = $"/api/platform/tenants/{setup.Tenant.Id}/scope-limits";

        // Bilinmeyen tür, kapalı modülün türü, tekrar eden tür, geçersiz kapsam.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto("crm.nothing", SharedScope.Own)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto("marketing.campaign", SharedScope.Own)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own), new ScopeLimitDto(Company, SharedScope.Team)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, (SharedScope)99)]))).StatusCode);

        // Firma yok.
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"/api/platform/tenants/{Guid.NewGuid()}/scope-limits",
            new SetScopeLimitsRequest([]))).StatusCode);

        var current = (await platform.GetFromJsonAsync<TenantScopeLimitsResponse>(url))!;
        Assert.Empty(current.Limits);
        Assert.Contains(current.Types, t => t.EntityKey == Company && t.ModuleKey == "crm");
        Assert.DoesNotContain(current.Types, t => t.ModuleKey == "marketing");
    }

    [Fact]
    public async Task Limit_of_one_tenant_does_not_affect_another()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        using var viewerB = await LoginAsync(b.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/tenants/{a.Tenant.Id}/scope-limits",
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);

        Assert.Equal(3, await CountCompaniesAsync(viewerB));
        Assert.Empty((await platform.GetFromJsonAsync<TenantScopeLimitsResponse>($"/api/platform/tenants/{b.Tenant.Id}/scope-limits"))!.Limits);
        Assert.Single((await platform.GetFromJsonAsync<TenantScopeLimitsResponse>($"/api/platform/tenants/{a.Tenant.Id}/scope-limits"))!.Limits);
    }

    [Fact]
    public async Task Only_platform_administrator_writes_limits_and_the_change_is_audited()
    {
        var setup = await SetUpAsync();
        using var tenantAdmin = await LoginAsync(setup.Admin);
        var url = $"/api/platform/tenants/{setup.Tenant.Id}/scope-limits";

        // Firmanın kendi yöneticisi tavan yazamaz ve platform ucunu okuyamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync(url)).StatusCode);

        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Unit)], "Test gerekçesi"))).StatusCode);

        // Platform denetim kaydı (gerekçe ve etkilenen rol sayısıyla) ve firmanın kendi denetim kaydı.
        var platformAudit = (await platform.GetFromJsonAsync<PlatformAuditPageResponse>(
            $"/api/platform/audit?tenantId={setup.Tenant.Id}&action=tenant.scope-limits-changed"))!;
        var entry = Assert.Single(platformAudit.Items);
        Assert.Equal("Test gerekçesi", entry.Reason);
        Assert.Contains("affectedRoles", entry.Details);

        var tenantAudit = await tenantAdmin.GetStringAsync("/api/audit?entityKey=core.tenant");
        Assert.Contains("scopeLimits", tenantAudit);

        // Aynı kümeyi yeniden göndermek yeni denetim kaydı üretmez.
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Unit)]))).StatusCode);
        Assert.Single((await platform.GetFromJsonAsync<PlatformAuditPageResponse>(
            $"/api/platform/audit?tenantId={setup.Tenant.Id}&action=tenant.scope-limits-changed"))!.Items);
    }

    [Fact]
    public async Task Tenant_export_and_delete_cover_limits_and_role_overrides()
    {
        var setup = await SetUpAsync();
        var other = await SetUpAsync();
        using (var platform = await LoginAsync(await CreatePlatformAdministratorAsync()))
        {
            foreach (var tenantId in new[] { setup.Tenant.Id, other.Tenant.Id })
            {
                Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/tenants/{tenantId}/scope-limits",
                    new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);
            }
        }

        foreach (var tenantId in new[] { setup.Tenant.Id, other.Tenant.Id })
        {
            using var scope = factory.OpenScope(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<Modulix.Infrastructure.Persistence.ModulixDbContext>();
            var role = await db.Roles.FirstAsync(r => !r.IsSystem);
            db.RoleScopeOverrides.Add(new RoleScopeOverride(role.Id, Company, Scope.Unit));
            await db.SaveChangesAsync();
        }

        var directory = Path.Combine(Path.GetTempPath(), "modulix-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var scope = factory.Services.CreateScope();
            var transfer = scope.ServiceProvider.GetRequiredService<Modulix.Infrastructure.Tenancy.ITenantDataTransfer>();
            var export = await transfer.ExportAsync(setup.Tenant.Id, directory, CancellationToken.None);
            Assert.Contains(export.Tables, t => t is { Schema: "platform", Table: "TenantScopeLimits", Rows: 1 });
            Assert.Contains(export.Tables, t => t is { Schema: "core", Table: "RoleScopeOverrides", Rows: 1 });
            Assert.Contains(Company, await File.ReadAllTextAsync(Path.Combine(directory, "platform.TenantScopeLimits.json")));

            await transfer.DeleteAsync(setup.Tenant.Id, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        const string limits = "SELECT COUNT(*) FROM [platform].[TenantScopeLimits] WHERE [TenantId] = @t";
        const string overrides = "SELECT COUNT(*) FROM [core].[RoleScopeOverrides] WHERE [TenantId] = @t";
        Assert.Equal(0, await factory.ScalarAsync<int>(null, limits, ("@t", setup.Tenant.Id)));
        Assert.Equal(0, await factory.ScalarAsync<int>(setup.Tenant.Id, overrides, ("@t", setup.Tenant.Id)));

        // Diğer firmanın tavanı ve istisnası silinmedi.
        Assert.Equal(1, await factory.ScalarAsync<int>(null, limits, ("@t", other.Tenant.Id)));
        Assert.Equal(1, await factory.ScalarAsync<int>(other.Tenant.Id, overrides, ("@t", other.Tenant.Id)));
    }

    [Fact]
    public async Task Limit_of_a_closed_module_is_kept_when_the_set_is_resent_but_new_ones_cannot_be_added()
    {
        var setup = await SetUpAsync();
        using var platform = await LoginAsync(await CreatePlatformAdministratorAsync());
        var url = $"/api/platform/tenants/{setup.Tenant.Id}/scope-limits";
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);

        await factory.SetModuleAsync(setup.Tenant.Id, "crm", enabled: false);

        // Modül kapandı: tür listede yok ama tavan duruyor.
        var closed = (await platform.GetFromJsonAsync<TenantScopeLimitsResponse>(url))!;
        Assert.Single(closed.Limits);
        Assert.DoesNotContain(closed.Types, t => t.ModuleKey == "crm");

        // Ekran tüm kümeyi yeniden gönderir; mevcut tavan korunur (sessizce silinmez).
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync(url, new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own)]))).StatusCode);
        Assert.Single((await platform.GetFromJsonAsync<TenantScopeLimitsResponse>(url))!.Limits);

        // Kapalı modülün yeni bir türüne tavan konamaz.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync(url,
            new SetScopeLimitsRequest([new ScopeLimitDto(Company, SharedScope.Own), new ScopeLimitDto("crm.contact", SharedScope.Own)]))).StatusCode);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Viewer)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var rep1 = await factory.CreateUserAsync(tenant);
        var rep2 = await factory.CreateUserAsync(tenant);

        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] access = [CompaniesRead, CompaniesWrite, ContactsRead];
        await factory.CreateRoleAsync(tenant.Id, access, Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, access, Scope.Own, rep1, rep2);

        // Üç firma, üç farklı sorumlu (görüntüleyicinin kendi kaydı yok).
        foreach (var (user, name) in new[] { (rep1, "Firma 1"), (rep2, "Firma 2"), (rep1, "Firma 3") })
        {
            using var client = await LoginAsync(user);
            var created = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        return (tenant, admin, viewer);
    }

    private static async Task<int> CountCompaniesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=200"))!.TotalCount;

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
