using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029: müşteri adayı ve fırsat listelerinde hizmet kapsamı süzgeci (<c>serviceScope=kod</c>). Tam kod eşleşir (kısmi eşleşme yok),
/// süzgeç sunucuda tenant ve kayıt kapsamının içinde çalışır; geçersiz kod süzgeç sayılmaz.
/// </summary>
[Collection("SectorCatalog")]
public class ServiceScopeFilterTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite,
        LeadsRead, LeadsCreate, LeadsUpdate,
    ];

    [Fact]
    public async Task Lists_filter_by_exact_scope_code_for_leads_and_opportunities()
    {
        await InstallLogisticsAsync();
        using var client = await ClientAsync();
        var company = await CompanyAsync(client);

        var road = await LeadAsync(client, ["karayolu-tasimaciligi"]);
        var both = await LeadAsync(client, ["karayolu-tasimaciligi", "gumruklu-antrepo"]);
        var none = await LeadAsync(client, null);
        var oRoad = await OpportunityAsync(client, company.Id, ["karayolu-tasimaciligi"]);
        var oAntrepo = await OpportunityAsync(client, company.Id, ["gumruklu-antrepo"]);

        var leads = await IdsAsync<LeadDto>(client, "/api/crm/leads?serviceScope=karayolu-tasimaciligi&pageSize=100", l => l.Id);
        Assert.Contains(road.Id, leads);
        Assert.Contains(both.Id, leads);
        Assert.DoesNotContain(none.Id, leads);

        var antrepoLeads = await IdsAsync<LeadDto>(client, "/api/crm/leads?serviceScope=gumruklu-antrepo&pageSize=100", l => l.Id);
        Assert.Equal([both.Id], antrepoLeads);

        var opportunities = await IdsAsync<OpportunityDto>(client, "/api/crm/opportunities?serviceScope=gumruklu-antrepo&pageSize=100", o => o.Id);
        Assert.Equal([oAntrepo.Id], opportunities);
        Assert.DoesNotContain(oRoad.Id, opportunities);

        // Kısmi kod eşleşmez ("karayolu" ≠ "karayolu-tasimaciligi").
        Assert.Empty(await IdsAsync<LeadDto>(client, "/api/crm/leads?serviceScope=karayolu&pageSize=100", l => l.Id));
        Assert.Empty(await IdsAsync<OpportunityDto>(client, "/api/crm/opportunities?serviceScope=tasimaciligi&pageSize=100", o => o.Id));
    }

    [Fact]
    public async Task Invalid_scope_codes_are_not_treated_as_filters_and_never_break_the_list()
    {
        await InstallLogisticsAsync();
        using var client = await ClientAsync();
        var company = await CompanyAsync(client);
        var lead = await LeadAsync(client, ["karayolu-tasimaciligi"]);
        var opportunity = await OpportunityAsync(client, company.Id, ["karayolu-tasimaciligi"]);

        // Büyük harf, boşluklu, virgüllü ya da çok uzun değer süzgeç sayılmaz: liste hata vermez.
        foreach (var bad in new[] { "Karayolu", "a%20b", "a,b", "%27%20OR%201=1", new string('x', 60) })
        {
            var leads = await client.GetAsync($"/api/crm/leads?serviceScope={bad}&pageSize=100");
            Assert.True(leads.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, $"lead '{bad}': {leads.StatusCode}");
            var opportunities = await client.GetAsync($"/api/crm/opportunities?serviceScope={bad}&pageSize=100");
            Assert.True(opportunities.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, $"fırsat '{bad}': {opportunities.StatusCode}");
        }

        // Var olmayan ama geçerli biçimli kod boş sonuç döner.
        Assert.Empty(await IdsAsync<LeadDto>(client, "/api/crm/leads?serviceScope=yok-faaliyet&pageSize=100", l => l.Id));
        Assert.Contains(lead.Id, await IdsAsync<LeadDto>(client, "/api/crm/leads?serviceScope=karayolu-tasimaciligi&pageSize=100", l => l.Id));
        Assert.Contains(opportunity.Id, await IdsAsync<OpportunityDto>(client, "/api/crm/opportunities?serviceScope=karayolu-tasimaciligi&pageSize=100", o => o.Id));
    }

    [Fact]
    public async Task The_filter_never_returns_another_tenants_records()
    {
        await InstallLogisticsAsync();
        using var clientA = await ClientAsync();
        using var clientB = await ClientAsync();
        var leadA = await LeadAsync(clientA, ["serbest-depolama"]);
        var oppA = await OpportunityAsync(clientA, (await CompanyAsync(clientA)).Id, ["serbest-depolama"]);

        // B aynı kodla süzer; yalnızca kendi kayıtlarını görür.
        var leadB = await LeadAsync(clientB, ["serbest-depolama"]);
        var leadsB = await IdsAsync<LeadDto>(clientB, "/api/crm/leads?serviceScope=serbest-depolama&pageSize=100", l => l.Id);
        Assert.Equal([leadB.Id], leadsB);
        Assert.DoesNotContain(leadA.Id, leadsB);
        Assert.Empty(await IdsAsync<OpportunityDto>(clientB, "/api/crm/opportunities?serviceScope=serbest-depolama&pageSize=100", o => o.Id));
        Assert.Equal([oppA.Id], await IdsAsync<OpportunityDto>(clientA, "/api/crm/opportunities?serviceScope=serbest-depolama&pageSize=100", o => o.Id));
    }

    [Fact]
    public async Task Users_without_read_permission_or_session_cannot_use_the_filter()
    {
        await InstallLogisticsAsync();
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var seller = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, seller);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], outsider);
        using var sellerClient = await LoginAsync(seller);
        using var outsiderClient = await LoginAsync(outsider);
        await LeadAsync(sellerClient, ["karayolu-tasimaciligi"]);

        // Müşteri adayı/fırsat okuma izni olmayan kullanıcı süzgeçle de listeleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync("/api/crm/leads?serviceScope=karayolu-tasimaciligi")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync("/api/crm/opportunities?serviceScope=karayolu-tasimaciligi")).StatusCode);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/crm/leads?serviceScope=karayolu-tasimaciligi")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/crm/opportunities?serviceScope=karayolu-tasimaciligi")).StatusCode);
    }

    private static async Task<List<Guid>> IdsAsync<T>(HttpClient client, string url, Func<T, Guid> id)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return [.. (await response.Content.ReadFromJsonAsync<CrmPage<T>>())!.Items.Select(id)];
    }

    private static async Task<LeadDto> LeadAsync(HttpClient client, List<string>? scope)
    {
        var response = await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest(
            $"Talep {Guid.NewGuid():N}"[..16], FirstName: "Mehmet", LastName: "Kaya", CompanyName: "Süzgeç Lojistik", Email: "kaya@suzgec.test",
            NeedDescription: "Süzgeç", EstimatedBudget: 10_000m, Source: "Referral", ServiceScope: scope));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LeadDto>())!;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Süzgeç {Guid.NewGuid():N}"[..16]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<OpportunityDto> OpportunityAsync(HttpClient client, Guid companyId, List<string> scope)
    {
        var response = await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest($"Hizmet {Guid.NewGuid():N}"[..14], companyId, ServiceScope: scope));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OpportunityDto>())!;
    }

    private async Task InstallLogisticsAsync()
    {
        var user = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
            stored.IsPlatformAdministrator = true;
            Assert.True((await users.UpdateAsync(stored)).Succeeded);
        }

        using var platform = await LoginAsync(user);
        var response = await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> ClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, user);
        return await LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
