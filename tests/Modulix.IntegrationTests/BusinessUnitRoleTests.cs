using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;
using SharedScope = Modulix.Shared.Access.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 2: rolün departmana atanması, atanabilir düzey, kapsamların birleşimi ve "departman" / "departman ve altı"
/// kayıt kapsamları. Kapsam listede sorguda, tek kayıtta 404 olarak uygulanır; departmandan gelen rol kullanıcıya
/// doğrudan atananla toplanır; sistem rolü departmana atanamaz; tenant sınırı ve atayanın yetkisi sunucuda uygulanır.
/// </summary>
public class BusinessUnitRoleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CompanyAccess = [CompaniesRead, CompaniesWrite];

    [Fact]
    public async Task Unit_role_gives_the_unit_permissions_and_unit_scope_while_other_units_stay_hidden()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var role = await CreateRoleAsync(admin, "Bayi satış", CompanyAccess, SharedScope.Unit, RoleAssignLevel.Unit);
        await AssignAsync(admin, t.UnitA.Id, (role.Id, false));
        await AssignAsync(admin, t.UnitB.Id, (role.Id, false));

        // Kullanıcılara doğrudan rol verilmedi; izin ve kapsam departmandan gelir.
        using var a1 = await LoginAsync(t.A1);
        using var a2 = await LoginAsync(t.A2);
        using var b1 = await LoginAsync(t.B1);
        var aCompany = await CreateCompanyAsync(a1, "A bayisi müşterisi");
        await CreateCompanyAsync(b1, "B bayisi müşterisi");

        var seenByA2 = await a2.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Equal([aCompany.Id], seenByA2!.Items.Select(c => c.Id));
        Assert.Equal(HttpStatusCode.OK, (await a2.GetAsync($"/api/crm/companies/{aCompany.Id}")).StatusCode);

        var seenByB1 = await b1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Single(seenByB1!.Items);
        Assert.DoesNotContain(seenByB1.Items, c => c.Id == aCompany.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b1.GetAsync($"/api/crm/companies/{aCompany.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b1.PutAsJsonAsync(
            $"/api/crm/companies/{aCompany.Id}", new SaveCompanyRequest("Ele geçir", Version: aCompany.Version))).StatusCode);

        // Sorumlu başka departmandaki birine atanamaz; aynı departmandaki birine atanabilir.
        var toOtherUnit = await a1.PutAsJsonAsync(
            $"/api/crm/companies/{aCompany.Id}", new SaveCompanyRequest(aCompany.Name, Version: aCompany.Version, OwnerId: t.B1.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toOtherUnit.StatusCode);
        var toUnitMate = await a1.PutAsJsonAsync(
            $"/api/crm/companies/{aCompany.Id}", new SaveCompanyRequest(aCompany.Name, Version: aCompany.Version, OwnerId: t.A2.Id));
        Assert.Equal(HttpStatusCode.OK, toUnitMate.StatusCode);

        // Kullanıcı kartı: rollerin kaynağı ve kapsam.
        var access = await admin.GetFromJsonAsync<UserAccessDto>($"/api/users/{await MembershipAsync(t.A1, t.Tenant)}/access");
        Assert.Equal(SharedScope.Unit, access!.Scope);
        Assert.Equal([SharedScope.Unit], access.Scopes);
        var source = Assert.Single(access.Roles!);
        Assert.Equal("Bayi satış", source.Name);
        Assert.Equal("A Bayisi", source.ViaBusinessUnit);
        Assert.Equal("A Bayisi", access.BusinessUnit);

        // Rolün atandığı departman sayısı rol listesinde görünür; kullanıcıya doğrudan atanan rol sayısı değişmez.
        var listed = (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.Id == role.Id);
        Assert.Equal(2, listed.UnitCount);
        Assert.Equal(0, listed.MemberCount);
        Assert.Equal(RoleAssignLevel.Unit, listed.AssignLevel);
    }

    [Fact]
    public async Task Unit_and_below_scope_reaches_child_units_and_the_root_sees_everything()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var wide = await CreateRoleAsync(admin, "Bölge", CompanyAccess, SharedScope.UnitAndBelow, RoleAssignLevel.Both);
        var narrow = await CreateRoleAsync(admin, "Tek departman", CompanyAccess, SharedScope.Unit, RoleAssignLevel.Both);

        // A bayisi "ve altı", alt departmanı A1 ve merkez "yalnızca kendi departmanı" kapsamıyla çalışır.
        await AssignAsync(admin, t.UnitA.Id, (wide.Id, false));
        await AssignAsync(admin, t.UnitA1.Id, (narrow.Id, false));
        await AssignAsync(admin, t.Root.Id, (narrow.Id, false));
        await AssignAsync(admin, t.UnitB.Id, (narrow.Id, false));

        using var a1 = await LoginAsync(t.A1);
        using var sub = await LoginAsync(t.A1Sub);
        using var b1 = await LoginAsync(t.B1);
        var subCompany = await CreateCompanyAsync(sub, "Alt departman müşterisi");
        var bCompany = await CreateCompanyAsync(b1, "B müşterisi");

        // A bayisi kullanıcısı alt departmanın kaydını görür; B bayisininkini görmez.
        var seenByA1 = await a1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Contains(seenByA1!.Items, c => c.Id == subCompany.Id);
        Assert.DoesNotContain(seenByA1.Items, c => c.Id == bCompany.Id);

        // Alt departmandaki kullanıcı "yalnızca kendi departmanı" kapsamıyla üstteki departmanı görmez.
        var aCompany = await CreateCompanyAsync(a1, "A müşterisi");
        var seenBySub = await sub.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Equal([subCompany.Id], seenBySub!.Items.Select(c => c.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await sub.GetAsync($"/api/crm/companies/{aCompany.Id}")).StatusCode);

        // Kök departmanda "departman ve altı" tüm firmadır; "yalnızca departman" ise yalnızca kök departmanın kullanıcıları.
        using var rootUser = await LoginAsync(t.RootUser);
        Assert.Empty((await rootUser.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50"))!.Items);
        await AssignAsync(admin, t.Root.Id, (wide.Id, false), (narrow.Id, false));
        var all = await rootUser.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Equal(
            new[] { aCompany.Id, subCompany.Id, bCompany.Id }.Order(),
            all!.Items.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Scopes_from_the_unit_and_from_direct_roles_are_united()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var unitRole = await CreateRoleAsync(admin, "Departman", CompanyAccess, SharedScope.Unit, RoleAssignLevel.Both);
        await AssignAsync(admin, t.UnitA.Id, (unitRole.Id, false));
        await AssignAsync(admin, t.UnitB.Id, (unitRole.Id, false));
        await AssignAsync(admin, t.UnitC.Id, (unitRole.Id, false));

        // A1 aynı zamanda B bayisindeki b1 ile aynı ekipte ve doğrudan "ekibinin kayıtları" rolü taşır.
        await factory.CreateRoleAsync(t.Tenant.Id, CompanyAccess, Scope.Team, t.A1);
        await CreateTeamAsync(admin, "Ortak ekip", t.A1, t.B1, t.Tenant);

        using var a1 = await LoginAsync(t.A1);
        using var a2 = await LoginAsync(t.A2);
        using var b1 = await LoginAsync(t.B1);
        using var c1 = await LoginAsync(t.C1);
        var fromUnitMate = await CreateCompanyAsync(a2, "Departman arkadaşı");
        var fromTeamMate = await CreateCompanyAsync(b1, "Ekip arkadaşı");
        var outsider = await CreateCompanyAsync(c1, "Dışarıdan");

        // Her iki kapsamdaki kayıtlar görünür (birleşim); hiçbirinde olmayan görünmez.
        var seen = await a1.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=50");
        Assert.Contains(seen!.Items, c => c.Id == fromUnitMate.Id);
        Assert.Contains(seen.Items, c => c.Id == fromTeamMate.Id);
        Assert.DoesNotContain(seen.Items, c => c.Id == outsider.Id);

        var access = await admin.GetFromJsonAsync<UserAccessDto>($"/api/users/{await MembershipAsync(t.A1, t.Tenant)}/access");
        Assert.Equal([SharedScope.Team, SharedScope.Unit], access!.Scopes!.Order());
        Assert.Equal(2, access.Roles!.Count);
    }

    [Fact]
    public async Task Inheritance_to_child_units_is_off_by_default_and_roles_can_be_removed()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var role = await CreateRoleAsync(admin, "Merkez rolü", CompanyAccess, SharedScope.Own, RoleAssignLevel.Both);

        // Merkeze verilen rol, "alt departmanlara da uygula" işaretlenmedikçe bayilere inmez.
        await AssignAsync(admin, t.Root.Id, (role.Id, false));
        using var a1 = await LoginAsync(t.A1);
        Assert.Equal(HttpStatusCode.Forbidden, (await a1.GetAsync("/api/crm/companies")).StatusCode);
        using var rootUser = await LoginAsync(t.RootUser);
        Assert.Equal(HttpStatusCode.OK, (await rootUser.GetAsync("/api/crm/companies")).StatusCode);

        var inherited = await AssignAsync(admin, t.Root.Id, (role.Id, true));
        Assert.True(Assert.Single(inherited).InheritToChildren);
        Assert.Equal(HttpStatusCode.OK, (await a1.GetAsync("/api/crm/companies")).StatusCode);
        var viaRoot = await admin.GetFromJsonAsync<UserAccessDto>($"/api/users/{await MembershipAsync(t.A1, t.Tenant)}/access");
        Assert.Equal(t.Root.Name, Assert.Single(viaRoot!.Roles!).ViaBusinessUnit);

        // Kaldırınca izin gider; kullanıcının departmanını değiştirmek de rolü değiştirir.
        await AssignAsync(admin, t.Root.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await a1.GetAsync("/api/crm/companies")).StatusCode);
        await AssignAsync(admin, t.UnitB.Id, (role.Id, false));
        Assert.Equal(HttpStatusCode.Forbidden, (await a1.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
            $"/api/users/{await MembershipAsync(t.A1, t.Tenant)}/business-unit", new SetMembershipBusinessUnitRequest(t.UnitB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a1.GetAsync("/api/crm/companies")).StatusCode);
    }

    [Fact]
    public async Task Assignment_rules_return_validation_and_conflict_responses()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var userOnly = await CreateRoleAsync(admin, "Yalnızca kullanıcı", [CompaniesRead], SharedScope.Own, RoleAssignLevel.User);
        var assignable = await CreateRoleAsync(admin, "Atanabilir", [CompaniesRead], SharedScope.Own, RoleAssignLevel.Both);
        var system = (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Single(r => r.IsSystem);

        Assert.Equal(HttpStatusCode.Conflict, (await PutRoles(admin, t.UnitA.Id, (system.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutRoles(admin, t.UnitA.Id, (userOnly.Id, false))).StatusCode);
        var unknownRole = await PutRoles(admin, t.UnitA.Id, (Guid.NewGuid(), false));
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
        Assert.Contains("roles", await unknownRole.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await PutRoles(admin, Guid.NewGuid(), (assignable.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/business-units/{Guid.NewGuid()}/roles")).StatusCode);

        // Geçersiz atanabilir düzey alan hatasıdır; atanmış rol "yalnızca kullanıcıya" yapılamaz.
        var invalidLevel = await admin.PutAsJsonAsync($"/api/roles/{assignable.Id}",
            new { name = "Atanabilir", permissions = new[] { CompaniesRead }, scope = (int)SharedScope.Own, assignLevel = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidLevel.StatusCode);
        await AssignAsync(admin, t.UnitA.Id, (assignable.Id, false));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/roles/{assignable.Id}",
            new SaveRoleRequest("Atanabilir", [CompaniesRead], SharedScope.Own, RoleAssignLevel.User))).StatusCode);

        // Kapalı departmana rol eklenemez ama mevcut atamalar kaldırılabilir.
        var empty = await CreateUnitAsync(admin, t.Root.Id, "Boş Bayi");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/business-units/{empty.Id}/active", new SetBusinessUnitActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutRoles(admin, empty.Id, (assignable.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutRoles(admin, t.UnitA.Id)).StatusCode);

        // Aynı rol iki kez gelirse tek atama olur.
        var twice = await AssignAsync(admin, t.UnitA.Id, (assignable.Id, false), (assignable.Id, true));
        Assert.True(Assert.Single(twice).InheritToChildren);
    }

    [Fact]
    public async Task Deleting_a_role_moves_its_unit_assignments_to_the_target_role()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var doomed = await CreateRoleAsync(admin, "Silinecek", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);
        var target = await CreateRoleAsync(admin, "Hedef", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);
        var userOnlyTarget = await CreateRoleAsync(admin, "Yalnız kullanıcı", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.User);
        await AssignAsync(admin, t.UnitA.Id, (doomed.Id, true));
        await AssignAsync(admin, t.UnitB.Id, (doomed.Id, false), (target.Id, false));

        // Departman ataması da taşınmayı gerektirir; hedef departmana atanamıyorsa reddedilir.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/roles/{doomed.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/roles/{doomed.Id}?moveTo={userOnlyTarget.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/roles/{doomed.Id}?moveTo={target.Id}")).StatusCode);

        var unitA = await admin.GetFromJsonAsync<List<BusinessUnitRoleDto>>($"/api/business-units/{t.UnitA.Id}/roles");
        var moved = Assert.Single(unitA!);
        Assert.Equal(target.Id, moved.RoleId);
        Assert.True(moved.InheritToChildren);
        var unitB = await admin.GetFromJsonAsync<List<BusinessUnitRoleDto>>($"/api/business-units/{t.UnitB.Id}/roles");
        Assert.Equal([target.Id], unitB!.Select(r => r.RoleId));

        // Taşıma denetim kaydına yazılır.
        using var scope = factory.OpenScope(t.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.AuditLog.AsNoTracking().AnyAsync(a =>
            a.EntityKey == CoreModule.AuditedEntityKeys.BusinessUnit && a.RecordId == t.UnitA.Id && a.Action == AuditAction.Updated));
    }

    [Fact]
    public async Task One_tenant_cannot_use_the_roles_or_units_of_another()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var mine = await CreateRoleAsync(admin, "Benim rolüm", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);
        await AssignAsync(admin, t.UnitA.Id, (mine.Id, false));

        var other = await factory.CreateTenantAsync();
        var otherAdminUser = await factory.CreateUserAsync(other);
        await factory.MakeAdministratorAsync(other.Id, otherAdminUser);
        using var otherClient = await LoginAsync(otherAdminUser);
        var theirs = await CreateRoleAsync(otherClient, "Onların rolü", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);
        var theirRoot = (await otherClient.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single();

        // Başka tenant'ın departmanının rolleri okunamaz ya da değiştirilemez; kendi departmanına başkasının rolü verilemez.
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/business-units/{t.UnitA.Id}/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutRoles(otherClient, t.UnitA.Id, (theirs.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutRoles(otherClient, theirRoot.Id, (mine.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutRoles(admin, t.UnitB.Id, (theirs.Id, false))).StatusCode);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!.Where(r => r.Id == mine.Id));

        // Veritabanı: başka tenant'ın rolü departmana bağlanamaz (bileşik yabancı anahtar); oturumsuz bağlantı satır görmez.
        using (var scope = factory.OpenScope(t.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            db.BusinessUnitRoles.Add(new BusinessUnitRole(t.UnitB.Id, theirs.Id, false));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [core].[BusinessUnitRoles]"));
        Assert.Equal(1, await factory.ScalarAsync<int>(t.Tenant.Id, "SELECT COUNT(*) FROM [core].[BusinessUnitRoles]"));
        Assert.Equal(0, await factory.ScalarAsync<int>(other.Id, "SELECT COUNT(*) FROM [core].[BusinessUnitRoles]"));
    }

    [Fact]
    public async Task Assigner_is_limited_to_their_own_permissions_and_scope()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var withWrite = await CreateRoleAsync(admin, "Yazan", CompanyAccess, SharedScope.Own, RoleAssignLevel.Both);
        var readOnly = await CreateRoleAsync(admin, "Okuyan", [CompaniesRead], SharedScope.Own, RoleAssignLevel.Both);
        var unitScoped = await CreateRoleAsync(admin, "Departman okuyan", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);

        // Kullanıcı yönetebilen ama yalnızca firma okuyabilen ve yalnızca kendi kayıtlarını gören kişi.
        var manager = await factory.CreateUserAsync(t.Tenant);
        await factory.CreateRoleAsync(t.Tenant.Id, [ManageUsers, CompaniesRead], Scope.Own, manager);
        // Yönetici yalnızca kendi kapsamındaki departmanları görür (ADR-0026): atama yapacağı bayiye üye olmalıdır.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(
            $"/api/users/{await factory.GetMembershipIdAsync(manager.Id, t.Tenant.Id)}/business-unit", new SetMembershipBusinessUnitRequest(t.UnitA.Id))).StatusCode);
        using var managerClient = await LoginAsync(manager);

        // Kendinde olmayan izni (firma yazma) ya da kapsamı (departman) departmana veremez; kendinde olanı verebilir.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutRoles(managerClient, t.UnitA.Id, (withWrite.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutRoles(managerClient, t.UnitA.Id, (unitScoped.Id, false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutRoles(managerClient, t.UnitA.Id, (readOnly.Id, false))).StatusCode);

        // Yönetici kendi departmanına rol atayınca o rol yöneticiye de geçer (departmandan gelen roller kullanıcıya eklenir):
        // artık bu izne sahip olduğu için aynı atamayı kaldırabilir. Kendinde olmayan izni içeren atamayı kaldırma sınırı
        // yalnızca başka departmanda anlamlıdır ve o departman bu yöneticiden gizlidir (BusinessUnitVisibilityTests).
        await AssignAsync(admin, t.UnitA.Id, (readOnly.Id, false), (withWrite.Id, false));
        Assert.Equal(HttpStatusCode.OK, (await PutRoles(managerClient, t.UnitA.Id, (readOnly.Id, false))).StatusCode);

        // Kullanıcı yönetme izni olmayan kişi hiçbir şey yapamaz.
        var plain = await factory.CreateUserAsync(t.Tenant);
        using var plainClient = await LoginAsync(plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await plainClient.GetAsync($"/api/business-units/{t.UnitA.Id}/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutRoles(plainClient, t.UnitA.Id, (readOnly.Id, false))).StatusCode);
    }

    [Fact]
    public async Task Changes_to_unit_roles_are_audited_with_the_number_of_affected_users()
    {
        var t = await SetUpAsync();
        using var admin = await LoginAsync(t.Admin);
        var role = await CreateRoleAsync(admin, "Denetlenen", [CompaniesRead], SharedScope.Unit, RoleAssignLevel.Both);
        await AssignAsync(admin, t.UnitA.Id, (role.Id, false));
        await AssignAsync(admin, t.UnitA.Id, (role.Id, false));

        using var scope = factory.OpenScope(t.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var entries = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.BusinessUnit && a.RecordId == t.UnitA.Id && a.Action == AuditAction.Updated)
            .ToListAsync();

        // Değişmeyen ikinci istek denetim kaydı yazmaz; ilk değişiklik etkilenen kullanıcı sayısını taşır (A bayisinde A1 ve A2).
        var entry = Assert.Single(entries);
        Assert.Contains("Denetlenen", entry.Changes.ToString());
        Assert.Contains("affectedUsers", entry.Changes.ToString());
    }

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rootUser = await factory.CreateUserAsync(tenant);
        var a1 = await factory.CreateUserAsync(tenant);
        var a2 = await factory.CreateUserAsync(tenant);
        var a1Sub = await factory.CreateUserAsync(tenant);
        var b1 = await factory.CreateUserAsync(tenant);
        var c1 = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);

        using var client = await LoginAsync(admin);
        var root = (await client.GetFromJsonAsync<List<BusinessUnitDto>>("/api/business-units"))!.Single(u => u.ParentId is null);
        var unitA = await CreateUnitAsync(client, root.Id, "A Bayisi");
        var unitA1 = await CreateUnitAsync(client, unitA.Id, "A Alt");
        var unitB = await CreateUnitAsync(client, root.Id, "B Bayisi");
        var unitC = await CreateUnitAsync(client, root.Id, "C Bayisi");
        foreach (var (user, unit) in new[] { (a1, unitA), (a2, unitA), (a1Sub, unitA1), (b1, unitB), (c1, unitC) })
        {
            var move = await client.PutAsJsonAsync(
                $"/api/users/{await MembershipAsync(user, tenant)}/business-unit", new SetMembershipBusinessUnitRequest(unit.Id));
            Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        }

        return new Setup(tenant, admin, rootUser, a1, a2, a1Sub, b1, c1, root, unitA, unitA1, unitB, unitC);
    }

    private sealed record Setup(
        Core.Platform.Tenant Tenant,
        ModulixUser Admin,
        ModulixUser RootUser,
        ModulixUser A1,
        ModulixUser A2,
        ModulixUser A1Sub,
        ModulixUser B1,
        ModulixUser C1,
        BusinessUnitDto Root,
        BusinessUnitDto UnitA,
        BusinessUnitDto UnitA1,
        BusinessUnitDto UnitB,
        BusinessUnitDto UnitC);

    private async Task<Guid> MembershipAsync(ModulixUser user, Core.Platform.Tenant tenant) =>
        await factory.GetMembershipIdAsync(user.Id, tenant.Id);

    private static async Task<BusinessUnitDto> CreateUnitAsync(HttpClient client, Guid parentId, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/business-units", new CreateBusinessUnitRequest(parentId, name, null, BusinessUnitKindLabels.Dealer));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BusinessUnitDetailDto>())!.Unit;
    }

    private static async Task<RoleDto> CreateRoleAsync(
        HttpClient client, string name, string[] permissions, SharedScope scope, RoleAssignLevel level)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest(name, permissions, scope, level));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    private static Task<HttpResponseMessage> PutRoles(HttpClient client, Guid unitId, params (Guid RoleId, bool Inherit)[] roles) =>
        client.PutAsJsonAsync(
            $"/api/business-units/{unitId}/roles",
            new SetBusinessUnitRolesRequest([.. roles.Select(r => new BusinessUnitRoleItem(r.RoleId, r.Inherit))]));

    private static async Task<List<BusinessUnitRoleDto>> AssignAsync(HttpClient client, Guid unitId, params (Guid RoleId, bool Inherit)[] roles)
    {
        var response = await PutRoles(client, unitId, roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<BusinessUnitRoleDto>>())!;
    }

    private async Task CreateTeamAsync(HttpClient client, string name, ModulixUser first, ModulixUser second, Core.Platform.Tenant tenant)
    {
        var created = await client.PostAsJsonAsync("/api/teams", new SaveTeamRequest(name, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = (await created.Content.ReadFromJsonAsync<TeamDetailDto>())!;
        var ids = new[] { await MembershipAsync(first, tenant), await MembershipAsync(second, tenant) };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest(ids))).StatusCode);
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
