using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026: kullanıcı dizini kapsamlıdır. <c>core.users.manage</c> tenant geneli bir izindir ama yönetici olmayan işlemi yapan
/// yalnızca kendi kapsamındaki departmanların kullanıcılarını listeler, görür ve yönetir; başka bayinin kullanıcısı (kart, erişim,
/// sahip olunan kayıtlar, devre dışı bırakma, şifre sıfırlama, rol ve şirket görevleri, ekip üyeliği, kayıt devri) başka tenant'ınki
/// gibi "bulunamadı" döner. Tüm kayıtlar kapsamındaki yönetici ve platform/sistem bağlamı etkilenmez.
/// </summary>
public class UserDirectoryScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task The_user_list_contains_only_users_of_the_managers_scope()
    {
        var t = await SetUpAsync();

        using var unitManager = await LoginAsync(t.UnitManager);
        var own = (await unitManager.GetFromJsonAsync<UserPageResponse>("/api/users?pageSize=100"))!;
        Assert.Equal(
            new[] { t.A2.Id, t.TreeManager.Id, t.UnitManager.Id }.Order(),
            own.Items.Select(u => u.UserId).Order());
        Assert.Equal(3, own.TotalCount);

        // "Departman ve altı": alt departmanın kullanıcısı da gelir, diğer bayi ve merkez gelmez.
        using var treeManager = await LoginAsync(t.TreeManager);
        var tree = (await treeManager.GetFromJsonAsync<UserPageResponse>("/api/users?pageSize=100"))!;
        Assert.Equal(
            new[] { t.A1Sub.Id, t.A2.Id, t.TreeManager.Id, t.UnitManager.Id }.Order(),
            tree.Items.Select(u => u.UserId).Order());

        // Arama da kapsamın dışına çıkmaz.
        Assert.Equal(0, (await unitManager.GetFromJsonAsync<UserPageResponse>($"/api/users?search={Uri.EscapeDataString(t.B1.Email!)}"))!.TotalCount);

        // Yönetici hepsini görür (kendisi, iki yönetici, a2, a1sub, b1).
        using var admin = await LoginAsync(t.Admin);
        Assert.Equal(6, (await admin.GetFromJsonAsync<UserPageResponse>("/api/users?pageSize=100"))!.TotalCount);
    }

    [Fact]
    public async Task Operations_on_another_dealers_user_are_not_found_and_change_nothing()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);
        using var admin = await LoginAsync(t.Admin);
        var b1 = await factory.GetMembershipIdAsync(t.B1.Id, t.Tenant.Id);
        var a2 = await factory.GetMembershipIdAsync(t.A2.Id, t.Tenant.Id);

        // Okuma uçları.
        foreach (var path in new[] { "", "/access", "/owned-records", "/business-unit", "/teams" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/users/{b1}{path}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/memberships/{b1}/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/memberships/{b1}/companies")).StatusCode);

        // Değiştiren uçlar.
        foreach (var action in new[] { "disable", "enable", "password-reset", "close-sessions", "unlock" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"/api/users/{b1}/{action}", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsJsonAsync($"/api/users/{b1}/transfer-records", new TransferRecordsRequest(a2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"/api/memberships/{b1}/roles", new SetMembershipRolesRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"/api/memberships/{b1}/companies", new SetMembershipCompaniesRequest([]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"/api/users/{b1}/teams", new SetMembershipTeamsRequest([]))).StatusCode);

        // Hiçbiri etki etmedi: kullanıcı etkin ve yeri aynı.
        var card = (await admin.GetFromJsonAsync<UserDto>($"/api/users/{b1}"))!;
        Assert.Equal(UserStatus.Active, card.Status);
        Assert.Equal("Bayi B", card.BusinessUnitName);

        // Kendi kapsamındaki kullanıcıda aynı uçlar çalışır.
        foreach (var path in new[] { "", "/access", "/owned-records", "/business-unit", "/teams" })
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/users/{a2}{path}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/memberships/{a2}/roles")).StatusCode);
    }

    [Fact]
    public async Task Records_cannot_be_handed_over_to_a_user_outside_the_scope()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);
        var a2 = await factory.GetMembershipIdAsync(t.A2.Id, t.Tenant.Id);
        var b1 = await factory.GetMembershipIdAsync(t.B1.Id, t.Tenant.Id);

        var toOutsider = await manager.PostAsJsonAsync($"/api/users/{a2}/transfer-records", new TransferRecordsRequest(b1));
        Assert.Equal(HttpStatusCode.BadRequest, toOutsider.StatusCode);
        Assert.Contains("transferToMembershipId", await toOutsider.Content.ReadAsStringAsync());

        var disableWithHandover = await manager.PostAsJsonAsync($"/api/users/{a2}/disable", new DisableUserRequest(b1));
        Assert.Equal(HttpStatusCode.BadRequest, disableWithHandover.StatusCode);
        using var admin = await LoginAsync(t.Admin);
        Assert.Equal(UserStatus.Active, (await admin.GetFromJsonAsync<UserDto>($"/api/users/{a2}"))!.Status);
    }

    [Fact]
    public async Task Team_membership_of_users_outside_the_scope_cannot_be_changed()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);
        using var admin = await LoginAsync(t.Admin);
        var a2 = await factory.GetMembershipIdAsync(t.A2.Id, t.Tenant.Id);
        var b1 = await factory.GetMembershipIdAsync(t.B1.Id, t.Tenant.Id);

        var created = await manager.PostAsJsonAsync("/api/teams", new SaveTeamRequest("Bayi ekibi", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = (await created.Content.ReadFromJsonAsync<TeamDetailDto>())!;

        // Kapsamındaki kullanıcıyı ekleyebilir, kapsam dışındakini ekleyemez.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest([a2]))).StatusCode);
        var addOutsider = await manager.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest([a2, b1]));
        Assert.Equal(HttpStatusCode.BadRequest, addOutsider.StatusCode);
        Assert.Contains("membershipIds", await addOutsider.Content.ReadAsStringAsync());

        // Yönetici kapsam dışı bir kullanıcıyı ekipte tutuyorsa, yönetici olmayan onu listeden çıkarıp kaydedemez.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest([a2, b1]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest([a2]))).StatusCode);
        Assert.Equal(2, (await admin.GetFromJsonAsync<TeamDetailDto>($"/api/teams/{team.Id}"))!.Members.Count);
    }

    [Fact]
    public async Task Tenants_are_isolated_and_the_unrestricted_admin_keeps_full_access()
    {
        var t = await SetUpAsync();
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        var a2 = await factory.GetMembershipIdAsync(t.A2.Id, t.Tenant.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/users/{a2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/memberships/{a2}/roles")).StatusCode);

        // Tüm kayıtlar kapsamındaki yönetici başka bayinin kullanıcısını görür ve yönetebilir.
        using var admin = await LoginAsync(t.Admin);
        var b1 = await factory.GetMembershipIdAsync(t.B1.Id, t.Tenant.Id);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/users/{b1}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/memberships/{b1}/roles")).StatusCode);

        // Yetkisiz kullanıcı listeyi göremez.
        var plain = await factory.CreateUserAsync(t.Tenant);
        using var plainClient = await LoginAsync(plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await plainClient.GetAsync("/api/users")).StatusCode);
    }

    private sealed record Setup(
        Core.Platform.Tenant Tenant,
        ModulixUser Admin,
        ModulixUser UnitManager,
        ModulixUser TreeManager,
        ModulixUser A2,
        ModulixUser A1Sub,
        ModulixUser B1);

    // Bayi A: iki yönetici (yalnızca departman / departman ve altı) ve a2. Alt departman: a1sub. Bayi B: b1. Kök: yönetici.
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var unitManager = await factory.CreateUserAsync(tenant);
        var treeManager = await factory.CreateUserAsync(tenant);
        var a2 = await factory.CreateUserAsync(tenant);
        var a1Sub = await factory.CreateUserAsync(tenant);
        var b1 = await factory.CreateUserAsync(tenant);

        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var unitA = await CreateUnitAsync(client, root.Id, "Bayi A");
        var unitA1 = await CreateUnitAsync(client, unitA.Id, "Bayi A Alt");
        var unitB = await CreateUnitAsync(client, root.Id, "Bayi B");
        foreach (var (user, unit) in new[] { (unitManager, unitA), (treeManager, unitA), (a2, unitA), (a1Sub, unitA1), (b1, unitB) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
                $"/api/users/{await factory.GetMembershipIdAsync(user.Id, tenant.Id)}/business-unit",
                new SetMembershipBusinessUnitRequest(unit.Id))).StatusCode);
        }

        string[] permissions = [ManageUsers, ManageCompanies, CompaniesRead];
        var unitRole = await CreateRoleAsync(client, "Dep yöneticisi", permissions, SharedScope.Unit);
        var treeRole = await CreateRoleAsync(client, "Ağaç yöneticisi", permissions, SharedScope.UnitAndBelow);
        await SetRolesAsync(client, unitManager, tenant, unitRole.Id);
        await SetRolesAsync(client, treeManager, tenant, treeRole.Id);
        return new Setup(tenant, admin, unitManager, treeManager, a2, a1Sub, b1);
    }

    private static async Task<RoleDto> CreateRoleAsync(HttpClient client, string name, string[] permissions, SharedScope scope)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(name, permissions, scope, RoleAssignLevel.User));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private async Task SetRolesAsync(HttpClient admin, ModulixUser user, Core.Platform.Tenant tenant, Guid roleId)
    {
        var response = await admin.PutAsJsonAsync(
            $"/api/memberships/{await factory.GetMembershipIdAsync(user.Id, tenant.Id)}/roles", new SetMembershipRolesRequest([roleId]));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BusinessUnitDto> CreateUnitAsync(HttpClient client, Guid parentId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(parentId, name, null, BusinessUnitKindLabels.Dealer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
