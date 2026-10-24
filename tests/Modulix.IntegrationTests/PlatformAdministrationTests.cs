using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y4: platform yöneticisi, firmalar ve modül hakları. Platform uç noktalarının yalnızca platform yöneticisine açık olması, firma
/// açılışının tek işlem olması, ilk yöneticinin davetle firmaya girmesi, kapatma ve yeniden açma, modül
/// açıp kapatma ve katalog; platform yöneticisinin firma verisini görmemesi.
/// </summary>
public partial class PlatformAdministrationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string NewPassword = "Yeni.Sifre.2024";

    [Fact]
    public async Task Platform_endpoints_are_only_for_platform_administrators_and_the_flag_is_read_on_every_request()
    {
        var tenant = await factory.CreateTenantAsync();
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        var platformAdmin = await CreatePlatformAdministratorAsync();

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/tenants")).StatusCode);

        // Firmasında yönetici olmak platform yetkisi vermez.
        using var tenantClient = await LoginAsync(tenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync("/api/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsync($"/api/platform/tenants/{tenant.Id}/close", null)).StatusCode);
        Assert.False((await tenantClient.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.IsPlatformAdministrator);

        using var client = factory.CreateBrowserClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(platformAdmin.Email!, ModulixApiFactory.Password));
        var session = (await login.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.True(session.IsPlatformAdministrator);
        Assert.Empty(session.Memberships);

        var page = await client.GetFromJsonAsync<PlatformTenantPageResponse>($"/api/platform/tenants?search={tenant.Slug}");
        var listed = Assert.Single(page!.Items);
        Assert.Equal(tenant.Id, listed.Id);
        Assert.Equal(1, listed.ActiveMemberCount);
        Assert.Equal(1, listed.AdministratorCount);

        await SetPlatformAdministratorAsync(platformAdmin, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task Opening_a_tenant_creates_roles_modules_and_the_first_administrators_invitation_together()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var slug = $"f-{Guid.NewGuid():N}"[..20];
        var email = $"ilk-{Guid.NewGuid():N}@test.modulix";

        var modules = await client.GetFromJsonAsync<PlatformModulesResponse>("/api/platform/modules");
        Assert.Contains(modules!.Modules, m => m.Key == "crm" && m.Templates.Any(t => t.Key == "crm.sales-rep"));
        Assert.DoesNotContain(modules.Modules, m => m.Key == "core");

        var response = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Yeni Firma", slug, ["crm"], ["crm.sales-rep", "core.read-only"], email, "Ilk Yonetici"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>())!;
        Assert.False(created.ExistingAccount);
        Assert.Equal(["crm"], created.Tenant.Tenant.EnabledModules);
        var administrator = Assert.Single(created.Tenant.Administrators);
        Assert.Equal(UserStatus.Invited, administrator.Status);
        Assert.Equal(email, administrator.Email);

        // İlk yönetici davet bağlantısıyla şifresini belirler ve Yönetici rolüyle firmaya girer.
        var link = await ReadInvitationAsync(email);
        Assert.Equal(administrator.MembershipId, link.Id);
        using var invited = factory.CreateBrowserClient();
        var accepted = await invited.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, NewPassword));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(created.Tenant.Tenant.Id, (await accepted.Content.ReadFromJsonAsync<MeResponse>())!.ActiveTenantId);

        var access = await invited.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.True(access!.IsAdministrator);
        var roles = await invited.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Equal(["Salt okur", "Satış temsilcisi", "Yönetici"], roles!.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, roles!.Single(r => r.IsSystem).MemberCount);

        var detail = await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{created.Tenant.Tenant.Id}");
        Assert.Equal(UserStatus.Active, Assert.Single(detail!.Administrators).Status);

        using var scope = factory.OpenScope(created.Tenant.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.tenant").ToListAsync();
        var opened = Assert.Single(audit);
        Assert.Equal(AuditAction.Created, opened.Action);
        Assert.Equal(platformAdmin.Id, opened.UserId);
    }

    [Fact]
    public async Task Failed_opening_creates_nothing_and_sends_no_email()
    {
        var existing = await factory.CreateTenantAsync();
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var email = $"ilk-{Guid.NewGuid():N}@test.modulix";

        var taken = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Aynı kısa ad", existing.Slug, ["crm"], [], email, "Ilk Yonetici"));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains(existing.Slug, await taken.Content.ReadAsStringAsync());

        // Davet aşamasında başarısız olan açılış (geçersiz e-posta) firmayı, rolleri ve modülleri geri alır.
        var slug = $"f-{Guid.NewGuid():N}"[..20];
        var invalid = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Yarım kalan", slug, ["crm"], ["crm.sales-rep"], "gecersiz-adres", "Ilk Yonetici"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("administratorEmail", await invalid.Content.ReadAsStringAsync());

        var template = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Modülsüz şablon", slug, [], ["crm.sales-rep"], email, "Ilk Yonetici"));
        Assert.Equal(HttpStatusCode.BadRequest, template.StatusCode);
        Assert.Contains("roleTemplates", await template.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.False(await db.Tenants.AnyAsync(t => t.Slug == slug));
            Assert.False(await db.Users.AnyAsync(u => u.Email == email));
            Assert.False(await db.EmailMessages.AnyAsync(e => e.ToAddress == email));
        }

        // Aynı kısa adla düzgün açılış sonra yapılabilir.
        var ok = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Yarım kalan", slug, ["crm"], ["crm.sales-rep"], email, "Ilk Yonetici"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Closed_tenant_loses_access_and_invitations_until_it_is_reopened()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        using var memberClient = await LoginAsync(member);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);

        // Yöneticisi olmayan firmaya platform yöneticisi yönetici davet eder (kurtarma yolu).
        var email = $"kurtar-{Guid.NewGuid():N}@test.modulix";
        var invite = await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/administrators",
            new InviteTenantAdministratorRequest(email, "Yeni Yonetici"));
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        var link = await ReadInvitationAsync(email);
        using var anonymous = factory.CreateBrowserClient();
        var invitationUrl = $"/api/auth/invitation?uyelik={link.Id}&token={Uri.EscapeDataString(link.Token)}";
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(invitationUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/administrators",
            new InviteTenantAdministratorRequest($"x-{Guid.NewGuid():N}@test.modulix", "Baska"))).StatusCode);

        // Üye bir sonraki isteğinde erişimi kaybeder, girişte firmayı görmez; davet bağlantısı çalışmaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);
        using var again = factory.CreateBrowserClient();
        var relogin = await again.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, ModulixApiFactory.Password));
        Assert.Empty((await relogin.Content.ReadFromJsonAsync<MeResponse>())!.Memberships);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(invitationUrl)).StatusCode);

        var closed = await client.GetFromJsonAsync<PlatformTenantPageResponse>($"/api/platform/tenants?search={tenant.Slug}&status=Closed");
        Assert.Equal(TenantState.Closed, Assert.Single(closed!.Items).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(invitationUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}", new RenameTenantRequest("Yeni Ad"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/platform/tenants/{Guid.NewGuid()}/close", null)).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var changes = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == "core.tenant" && a.RecordId == tenant.Id)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal(platformAdmin.Id, c.UserId));
        Assert.Contains("Closed", changes[0].Changes);
        Assert.Contains("Yeni Ad", changes[2].Changes);
    }

    [Fact]
    public async Task Platform_administrator_does_not_see_tenant_records()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);
    }

    [Fact]
    public async Task Turning_a_module_off_and_on_applies_on_the_next_request_keeps_roles_and_is_audited()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var member = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CrmModule.Permissions.CompaniesRead], member);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        using var memberClient = await LoginAsync(member);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/crm/companies")).StatusCode);

        // Firma kullanıcısı modül açıp kapatamaz.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/modules/crm", new SetTenantModuleRequest(false))).StatusCode);

        var url = $"/api/platform/tenants/{tenant.Id}/modules/crm";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/crm/companies")).StatusCode);

        // Aynı durum ikinci kez denetim kaydı yazmaz.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(false))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/crm/companies")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/modules/core", new SetTenantModuleRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/platform/tenants/{tenant.Id}/modules/yok", new SetTenantModuleRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/platform/tenants/{Guid.NewGuid()}/modules/crm", new SetTenantModuleRequest(true))).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var changes = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == "core.tenant" && a.RecordId == tenant.Id)
            .OrderBy(a => a.OccurredAt)
            .ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(platformAdmin.Id, c.UserId));
        Assert.Contains("crm", changes[0].Changes);
    }

    [Fact]
    public async Task Catalog_lists_modules_permissions_templates_and_how_many_tenants_use_them()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        var before = await client.GetFromJsonAsync<List<CatalogModuleDto>>("/api/platform/catalog");
        var core = before!.Single(m => m.Key == "core");
        Assert.True(core.IsAlwaysEnabled);
        Assert.Null(core.EnabledTenantCount);
        var crm = before!.Single(m => m.Key == "crm");
        Assert.Contains(crm.Permissions, p => p.Key == CrmModule.Permissions.CompaniesRead && p.ResourceName.Length > 0);
        Assert.Contains(crm.Templates, t => t.Key == "crm.sales-rep");

        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var after = await client.GetFromJsonAsync<List<CatalogModuleDto>>("/api/platform/catalog");
        Assert.True(after!.Single(m => m.Key == "crm").EnabledTenantCount > crm.EnabledTenantCount);
    }

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(user, true);
        return user;
    }

    private async Task SetPlatformAdministratorAsync(ModulixUser user, bool value)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = value;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
    }

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        var membershipId = await factory.GetMembershipIdAsync(user.Id, tenantId);
        Assert.True(await roles.SetMembershipRolesAsync(membershipId, [system.Id], CancellationToken.None));
    }

    private async Task<(Guid Id, string Token)> ReadInvitationAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var mail = await db.EmailMessages.AsNoTracking()
            .Where(e => e.ToAddress == address)
            .OrderByDescending(e => e.CreatedAt)
            .FirstAsync();
        var match = InvitationLink().Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        return (Guid.Parse(match.Groups["id"].Value), Uri.UnescapeDataString(match.Groups["token"].Value));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    [GeneratedRegex(@"/davet\?uyelik=(?<id>[0-9a-f-]+)&token=(?<token>[^\s""<]+)")]
    private static partial Regex InvitationLink();
}
