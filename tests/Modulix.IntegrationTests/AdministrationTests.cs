using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y1: yönetim izinleri, sistem rolü ("Yönetici"), son yöneticinin korunması ve yönetim işlemlerinin denetim kaydı.
/// </summary>
public class AdministrationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string ManageUsers = CoreModule.Permissions.ManageUsers;
    private const string ManageRoles = CoreModule.Permissions.ManageRoles;
    private const string ManageSettings = CoreModule.Permissions.ManageSettings;
    private const string ReadAudit = CoreModule.Permissions.ReadAudit;
    private const string ManageCustomization = CoreModule.Permissions.ManageCustomization;
    private const string ManageCompanies = CoreModule.Permissions.ManageCompanies;

    [Fact]
    public void Shared_access_keys_match_the_core_module()
    {
        // Arayüz menüsü bu anahtarlara bakar; Core'daki bir değişiklik arayüzü sessizce bozmasın.
        Assert.Equal(CoreModule.ModuleKey, CoreAccess.ModuleKey);
        var corePermissions = ((IModule)new CoreModule()).Permissions.Select(p => p.Key).Order().ToList();
        Assert.Equal(
            corePermissions,
            typeof(CoreAccess).GetFields().Where(f => f.Name != nameof(CoreAccess.ModuleKey)).Select(f => (string)f.GetValue(null)!).Order());
        Assert.Equal(corePermissions, CoreAccess.AdministrationPermissions.Order());
    }

    [Fact]
    public async Task Assigning_roles_requires_user_management_and_defining_roles_requires_role_management()
    {
        var tenant = await factory.CreateTenantAsync();
        var roleManager = await factory.CreateUserAsync(tenant);
        var userManager = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var role = await factory.CreateRoleAsync(tenant.Id, [ManageRoles], roleManager);
        await factory.CreateRoleAsync(tenant.Id, [ManageUsers], userManager);
        var membershipId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);

        using var roleClient = await LoginAsync(roleManager);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await roleClient.PutAsJsonAsync($"/api/memberships/{membershipId}/roles", new SetMembershipRolesRequest([role.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await roleClient.GetAsync($"/api/memberships/{membershipId}/roles")).StatusCode);

        using var userClient = await LoginAsync(userManager);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await userClient.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Yeni", []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/roles/permissions")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await userClient.PutAsJsonAsync($"/api/memberships/{membershipId}/roles", new SetMembershipRolesRequest([role.Id]))).StatusCode);
    }

    [Fact]
    public async Task Tenant_settings_are_readable_by_members_but_writable_only_with_settings_permission()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageSettings], manager);
        const string url = "/api/records/core.tenant-setting";
        const string key = Modulix.Core.Modules.CoreModule.SettingKeys.LegalName;

        using var memberClient = await LoginAsync(member);
        var denied = await memberClient.PostAsJsonAsync(url, Write(new JsonObject { ["key"] = key, ["value"] = "tr-TR" }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var managerClient = await LoginAsync(manager);
        var created = await managerClient.PostAsJsonAsync(url, Write(new JsonObject { ["key"] = key, ["value"] = "tr-TR" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var setting = (await created.Content.ReadFromJsonAsync<RecordResponse>())!;

        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"{url}/{setting.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.PutAsJsonAsync($"{url}/{setting.Id}", Write(new JsonObject { ["value"] = "en-US" }, version: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.DeleteAsync($"{url}/{setting.Id}?version=1")).StatusCode);
    }

    [Fact]
    public async Task System_role_grants_every_permission_of_enabled_modules_and_cannot_be_edited()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var system = await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);

        var before = await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal([ReadAudit, ManageCompanies, ManageCustomization, ManageRoles, ManageSettings, ManageUsers], before!.Permissions);

        // Sonradan açılan modülün izinleri yöneticiye kendiliğinden gelir.
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var after = await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Contains("crm.companies.write", after!.Permissions);
        Assert.Equal(((IModule)new CoreModule()).Permissions.Count + 11, after.Permissions.Count);

        var roles = await client.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        var listed = Assert.Single(roles!);
        Assert.True(listed.IsSystem);
        Assert.Equal(Role.SystemRoleName, listed.Name);
        Assert.Empty(listed.Permissions);

        var edit = await client.PutAsJsonAsync($"/api/roles/{system.Id}", new SaveRoleRequest("Başka ad", [ManageRoles]));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        // Aynı adla ikinci bir rol açılamaz; sistem rolü tektir.
        var duplicate = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(Role.SystemRoleName, []));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task Ensure_system_role_is_idempotent_and_converts_a_legacy_administrator_role()
    {
        var tenant = await factory.CreateTenantAsync();
        using var scope = factory.OpenScope(tenant.Id);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();

        var legacy = await roles.CreateAsync(Role.SystemRoleName, [ManageRoles], CancellationToken.None);
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        var again = await roles.EnsureSystemRoleAsync(CancellationToken.None);

        Assert.Equal(legacy.Id, system.Id);
        Assert.Equal(system.Id, again.Id);
        Assert.True(system.IsSystem);
        Assert.Empty(system.Permissions);
        Assert.Single(await roles.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Last_active_administrator_cannot_lose_the_system_role()
    {
        var tenant = await factory.CreateTenantAsync();
        var a = await factory.CreateUserAsync(tenant);
        var b = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, a, b);
        var membershipA = await factory.GetMembershipIdAsync(a.Id, tenant.Id);
        var membershipB = await factory.GetMembershipIdAsync(b.Id, tenant.Id);
        using var client = await LoginAsync(a);

        // İki yöneticiden biri kaldırılabilir.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/memberships/{membershipB}/roles", new SetMembershipRolesRequest([]))).StatusCode);

        var removeSelf = await client.PutAsJsonAsync($"/api/memberships/{membershipA}/roles", new SetMembershipRolesRequest([]));
        Assert.Equal(HttpStatusCode.Conflict, removeSelf.StatusCode);
        Assert.Contains("en az bir yönetici", await removeSelf.Content.ReadAsStringAsync());

        // Yönetici rolünü korurken başka rol eklemek serbesttir.
        var other = await factory.CreateRoleAsync(tenant.Id, []);
        var system = (await client.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.IsSystem);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/memberships/{membershipA}/roles", new SetMembershipRolesRequest([system.Id, other.Id]))).StatusCode);
    }

    [Fact]
    public async Task Disabled_administrator_does_not_count_as_the_remaining_administrator()
    {
        var tenant = await factory.CreateTenantAsync();
        var a = await factory.CreateUserAsync(tenant);
        var b = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, a, b);
        await factory.DisableMembershipAsync(b.Id, tenant.Id);
        var membershipA = await factory.GetMembershipIdAsync(a.Id, tenant.Id);
        var membershipB = await factory.GetMembershipIdAsync(b.Id, tenant.Id);
        using var client = await LoginAsync(a);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"/api/memberships/{membershipA}/roles", new SetMembershipRolesRequest([]))).StatusCode);

        // Devre dışı üyenin yönetici rolünü kaldırmak yönetici sayısını değiştirmez.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/memberships/{membershipB}/roles", new SetMembershipRolesRequest([]))).StatusCode);
    }

    [Fact]
    public async Task Two_administrators_removing_each_other_at_once_leave_one_administrator()
    {
        for (var round = 0; round < 3; round++)
        {
            var tenant = await factory.CreateTenantAsync();
            var a = await factory.CreateUserAsync(tenant);
            var b = await factory.CreateUserAsync(tenant);
            var system = await MakeAdministratorsAsync(tenant.Id, a, b);
            var membershipA = await factory.GetMembershipIdAsync(a.Id, tenant.Id);
            var membershipB = await factory.GetMembershipIdAsync(b.Id, tenant.Id);
            using var clientA = await LoginAsync(a);
            using var clientB = await LoginAsync(b);

            var results = await Task.WhenAll(
                clientA.PutAsJsonAsync($"/api/memberships/{membershipB}/roles", new SetMembershipRolesRequest([])),
                clientB.PutAsJsonAsync($"/api/memberships/{membershipA}/roles", new SetMembershipRolesRequest([])));

            // İkinci istek ya son yönetici kuralına (409) ya da artık yönetici olmadığı için yönetici kuralına (403) takılır.
            var statuses = results.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
            Assert.Equal(HttpStatusCode.NoContent, statuses[0]);
            Assert.Contains(statuses[1], new[] { HttpStatusCode.Forbidden, HttpStatusCode.Conflict });

            using var scope = factory.OpenScope(tenant.Id);
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.Equal(1, await db.MembershipRoles.CountAsync(mr => mr.RoleId == system.Id));
        }
    }

    [Fact]
    public async Task Role_and_assignment_changes_are_written_to_the_audit_log()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        var membershipId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(admin);

        var created = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Depo", [ManageSettings]));
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Depo sorumlusu", [ManageSettings, ReadAudit]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/memberships/{membershipId}/roles", new SetMembershipRolesRequest([role.Id]))).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

        var roleAudit = await db.AuditLog.Where(e => e.RecordId == role.Id).OrderBy(e => e.Action).ToListAsync();
        Assert.Equal([AuditAction.Created, AuditAction.Updated], roleAudit.Select(e => e.Action));
        Assert.All(roleAudit, e => Assert.Equal(admin.Id, e.UserId));
        Assert.All(roleAudit, e => Assert.Equal("core.role", e.EntityKey));
        var update = JsonNode.Parse(roleAudit[1].Changes)!.AsObject();
        Assert.Equal("Depo", update["name"]!["old"]!.GetValue<string>());
        Assert.Equal("Depo sorumlusu", update["name"]!["new"]!.GetValue<string>());
        Assert.Equal(2, update["permissions"]!["new"]!.AsArray().Count);

        var assignment = Assert.Single(await db.AuditLog.Where(e => e.RecordId == membershipId).ToListAsync());
        Assert.Equal("core.membership", assignment.EntityKey);
        var roles = JsonNode.Parse(assignment.Changes)!["roles"]!;
        Assert.Empty(roles["old"]!.AsArray());
        Assert.Equal("Depo sorumlusu", roles["new"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Upgrade_converts_legacy_roles_and_extends_role_managers()
    {
        var legacyTenant = await factory.CreateTenantAsync();
        var emptyTenant = await factory.CreateTenantAsync();
        Guid legacyAdminId, managerId, salesId;
        using (var scope = factory.OpenScope(legacyTenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            legacyAdminId = (await roles.CreateAsync(Role.SystemRoleName, [ManageRoles, "crm.companies.read"], CancellationToken.None)).Id;
            managerId = (await roles.CreateAsync("Eski yönetici", [ManageRoles, "crm.companies.read"], CancellationToken.None)).Id;
            salesId = (await roles.CreateAsync("Satış", ["crm.companies.read"], CancellationToken.None)).Id;
        }

        // İkinci çalıştırma bir şey değiştirmemeli.
        await RunUpgradeAsync(legacyTenant.Id);
        await RunUpgradeAsync(legacyTenant.Id);
        await RunUpgradeAsync(emptyTenant.Id);

        using (var scope = factory.OpenScope(legacyTenant.Id))
        {
            var roles = (await scope.ServiceProvider.GetRequiredService<IRoleService>().ListAsync(CancellationToken.None))
                .ToDictionary(r => r.Id);
            Assert.Equal(3, roles.Count);
            Assert.True(roles[legacyAdminId].IsSystem);
            Assert.Empty(roles[legacyAdminId].Permissions);
            Assert.Equal(
                [ReadAudit, ManageRoles, ManageSettings, ManageUsers, "crm.companies.read"],
                roles[managerId].Permissions);
            Assert.Equal(["crm.companies.read"], roles[salesId].Permissions);
        }

        using (var scope = factory.OpenScope(emptyTenant.Id))
        {
            var role = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRoleService>().ListAsync(CancellationToken.None));
            Assert.True(role.IsSystem);
            Assert.Equal(Role.SystemRoleName, role.Name);
        }
    }

    /// <summary>Migration'ın veri adımını tek tenant için uygulama kullanıcısıyla çalıştırır (RLS açık).</summary>
    private async Task RunUpgradeAsync(Guid tenantId)
    {
        // ADR-0009: roller tenant veritabanındadır; yükseltme o tenant'ın veritabanında, oturum tenant'ı yazılarak çalışır.
        var options = factory.Services.GetRequiredService<Modulix.Infrastructure.Tenancy.PlatformDatabaseOptions>();
        var databaseName = (await factory.TenantDatabaseNameAsync(tenantId))!;
        var connectionString = Modulix.Infrastructure.Tenancy.TenantDatabaseNaming.ConnectionFor(options.AppConnectionString, databaseName);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DECLARE @TenantId uniqueidentifier = @tenant;
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
            {SystemRoleUpgradeSql.ForTenant}
            """;
        command.Parameters.AddWithValue("@tenant", tenantId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Tenant'ın sistem rolünü oluşturur ve kullanıcılara atar.</summary>
    private async Task<RoleSummary> MakeAdministratorsAsync(Guid tenantId, params ModulixUser[] users)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        foreach (var user in users)
        {
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
        }

        return system;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static RecordWriteRequest Write(JsonObject values, long? version = null) => new(values, version);
}
