using System.Net;
using System.Net.Http.Json;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0008 adım 2: üyelik–şirket bağı, oturumda erişilebilir ve aktif şirket, her istekte doğrulama ve şirket yönetimi.
/// Yönetici (sistem rolü) bütün şirketlere erişir; yönetici olmayan yalnızca görevlendirildiği şirketleri görür.
/// </summary>
public class CompanyScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Administrator_sees_all_companies_and_active_is_the_default()
    {
        var tenant = await factory.CreateTenantAsync();
        var defaultCompany = await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var second = await factory.CreateCompanyAsync(tenant.Id, "İkinci Şirket");
        var user = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, user);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var access = await GetAccessAsync(client);

        Assert.Equal(new[] { defaultCompany.Id, second.Id }.OrderBy(id => id), access.Companies.Select(c => c.Id).OrderBy(id => id));
        Assert.Equal(defaultCompany.Id, access.ActiveCompanyId);
    }

    [Fact]
    public async Task Non_administrator_sees_only_assigned_companies()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var assigned = await factory.CreateCompanyAsync(tenant.Id, "Depo A.Ş.");
        var user = await factory.CreateUserAsync(tenant);
        var membershipId = await factory.GetMembershipIdAsync(user.Id, tenant.Id);
        await factory.AssignCompanyAsync(tenant.Id, membershipId, assigned.Id);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var access = await GetAccessAsync(client);

        var only = Assert.Single(access.Companies);
        Assert.Equal(assigned.Id, only.Id);
        Assert.Equal(assigned.Id, access.ActiveCompanyId);
    }

    [Fact]
    public async Task User_without_company_assignment_has_no_active_company()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var access = await GetAccessAsync(client);

        Assert.Empty(access.Companies);
        Assert.Null(access.ActiveCompanyId);
    }

    [Fact]
    public async Task Active_company_can_be_selected_and_persists()
    {
        var tenant = await factory.CreateTenantAsync();
        var defaultCompany = await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var second = await factory.CreateCompanyAsync(tenant.Id, "İkinci Şirket");
        var user = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, user);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);
        Assert.Equal(defaultCompany.Id, (await GetAccessAsync(client)).ActiveCompanyId);

        var selected = await client.PostAsJsonAsync("/api/auth/active-company", new SelectCompanyRequest(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);

        Assert.Equal(second.Id, (await GetAccessAsync(client)).ActiveCompanyId);
    }

    [Fact]
    public async Task Selecting_an_inaccessible_company_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        var defaultCompany = await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var other = await factory.CreateCompanyAsync(tenant.Id, "Erişilemez");
        var user = await factory.CreateUserAsync(tenant);
        var membershipId = await factory.GetMembershipIdAsync(user.Id, tenant.Id);
        await factory.AssignCompanyAsync(tenant.Id, membershipId, defaultCompany.Id);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var rejected = await client.PostAsJsonAsync("/api/auth/active-company", new SelectCompanyRequest(other.Id));

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(defaultCompany.Id, (await GetAccessAsync(client)).ActiveCompanyId);
    }

    [Fact]
    public async Task Switching_tenant_resets_active_company_to_default()
    {
        var first = await factory.CreateTenantAsync();
        var firstDefault = await factory.CreateCompanyAsync(first.Id, "Birinci Ana", isDefault: true);
        var firstSecond = await factory.CreateCompanyAsync(first.Id, "Birinci İkinci");
        var second = await factory.CreateTenantAsync();
        var secondDefault = await factory.CreateCompanyAsync(second.Id, "İkinci Ana", isDefault: true);
        var user = await factory.CreateUserAsync(first, second);
        await factory.MakeAdministratorAsync(first.Id, user);
        await factory.MakeAdministratorAsync(second.Id, user);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!, autoSelected: false);

        await SelectTenantAsync(client, first.Id);
        await client.PostAsJsonAsync("/api/auth/active-company", new SelectCompanyRequest(firstSecond.Id));
        Assert.Equal(firstSecond.Id, (await GetAccessAsync(client)).ActiveCompanyId);

        await SelectTenantAsync(client, second.Id);
        Assert.Equal(secondDefault.Id, (await GetAccessAsync(client)).ActiveCompanyId);

        // İlk tenant'a dönünce açık seçim düşmüştür: aktif şirket yine varsayılandır.
        await SelectTenantAsync(client, first.Id);
        Assert.Equal(firstDefault.Id, (await GetAccessAsync(client)).ActiveCompanyId);
    }

    [Fact]
    public async Task Company_management_requires_permission()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/companies")).StatusCode);
    }

    [Fact]
    public async Task Administrator_can_create_and_rename_companies_but_not_duplicate_names()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, user);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var created = await client.PostAsJsonAsync("/api/companies", new SaveOperatingCompanyRequest("Lojistik A.Ş."));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<OperatingCompanyDto>())!;
        Assert.False(company.IsDefault);

        var renamed = await client.PutAsJsonAsync($"/api/companies/{company.Id}", new SaveOperatingCompanyRequest("Lojistik Ltd."));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/companies", new SaveOperatingCompanyRequest("Lojistik Ltd."));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Membership_company_assignment_is_applied()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.CreateCompanyAsync(tenant.Id, "Ana Şirket", isDefault: true);
        var warehouse = await factory.CreateCompanyAsync(tenant.Id, "Depo");
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var member = await factory.CreateUserAsync(tenant);
        var memberMembershipId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);

        using var adminClient = factory.CreateBrowserClient();
        await LoginAsync(adminClient, admin.Email!);
        var set = await adminClient.PutAsJsonAsync(
            $"/api/memberships/{memberMembershipId}/companies", new SetMembershipCompaniesRequest([warehouse.Id]));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        using var memberClient = factory.CreateBrowserClient();
        await LoginAsync(memberClient, member.Email!);
        var access = await GetAccessAsync(memberClient);
        var only = Assert.Single(access.Companies);
        Assert.Equal(warehouse.Id, only.Id);
    }

    private static async Task LoginAsync(HttpClient client, string email, bool autoSelected = true)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (autoSelected)
        {
            var me = (await response.Content.ReadFromJsonAsync<MeResponse>())!;
            Assert.NotNull(me.ActiveTenantId);
        }
    }

    private static async Task SelectTenantAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenantId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<MyAccessResponse> GetAccessAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access"))!;
}
