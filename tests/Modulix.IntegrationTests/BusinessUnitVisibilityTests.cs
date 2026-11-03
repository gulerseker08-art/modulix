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
/// ADR-0026: departman yönetimi tenant geneli bir izinle (<c>core.users.manage</c>) verilir, ama yönetici olmayan işlemi yapan yalnızca
/// kendi kapsamındaki departmanları görür ve yönetir. Başka bayilerin departman adı, üyeleri ve rolleri ne departman uçlarından, ne
/// kullanıcı listesindeki departman adından ve süzgecinden, ne de davetle öğrenilir ya da kullanılır; hepsi "bulunamadı" gibi davranır.
/// </summary>
public class BusinessUnitVisibilityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task A_unit_scoped_manager_sees_and_manages_only_their_own_department()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);

        // Liste yalnızca kendi departmanı; diğer bayiler, alt departman (yalnızca departman kapsamı) ve kök yok.
        var listed = (await manager.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!;
        Assert.Equal(new[] { "Bayi A" }, listed.Select(u => u.Name));

        var own = await manager.GetFromJsonAsync<BusinessUnitDetailDto>($"/api/business-units/{t.UnitA.Id}");
        Assert.Contains(own!.Members, m => m.UserId == t.A2.Id);
        foreach (var hidden in new[] { t.UnitB.Id, t.UnitA1.Id, t.Root.Id })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/business-units/{hidden}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/business-units/{hidden}/roles")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync(
                $"/api/business-units/{hidden}", new UpdateBusinessUnitRequest("Ele geçir", null, BusinessUnitKindLabels.Dealer))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync(
                $"/api/business-units/{hidden}/active", new SetBusinessUnitActiveRequest(false))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync(
                $"/api/business-units/{hidden}/roles", new SetBusinessUnitRolesRequest([]))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsJsonAsync(
                "/api/business-units", new CreateBusinessUnitRequest(hidden, "Yetkisiz alt", null, BusinessUnitKindLabels.Dealer))).StatusCode);
        }

        // Kendi departmanının altına departman ekleyebilir ve kendi departmanını düzenleyebilir.
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(t.UnitA.Id, "A Yeni Alt", null, BusinessUnitKindLabels.Other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(
            $"/api/business-units/{t.UnitA.Id}", new UpdateBusinessUnitRequest("Bayi A Yeni", "A1", BusinessUnitKindLabels.Dealer))).StatusCode);
    }

    [Fact]
    public async Task A_tree_scoped_manager_sees_their_department_and_the_ones_below_it()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.TreeManager);

        var listed = (await manager.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!;
        Assert.Equal(new[] { "Bayi A", "Bayi A Alt" }, listed.Select(u => u.Name).Order());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/business-units/{t.UnitA1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/business-units/{t.UnitB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/business-units/{t.Root.Id}")).StatusCode);

        // Yönetici (tüm kayıtlar) bütün ağacı görür.
        using var admin = await LoginAsync(t.Admin);
        Assert.Equal(4, (await admin.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Count);
    }

    [Fact]
    public async Task Other_departments_names_do_not_leak_through_the_user_list_the_user_card_or_the_filter()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);
        var a2 = await factory.GetMembershipIdAsync(t.A2.Id, t.Tenant.Id);
        var b1 = await factory.GetMembershipIdAsync(t.B1.Id, t.Tenant.Id);

        var page = (await manager.GetFromJsonAsync<UserPageResponse>("/api/users?pageSize=100"))!;
        Assert.Equal("Bayi A", page.Items.Single(u => u.MembershipId == a2).BusinessUnitName);
        Assert.Null(page.Items.Single(u => u.MembershipId == b1).BusinessUnitName);
        Assert.DoesNotContain(page.Items, u => u.BusinessUnitName is "Bayi B" or "Bayi A Alt");

        // Kullanıcı kartı ve kartın departman ucu da başka bayinin adını vermez.
        Assert.Null((await manager.GetFromJsonAsync<UserDto>($"/api/users/{b1}"))!.BusinessUnitName);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/users/{b1}/business-unit")).StatusCode);
        Assert.Equal("Bayi A", (await manager.GetFromJsonAsync<MembershipBusinessUnitDto>($"/api/users/{a2}/business-unit"))!.Name);

        // Başka bayinin departmanına göre süzmek boş döner; kendi departmanında kendi üyeleri gelir.
        var otherUnit = (await manager.GetFromJsonAsync<UserPageResponse>($"/api/users?businessUnitId={t.UnitB.Id}"))!;
        Assert.Equal(0, otherUnit.TotalCount);
        Assert.Empty(otherUnit.Items);
        Assert.Contains((await manager.GetFromJsonAsync<UserPageResponse>($"/api/users?businessUnitId={t.UnitA.Id}"))!.Items, u => u.MembershipId == a2);

        // Yönetici her şeyi görür.
        using var admin = await LoginAsync(t.Admin);
        Assert.Equal("Bayi B", (await admin.GetFromJsonAsync<UserDto>($"/api/users/{b1}"))!.BusinessUnitName);
    }

    [Fact]
    public async Task A_scoped_manager_can_only_invite_into_their_own_scope()
    {
        var t = await SetUpAsync();
        using var manager = await LoginAsync(t.UnitManager);

        // Başka bayinin ya da kökün departmanı "bulunmayan departman" gibi reddedilir; kullanıcı oluşmaz.
        foreach (var outside in new[] { t.UnitB.Id, t.Root.Id })
        {
            var rejected = await Invite(manager, outside);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("businessUnitId", await rejected.Content.ReadAsStringAsync());
        }

        // Departman verilmezse kök değil kendi departmanı kullanılır; verilirse kendi departmanı kabul edilir.
        var implicitUnit = await Invite(manager, null);
        Assert.Equal(HttpStatusCode.Created, implicitUnit.StatusCode);
        Assert.Equal("Bayi A", (await implicitUnit.Content.ReadFromJsonAsync<InviteUserResponse>())!.User.BusinessUnitName);
        Assert.Equal(HttpStatusCode.Created, (await Invite(manager, t.UnitA.Id)).StatusCode);

        // Yönetici kök varsayılanını ve herhangi bir departmanı kullanabilir.
        using var admin = await LoginAsync(t.Admin);
        var viaAdmin = await Invite(admin, null);
        Assert.Equal(t.Root.Name, (await viaAdmin.Content.ReadFromJsonAsync<InviteUserResponse>())!.User.BusinessUnitName);
        Assert.Equal(HttpStatusCode.Created, (await Invite(admin, t.UnitB.Id)).StatusCode);
    }

    [Fact]
    public async Task Tenants_and_unauthorized_users_cannot_read_departments()
    {
        var t = await SetUpAsync();
        var other = await factory.CreateTenantAsync();
        var otherAdmin = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.DoesNotContain(
            (await otherClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!, u => u.Id == t.UnitA.Id || u.Id == t.UnitB.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/business-units/{t.UnitA.Id}")).StatusCode);

        var plain = await factory.CreateUserAsync(t.Tenant);
        using var plainClient = await LoginAsync(plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await plainClient.GetAsync("/api/business-units")).StatusCode);
    }

    private static Task<HttpResponseMessage> Invite(HttpClient client, Guid? unitId) =>
        client.PostAsJsonAsync(
            "/api/users/invitations",
            new InviteUserRequest($"davet-{Guid.NewGuid():N}@test.modulix", "Davetli", [], unitId));

    private sealed record Setup(
        Core.Platform.Tenant Tenant,
        ModulixUser Admin,
        ModulixUser UnitManager,
        ModulixUser TreeManager,
        ModulixUser A2,
        ModulixUser B1,
        BusinessUnitDto Root,
        BusinessUnitDto UnitA,
        BusinessUnitDto UnitA1,
        BusinessUnitDto UnitB);

    // Bayi A: yönetici (yalnızca departman), yönetici (departman ve altı), a2. Alt departman "Bayi A Alt". Bayi B: b1.
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var unitManager = await factory.CreateUserAsync(tenant);
        var treeManager = await factory.CreateUserAsync(tenant);
        var a2 = await factory.CreateUserAsync(tenant);
        var b1 = await factory.CreateUserAsync(tenant);

        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var unitA = await CreateUnitAsync(client, root.Id, "Bayi A");
        var unitA1 = await CreateUnitAsync(client, unitA.Id, "Bayi A Alt");
        var unitB = await CreateUnitAsync(client, root.Id, "Bayi B");
        foreach (var (user, unit) in new[] { (unitManager, unitA), (treeManager, unitA), (a2, unitA), (b1, unitB) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
                $"/api/users/{await factory.GetMembershipIdAsync(user.Id, tenant.Id)}/business-unit",
                new SetMembershipBusinessUnitRequest(unit.Id))).StatusCode);
        }

        // Yöneticiler kullanıcı yönetir; yalnızca firma okuma izni vardır.
        var unitRole = await CreateRoleAsync(client, "Dep yöneticisi", SharedScope.Unit);
        var treeRole = await CreateRoleAsync(client, "Ağaç yöneticisi", SharedScope.UnitAndBelow);
        await SetRolesAsync(client, unitManager, tenant, unitRole.Id);
        await SetRolesAsync(client, treeManager, tenant, treeRole.Id);
        return new Setup(tenant, admin, unitManager, treeManager, a2, b1, root, unitA, unitA1, unitB);
    }

    private static async Task<RoleDto> CreateRoleAsync(HttpClient client, string name, SharedScope scope)
    {
        var response = await client.PostAsJsonAsync(
            "/api/roles", new SaveRoleRequest(name, [ManageUsers, CompaniesRead], scope, RoleAssignLevel.User));
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
