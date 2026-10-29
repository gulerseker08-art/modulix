using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y3: rol ekranlarının sunucu tarafı. Yetki yükseltmeye karşı kural, rol silme ve üye taşıma, kapalı modül izinleri,
/// değişikliğin bir sonraki istekte geçerli olması; hazır roller ve "kim neye erişebilir" görünümleri.
/// </summary>
public class RoleManagementTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Role_manager_who_is_not_an_administrator_can_only_grant_permissions_they_hold()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles, CompaniesRead], manager);
        await MakeAdministratorAsync(tenant.Id, admin);
        using var client = await LoginAsync(manager);

        // Kendine kullanıcı yönetimi veremez; kendinde olan izni verebilir.
        var escalate = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Ele geçir", [ManageUsers, CompaniesRead]));
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Contains("Kullanıcıları ve kullanıcıların rollerini yönetir", await escalate.Content.ReadAsStringAsync());

        var created = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Okur", [CompaniesRead]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;

        // Yöneticinin verdiği, kendisinde olmayan izni de kaldıramaz; ama o izne dokunmadan rolü düzenleyebilir.
        using var adminClient = await LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Okur", [CompaniesRead, CompaniesWrite]))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Okur", [CompaniesRead]))).StatusCode);
        var renamed = await client.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Firma okuru", [CompaniesRead, CompaniesWrite]));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal([CompaniesRead, CompaniesWrite], (await renamed.Content.ReadFromJsonAsync<RoleDto>())!.Permissions);
    }

    [Fact]
    public async Task Role_with_members_is_deleted_only_after_moving_them_to_another_role()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var system = await MakeAdministratorAsync(tenant.Id, admin);
        var leaving = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], member);
        var target = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite]);
        var empty = await factory.CreateRoleAsync(tenant.Id, []);
        using var client = await LoginAsync(admin);

        var listed = await client.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Equal(1, listed!.Single(r => r.Id == leaving.Id).MemberCount);
        Assert.Equal(1, listed!.Single(r => r.Id == system.Id).MemberCount);

        var withoutTarget = await client.DeleteAsync($"/api/roles/{leaving.Id}");
        Assert.Equal(HttpStatusCode.Conflict, withoutTarget.StatusCode);
        Assert.Contains("1 kişi var", await withoutTarget.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={leaving.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={Guid.NewGuid()}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={target.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/roles/{empty.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/roles/{empty.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/roles/{system.Id}")).StatusCode);

        var membershipId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        var roles = await client.GetFromJsonAsync<List<RoleDto>>($"/api/memberships/{membershipId}/roles");
        Assert.Equal([target.Id], roles!.Select(r => r.Id));

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Contains(await db.AuditLog.AsNoTracking().Where(a => a.RecordId == leaving.Id).ToListAsync(),
            a => a.Action == AuditAction.Deleted && a.EntityKey == "core.role" && a.UserId == admin.Id);
        Assert.Contains(await db.AuditLog.AsNoTracking().Where(a => a.RecordId == membershipId).ToListAsync(),
            a => a.Action == AuditAction.Updated && a.Changes.Contains(target.Name));
    }

    [Fact]
    public async Task Moving_members_needs_user_management_and_administrator_role_needs_an_administrator()
    {
        var tenant = await factory.CreateTenantAsync();
        var roleManager = await factory.CreateUserAsync(tenant);
        var bothManager = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var admin = await factory.CreateUserAsync(tenant);
        var system = await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles], roleManager);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles, ManageUsers], bothManager);
        var leaving = await factory.CreateRoleAsync(tenant.Id, [], member);
        var target = await factory.CreateRoleAsync(tenant.Id, []);

        using var roleClient = await LoginAsync(roleManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await roleClient.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={target.Id}")).StatusCode);

        using var bothClient = await LoginAsync(bothManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await bothClient.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={system.Id}")).StatusCode);

        // Reddedilen silmeden sonra rol ve ataması yerinde.
        using var adminClient = await LoginAsync(admin);
        var roles = await adminClient.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Equal(1, roles!.Single(r => r.Id == leaving.Id).MemberCount);

        Assert.Equal(HttpStatusCode.NoContent, (await bothClient.DeleteAsync($"/api/roles/{leaving.Id}?moveTo={target.Id}")).StatusCode);
    }

    [Fact]
    public async Task Permissions_of_a_closed_module_stay_on_the_role_and_role_changes_apply_on_the_next_request()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        var role = await factory.CreateRoleAsync(tenant.Id, [ManageSettings], member);
        using var adminClient = await LoginAsync(admin);
        using var memberClient = await LoginAsync(member);

        var permissions = await adminClient.GetFromJsonAsync<List<PermissionDto>>("/api/roles/permissions");
        var companies = permissions!.Single(p => p.Key == CompaniesRead);
        Assert.Equal(("CRM", "Firmalar"), (companies.ModuleName, companies.ResourceName));

        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/records/crm.company")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest(role.Name, [ManageSettings, CompaniesRead]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/records/crm.company")).StatusCode);

        // Modül kapanınca izin formda görünmez ama rolde kalır; yeniden açılınca geçerli olur.
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: false);
        Assert.DoesNotContain(await adminClient.GetFromJsonAsync<List<PermissionDto>>("/api/roles/permissions") ?? [], p => p.ModuleKey == "crm");
        var roles = await adminClient.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Contains(CompaniesRead, roles!.Single(r => r.Id == role.Id).Permissions);

        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/records/crm.company")).StatusCode);
    }

    [Fact]
    public async Task Templates_are_offered_only_for_enabled_modules()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);

        var templates = await client.GetFromJsonAsync<List<RoleTemplateDto>>("/api/roles/templates");
        Assert.Equal(["crm.sales-rep", "crm.lead-coordinator", "core.read-only"], templates!.Select(t => t.Key));
        Assert.All(templates![2].Permissions, p => Assert.StartsWith("crm.", p));
        Assert.All(templates[2].Permissions, p => Assert.EndsWith(".read", p));

        // Şablonun izinleriyle rol oluşturulabilir.
        var sales = templates[0];
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(sales.Name, sales.Permissions))).StatusCode);
    }

    [Fact]
    public async Task Role_members_and_a_users_access_match_what_the_server_enforces()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var roleManager = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        var readers = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, ContactsRead], member);
        var writers = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite]);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles], roleManager);
        var foreign = await factory.CreateRoleAsync(other.Id, []);
        var memberMembership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/memberships/{memberMembership}/roles", new SetMembershipRolesRequest([readers.Id, writers.Id]))).StatusCode);

        // Rol yöneticisi de rolün üyelerini görür; başka tenant'ın rolü 404.
        using var roleClient = await LoginAsync(roleManager);
        var members = await roleClient.GetFromJsonAsync<UserPageResponse>($"/api/roles/{readers.Id}/members");
        Assert.Equal([member.Id], members!.Items.Select(u => u.UserId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/roles/{foreign.Id}/members")).StatusCode);

        var access = await client.GetFromJsonAsync<UserAccessDto>($"/api/users/{memberMembership}/access");
        Assert.False(access!.IsAdministrator);
        Assert.Equal([CompaniesRead, CompaniesWrite, ContactsRead], access.Permissions.Select(p => p.Key));
        Assert.Equal(
            new[] { readers.Name, writers.Name }.Order(StringComparer.Ordinal),
            access.Permissions.Single(p => p.Key == CompaniesRead).GrantedBy.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Contains(access.Assignable, p => p.Key == ManageUsers);

        using var memberClient = await LoginAsync(member);
        var enforced = await memberClient.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal(enforced!.Permissions, access.Permissions.Select(p => p.Key));

        var adminAccess = await client.GetFromJsonAsync<UserAccessDto>(
            $"/api/users/{await factory.GetMembershipIdAsync(admin.Id, tenant.Id)}/access");
        Assert.True(adminAccess!.IsAdministrator);
        Assert.Equal(adminAccess.Assignable.Count, adminAccess.Permissions.Count);

        // Kullanıcı yönetimi izni olmayan rol yöneticisi kişinin erişimini göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await roleClient.GetAsync($"/api/users/{memberMembership}/access")).StatusCode);
    }

    private async Task<RoleSummary> MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
        return system;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
