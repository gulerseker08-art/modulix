using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y8a: ekipler ve rol bazında kayıt kapsamı. Kapsam listede sorguda (sayfalama ve toplam doğru), tek kayıtta 404 olarak
/// uygulanır; sorumlu kapsam dışına atanamaz; en geniş rol geçerlidir; ekip değişikliği bir sonraki istekte görünür.
/// </summary>
public class DataScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] CompanyAccess = [CompaniesRead, CompaniesWrite];

    [Fact]
    public async Task Own_and_team_scopes_limit_lists_details_and_owner_assignment()
    {
        var (tenant, admin, lead, rep1, rep2, outsider) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var team = await CreateTeamAsync(adminClient, "Satış", lead, rep1, rep2);
        Assert.Equal(3, team.Members.Count);

        using var rep1Client = await LoginAsync(rep1);
        using var rep2Client = await LoginAsync(rep2);
        using var outsiderClient = await LoginAsync(outsider);
        using var leadClient = await LoginAsync(lead);
        for (var i = 0; i < 3; i++)
        {
            await CreateCompanyAsync(rep1Client, $"Rep1 {i}");
        }

        var rep2Company = await CreateCompanyAsync(rep2Client, "Rep2 firma");
        var outsiderCompany = await CreateCompanyAsync(outsiderClient, "Dış firma");

        // Yalnızca kendi kayıtları: sayfa ve toplam kapsamla hesaplanır.
        var page = await rep1Client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=2");
        Assert.Equal(3, page!.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, c => Assert.Equal(rep1.Id, c.OwnerId));

        Assert.Equal(HttpStatusCode.NotFound, (await rep1Client.GetAsync($"/api/crm/companies/{rep2Company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await rep1Client.PutAsJsonAsync($"/api/crm/companies/{rep2Company.Id}", new SaveCompanyRequest("Ele geçir", Version: rep2Company.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rep1Client.DeleteAsync($"/api/crm/companies/{rep2Company.Id}?version={rep2Company.Version}")).StatusCode);

        var assignToOther = await rep1Client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Başkasına", OwnerId: rep2.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, assignToOther.StatusCode);
        Assert.Contains("ownerId", await assignToOther.Content.ReadAsStringAsync());
        Assert.Equal(3, (await rep1Client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);

        // Ekibinin kayıtları: kendisi ve ekip arkadaşları; ekip dışındakiler görünmez.
        var leadList = await leadClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?pageSize=200");
        Assert.Equal(4, leadList!.TotalCount);
        Assert.DoesNotContain(leadList.Items, c => c.Id == outsiderCompany.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await leadClient.GetAsync($"/api/crm/companies/{outsiderCompany.Id}")).StatusCode);

        // Ekip arkadaşına atayabilir, ekip dışına atayamaz.
        var moved = await leadClient.PutAsJsonAsync($"/api/crm/companies/{rep2Company.Id}",
            new SaveCompanyRequest(rep2Company.Name, Version: rep2Company.Version, OwnerId: rep1.Id));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var movedCompany = (await moved.Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await leadClient.PutAsJsonAsync($"/api/crm/companies/{rep2Company.Id}",
            new SaveCompanyRequest(rep2Company.Name, Version: movedCompany.Version, OwnerId: outsider.Id))).StatusCode);

        // Atanabilir üyeler kapsamla aynıdır; tüm üyeler (adlar için) herkese açıktır.
        var rep1Assignable = await rep1Client.GetFromJsonAsync<List<TenantMemberResponse>>("/api/tenant/members?assignable=true&entityKey=crm.company");
        Assert.Equal([rep1.Id], rep1Assignable!.Select(m => m.UserId));
        var leadAssignable = await leadClient.GetFromJsonAsync<List<TenantMemberResponse>>("/api/tenant/members?assignable=true&entityKey=crm.company");
        Assert.Equal(new[] { lead.Id, rep1.Id, rep2.Id }.Order(), leadAssignable!.Select(m => m.UserId).Order());
        Assert.Equal(5, (await rep1Client.GetFromJsonAsync<List<TenantMemberResponse>>("/api/tenant/members"))!.Count);

        // Yönetici tüm kayıtları görür.
        Assert.Equal(5, (await adminClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);

        var myAccess = await rep1Client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access");
        Assert.Equal(Modulix.Shared.Access.RecordScope.Own, myAccess!.Scope);
        var leadAccess = await adminClient.GetFromJsonAsync<UserAccessDto>(
            $"/api/users/{await factory.GetMembershipIdAsync(lead.Id, tenant.Id)}/access");
        Assert.Equal(Modulix.Shared.Access.RecordScope.Team, leadAccess!.Scope);
        Assert.Equal(["Satış"], leadAccess.Teams);
    }

    [Fact]
    public async Task Assignable_members_require_an_entity_key()
    {
        var (_, admin, _, _, _, _) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);

        // Kapsam kayıt türüne göre çözülür (ADR-0030); türsüz istek sessizce geniş sonuç vermek yerine reddedilir.
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.GetAsync("/api/tenant/members?assignable=true")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.GetAsync("/api/tenant/members?assignable=true&entityKey=%20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/tenant/members")).StatusCode);
    }

    [Fact]
    public async Task Widest_role_wins_and_team_changes_apply_on_the_next_request()
    {
        var (tenant, admin, lead, rep1, rep2, outsider) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var team = await CreateTeamAsync(adminClient, "Ankara", lead, rep1, rep2);
        using var rep2Client = await LoginAsync(rep2);
        using var leadClient = await LoginAsync(lead);
        using var outsiderClient = await LoginAsync(outsider);
        await CreateCompanyAsync(rep2Client, "Rep2 firma");
        await CreateCompanyAsync(outsiderClient, "Dış firma");

        Assert.Equal(1, (await leadClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);

        // rep2 ekipten çıkarılınca lider onun kaydını artık görmez (kullanıcı ayrıntısından).
        var rep2Membership = await factory.GetMembershipIdAsync(rep2.Id, tenant.Id);
        Assert.Equal(HttpStatusCode.NoContent,
            (await adminClient.PutAsJsonAsync($"/api/users/{rep2Membership}/teams", new SetMembershipTeamsRequest([]))).StatusCode);
        Assert.Empty((await adminClient.GetFromJsonAsync<List<TeamDto>>($"/api/users/{rep2Membership}/teams"))!);
        Assert.Equal(0, (await leadClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);
        Assert.Equal(2, (await adminClient.GetFromJsonAsync<List<TeamDto>>("/api/teams"))!.Single(t => t.Id == team.Id).MemberCount);

        // İkinci, "tüm kayıtlar" kapsamlı rol: en geniş kapsam geçerlidir.
        var all = await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var leadMembership = await factory.GetMembershipIdAsync(lead.Id, tenant.Id);
            var current = (await roles.GetMembershipRolesAsync(leadMembership, CancellationToken.None))!;
            Assert.True(await roles.SetMembershipRolesAsync(leadMembership, [.. current.Select(r => r.Id), all.Id], CancellationToken.None));
        }

        Assert.Equal(2, (await leadClient.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.TotalCount);
    }

    [Fact]
    public async Task Teams_are_managed_with_user_management_audited_and_kept_per_tenant()
    {
        var (tenant, admin, _, rep1, _, _) = await SetUpAsync();
        var other = await factory.CreateTenantAsync();
        var stranger = await factory.CreateUserAsync(other);
        using var adminClient = await LoginAsync(admin);
        using var rep1Client = await LoginAsync(rep1);

        Assert.Equal(HttpStatusCode.Forbidden, (await rep1Client.GetAsync("/api/teams")).StatusCode);

        var team = await CreateTeamAsync(adminClient, "Muhasebe", rep1);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync("/api/teams", new SaveTeamRequest(" Muhasebe ", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync("/api/teams", new SaveTeamRequest(" ", null))).StatusCode);

        var renamed = await adminClient.PutAsJsonAsync($"/api/teams/{team.Id}", new SaveTeamRequest("Finans", "Muhasebe ve finans"));
        Assert.Equal("Finans", (await renamed.Content.ReadFromJsonAsync<TeamDetailDto>())!.Name);

        // Başka firmanın üyesi eklenemez; başka firmanın ekibi görünmez.
        var strangerMembership = await factory.GetMembershipIdAsync(stranger.Id, other.Id);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminClient.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest([strangerMembership]))).StatusCode);
        var otherAdmin = await factory.CreateUserAsync(other);
        await MakeAdministratorAsync(other.Id, otherAdmin);
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<TeamDto>>("/api/teams"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/teams/{team.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.DeleteAsync($"/api/teams/{team.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/teams/{team.Id}")).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var actions = await db.AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.Team && a.RecordId == team.Id)
            .OrderBy(a => a.OccurredAt)
            .Select(a => a.Action)
            .ToListAsync();
        Assert.Equal(
            [Core.Records.AuditAction.Created, Core.Records.AuditAction.Updated, Core.Records.AuditAction.Updated, Core.Records.AuditAction.Deleted],
            actions);
        Assert.Empty(await db.TeamMembers.Where(tm => tm.TeamId == team.Id).ToListAsync());
    }

    [Fact]
    public async Task Role_manager_cannot_grant_a_wider_scope_than_their_own()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageRoles, CompaniesRead], Scope.Own, manager);
        using var client = await LoginAsync(manager);

        var wide = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Geniş", [CompaniesRead], Modulix.Shared.Access.RecordScope.All));
        Assert.Equal(HttpStatusCode.Forbidden, wide.StatusCode);

        var narrow = await client.PostAsJsonAsync("/api/roles", new SaveRoleRequest("Dar", [CompaniesRead], Modulix.Shared.Access.RecordScope.Own));
        Assert.Equal(HttpStatusCode.Created, narrow.StatusCode);
        var role = (await narrow.Content.ReadFromJsonAsync<RoleDto>())!;
        Assert.Equal(Modulix.Shared.Access.RecordScope.Own, role.Scope);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{role.Id}",
            new SaveRoleRequest("Dar", [CompaniesRead], Modulix.Shared.Access.RecordScope.Team))).StatusCode);

        // Kapsam verilmeden düzenlemek kapsamı değiştirmez.
        var renamed = await client.PutAsJsonAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Dar okur", [CompaniesRead]));
        Assert.Equal(Modulix.Shared.Access.RecordScope.Own, (await renamed.Content.ReadFromJsonAsync<RoleDto>())!.Scope);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Lead, ModulixUser Rep1, ModulixUser Rep2, ModulixUser Outsider)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var lead = await factory.CreateUserAsync(tenant);
        var rep1 = await factory.CreateUserAsync(tenant);
        var rep2 = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, CompanyAccess, Scope.Team, lead);
        await factory.CreateRoleAsync(tenant.Id, CompanyAccess, Scope.Own, rep1, rep2, outsider);
        return (tenant, admin, lead, rep1, rep2, outsider);
    }

    private async Task<TeamDetailDto> CreateTeamAsync(HttpClient client, string name, params ModulixUser[] members)
    {
        var created = await client.PostAsJsonAsync("/api/teams", new SaveTeamRequest(name, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var team = (await created.Content.ReadFromJsonAsync<TeamDetailDto>())!;
        var tenantId = (await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.ActiveTenantId!.Value;
        var ids = new List<Guid>();
        foreach (var member in members)
        {
            ids.Add(await factory.GetMembershipIdAsync(member.Id, tenantId));
        }

        var set = await client.PutAsJsonAsync($"/api/teams/{team.Id}/members", new SetTeamMembersRequest(ids));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        return (await set.Content.ReadFromJsonAsync<TeamDetailDto>())!;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
