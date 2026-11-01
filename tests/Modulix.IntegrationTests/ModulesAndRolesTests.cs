using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ortak modül altyapısı: tenant başına modül hakları, tenant'a özel roller ve izinler (Görev E4).
/// Kapalı modülün izni rolde olsa bile geçmez; roller ve atamalar başka tenant'tan görülemez ve kullanılamaz.
/// </summary>
public class ModulesAndRolesTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string ManageRoles = CoreModule.Permissions.ManageRoles;
    private const string ManageUsers = CoreModule.Permissions.ManageUsers;
    private const string CompaniesRead = "crm.companies.read";

    [Fact]
    public async Task Modules_list_shows_registered_modules_and_tenant_entitlements()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        using var client = await LoginAsync(user.Email!);

        var modules = (await client.GetFromJsonAsync<List<ModuleDto>>("/api/tenant/modules"))!;

        Assert.Equal(["contracts", "core", "crm", "logistics", "orders"], modules.Select(m => m.Key));
        Assert.Equal([false, true, false, true, false], modules.Select(m => m.IsEnabled));
    }

    [Fact]
    public async Task Permission_of_a_disabled_module_is_not_granted_until_the_module_is_enabled()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles, CompaniesRead], user);
        using var client = await LoginAsync(user.Email!);

        var before = await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal([ManageRoles], before!.Permissions);
        Assert.Equal(["core"], before.EnabledModules);

        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var enabled = await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal([ManageRoles, CompaniesRead], enabled!.Permissions);

        await factory.SetModuleAsync(tenant.Id, "crm", enabled: false);
        var disabled = await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal([ManageRoles], disabled!.Permissions);
    }

    [Fact]
    public async Task User_without_permission_cannot_manage_roles()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user.Email!);

        var response = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Role_manager_creates_a_role_and_assigns_it_to_a_member()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        // Yönetici olmayan rol yöneticisi yalnızca kendisinde olan izni verebilir (Y3).
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles, ManageUsers, CompaniesRead], admin);
        using var adminClient = await LoginAsync(admin.Email!);

        var assignable = await adminClient.GetFromJsonAsync<List<PermissionDto>>("/api/roles/permissions");
        Assert.Contains(assignable!, p => p.Key == CompaniesRead);
        Assert.DoesNotContain(assignable!, p => p.ModuleKey == "logistics");

        var created = await adminClient.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Satış", [CompaniesRead]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;

        var membershipId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        var assigned = await adminClient.PutAsJsonAsync(
            $"/api/memberships/{membershipId}/roles", new SetMembershipRolesRequest([role.Id]));
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);

        using var memberClient = await LoginAsync(member.Email!);
        var access = await memberClient.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal([CompaniesRead], access!.Permissions);
    }

    [Fact]
    public async Task Duplicate_role_name_and_unknown_permission_are_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles], admin);
        using var client = await LoginAsync(admin.Email!);

        var first = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Depo", []));
        var duplicate = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("depo", []));
        var unknown = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Başka", ["crm.unknown.read"]));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Roles_and_memberships_of_another_tenant_cannot_be_seen_or_used()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await factory.CreateUserAsync(a);
        var memberB = await factory.CreateUserAsync(b);
        var roleA = await factory.CreateRoleAsync(a.Id, [ManageRoles, ManageUsers], adminA);
        var roleB = await factory.CreateRoleAsync(b.Id, [ManageRoles]);
        using var client = await LoginAsync(adminA.Email!);

        var roles = await client.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        Assert.Equal([roleA.Id], roles!.Select(r => r.Id));

        var updateOther = await client.PutAsJsonAsync($"/api/roles/{roleB.Id}", new SaveRoleRequest("Ele geçir", [ManageRoles]));
        Assert.Equal(HttpStatusCode.NotFound, updateOther.StatusCode);

        var membershipB = await factory.GetMembershipIdAsync(memberB.Id, b.Id);
        var assignOtherMembership = await client.PutAsJsonAsync(
            $"/api/memberships/{membershipB}/roles", new SetMembershipRolesRequest([roleA.Id]));
        Assert.Equal(HttpStatusCode.NotFound, assignOtherMembership.StatusCode);

        var membershipA = await factory.GetMembershipIdAsync(adminA.Id, a.Id);
        var assignOtherRole = await client.PutAsJsonAsync(
            $"/api/memberships/{membershipA}/roles", new SetMembershipRolesRequest([roleA.Id, roleB.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, assignOtherRole.StatusCode);
    }

    [Fact]
    public async Task Database_blocks_assigning_another_tenants_role_even_with_raw_sql()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var userA = await factory.CreateUserAsync(a);
        var roleB = await factory.CreateRoleAsync(b.Id, []);
        var membershipA = await factory.GetMembershipIdAsync(userA.Id, a.Id);

        using var scope = factory.OpenScope(a.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

        // TenantId doğru (A) olsa bile rol B'ye ait: bileşik yabancı anahtar reddeder.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [core].[MembershipRoles] ([Id], [TenantId], [MembershipId], [RoleId]) VALUES ({Guid.NewGuid()}, {a.Id}, {membershipA}, {roleB.Id})"));
    }

    [Fact]
    public async Task Roles_are_protected_by_row_level_security()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var roleB = await factory.CreateRoleAsync(b.Id, []);

        using var scope = factory.OpenScope(a.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

        Assert.False(await db.Roles.IgnoreQueryFilters().AnyAsync(r => r.Id == roleB.Id));
    }

    [Fact]
    public async Task Core_module_cannot_be_disabled_and_unknown_modules_are_rejected()
    {
        var tenant = await factory.CreateTenantAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => factory.SetModuleAsync(tenant.Id, "core", enabled: false));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.SetModuleAsync(tenant.Id, "unknown", enabled: true));
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
