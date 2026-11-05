using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim F1: fırsatın hizmet kapsamı. Faaliyet kataloğundan "Faaliyet" düğümü kodları, kayıt boru hattından geçer
/// (izin, kapsam, denetim); katalog global, değerler tenant verisidir. Kod listesi tekrarsız ve sıralı saklanır.
/// </summary>
// Genel faaliyet kataloğunu (başlangıç paketi kurulumu) kullanan sınıflar sırayla çalışır: eşzamanlı kurulum tekil dizin hatası verir.
[Collection("SectorCatalog")]
public class OpportunityServiceScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesPermissions =
        [CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead];

    [Fact]
    public async Task An_opportunity_keeps_a_normalized_service_scope_and_it_can_be_replaced_or_cleared()
    {
        await InstallLogisticsAsync();
        var (client, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);

        var created = await SaveAsync(client, company.Id, ["karayolu-tasimaciligi", "gumruklu-antrepo", "karayolu-tasimaciligi"]);
        Assert.Equal(["gumruklu-antrepo", "karayolu-tasimaciligi"], created.ServiceScope);
        Assert.Equal(["gumruklu-antrepo", "karayolu-tasimaciligi"], (await GetAsync(client, created.Id)).ServiceScope);

        // Serbest metin kapsam alanından ayrıdır; ikisi birlikte yazılır ve birbirini etkilemez.
        var put = await client.PutAsJsonAsync($"/api/crm/opportunities/{created.Id}", new SaveOpportunityRequest(
            created.Title, company.Id, Version: created.Version, Scope: "Yıllık depolama ve dağıtım", ServiceScope: ["serbest-depolama"]));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var replaced = (await put.Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal(["serbest-depolama"], replaced.ServiceScope);
        Assert.Equal("Yıllık depolama ve dağıtım", replaced.Scope);

        var cleared = await client.PutAsJsonAsync($"/api/crm/opportunities/{created.Id}", new SaveOpportunityRequest(
            created.Title, company.Id, Version: replaced.Version, Scope: replaced.Scope, ServiceScope: []));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<OpportunityDto>())!.ServiceScope ?? []);

        // Alan, fırsat formunun varsayılan düzeninde "Detaylar" sekmesindedir.
        var layouts = (await client.GetFromJsonAsync<List<FormLayoutDto>>("/api/form-layouts"))!;
        var opportunity = layouts.Single(l => l.EntityKey == CrmModule.Entities.Opportunity);
        Assert.Contains(opportunity.Fields, f => f.Key == "serviceScope" && f.Section == "detay");
    }

    [Fact]
    public async Task Service_scope_rejects_unknown_non_activity_inactive_and_malformed_codes_without_writing()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var (client, _) = await ClientAsync();
        using var _c = client;
        var company = await CompanyAsync(client);
        var opportunity = await SaveAsync(client, company.Id, ["karayolu-tasimaciligi"]);

        async Task<string[]> RejectedAsync(object serviceScope, string field = "serviceScope")
        {
            var body = new JsonObject
            {
                ["title"] = opportunity.Title, ["companyId"] = company.Id.ToString(), ["version"] = opportunity.Version,
                ["serviceScope"] = JsonSerializer(serviceScope),
            };
            var response = await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors[field];
        }

        Assert.Contains("yok-faaliyet", (await RejectedAsync(new[] { "yok-faaliyet" }))[0]);
        Assert.Contains("lojistik", (await RejectedAsync(new[] { "lojistik" }))[0]);      // sektör
        Assert.Contains("depolama", (await RejectedAsync(new[] { "depolama" }))[0]);      // grup
        Assert.Contains("Geçersiz kod", (await RejectedAsync(new[] { "Büyük Harf" }))[0]);
        // Tipli uç liste olmayan gövdeyi daha ayrıştırırken (400) reddeder; genel kayıt ucu alan hatası (422) verir.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new JsonObject
        {
            ["title"] = opportunity.Title, ["companyId"] = company.Id.ToString(), ["version"] = opportunity.Version, ["serviceScope"] = "serbest-depolama",
        })).StatusCode);
        var generic = await client.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Opportunity}/{opportunity.Id}",
            new RecordWriteRequest(new JsonObject { ["serviceScope"] = "serbest-depolama" }, opportunity.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, generic.StatusCode);
        Assert.Contains("Metin listesi", (await generic.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors["serviceScope"][0]);
        Assert.Contains("En çok 20", (await RejectedAsync(Enumerable.Range(0, 21).Select(i => $"kod-{i}").ToArray()))[0]);

        // Yeni eklenen pasif faaliyet reddedilir; kayıtta zaten duran kod pasifleşse bile başka alan düzenlenirken hata vermez.
        var catalog = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors/"))!;
        var temporary = $"gecici-{Guid.NewGuid():N}"[..20];
        var sector = catalog.Nodes.Single(n => n.Code == "lojistik");
        var node = await (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(temporary, sector.Id, "Activity", "Geçici faaliyet", null, 999))).Content.ReadFromJsonAsync<ActivityNodeDto>();
        var withTemporary = await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new SaveOpportunityRequest(
            opportunity.Title, company.Id, Version: opportunity.Version, ServiceScope: ["karayolu-tasimaciligi", temporary]));
        Assert.Equal(HttpStatusCode.OK, withTemporary.StatusCode);
        var current = (await withTemporary.Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{node!.Id}/active", new SetActiveRequest(false))).StatusCode);

        var edited = await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new SaveOpportunityRequest(
            current.Title, company.Id, Version: current.Version, Notes: "Başka alan", ServiceScope: current.ServiceScope));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var another = await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Pasif eklenmez", company.Id, ServiceScope: [temporary]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, another.StatusCode);
        Assert.Contains("Pasif faaliyet", (await another.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors["serviceScope"][0]);

        // Reddedilen denemelerin hiçbiri yazılmadı.
        Assert.Equal([temporary, "karayolu-tasimaciligi"], (await GetAsync(client, opportunity.Id)).ServiceScope); // kodlar sıralı saklanır
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_change_the_service_scope()
    {
        await InstallLogisticsAsync();
        var (clientA, _) = await ClientAsync();
        var (clientB, _) = await ClientAsync();
        using var _a = clientA;
        using var _b = clientB;
        var companyA = await CompanyAsync(clientA);
        var opportunityA = await SaveAsync(clientA, companyA.Id, ["karayolu-tasimaciligi"]);

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/opportunities/{opportunityA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/crm/opportunities/{opportunityA.Id}",
            new SaveOpportunityRequest(opportunityA.Title, companyA.Id, Version: opportunityA.Version, ServiceScope: ["serbest-depolama"]))).StatusCode);

        // B, A'nın firmasına fırsat açamaz; kendi firmasında aynı kodu bağımsız kullanır.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await clientB.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Yabancı firma", companyA.Id, ServiceScope: ["karayolu-tasimaciligi"]))).StatusCode);
        var companyB = await CompanyAsync(clientB);
        var opportunityB = await SaveAsync(clientB, companyB.Id, ["serbest-depolama"]);
        Assert.Equal(["serbest-depolama"], opportunityB.ServiceScope);
        Assert.Equal(["karayolu-tasimaciligi"], (await GetAsync(clientA, opportunityA.Id)).ServiceScope);
    }

    [Fact]
    public async Task Only_users_with_opportunity_write_permission_can_change_the_service_scope()
    {
        await InstallLogisticsAsync();
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var seller = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, seller);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead], reader);
        using var sellerClient = await LoginAsync(seller);
        using var readerClient = await LoginAsync(reader);
        var company = await CompanyAsync(sellerClient);
        var opportunity = await SaveAsync(sellerClient, company.Id, ["karayolu-tasimaciligi"]);

        // Salt okuyan kapsamı görür ama değiştiremez; yazma ve oluşturma 403, değer değişmez.
        Assert.Equal(["karayolu-tasimaciligi"], (await GetAsync(readerClient, opportunity.Id)).ServiceScope);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest(opportunity.Title, company.Id, Version: opportunity.Version, ServiceScope: ["serbest-depolama"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Yetkisiz", company.Id, ServiceScope: ["serbest-depolama"]))).StatusCode);
        Assert.Equal(["karayolu-tasimaciligi"], (await GetAsync(sellerClient, opportunity.Id)).ServiceScope);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/crm/opportunities/{opportunity.Id}")).StatusCode);
    }

    private static JsonNode? JsonSerializer(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value);

    private async Task InstallLogisticsAsync(HttpClient? platform = null)
    {
        using var own = platform is null ? await PlatformAsync() : null;
        var response = await (platform ?? own!).PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> PlatformAsync()
    {
        var user = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
            stored.IsPlatformAdministrator = true;
            Assert.True((await users.UpdateAsync(stored)).Succeeded);
        }

        return await LoginAsync(user);
    }

    private async Task<(HttpClient Client, Guid TenantId)> ClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, user);
        return (await LoginAsync(user), tenant.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Kapsam {Guid.NewGuid():N}"[..16]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<OpportunityDto> SaveAsync(HttpClient client, Guid companyId, List<string> serviceScope)
    {
        var response = await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest($"Hizmet {Guid.NewGuid():N}"[..14], companyId, ServiceScope: serviceScope));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OpportunityDto>())!;
    }

    private static async Task<OpportunityDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{id}"))!;
}
