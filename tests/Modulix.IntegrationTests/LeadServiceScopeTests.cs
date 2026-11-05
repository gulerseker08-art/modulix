using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim F2: müşteri adayının hizmet kapsamı ve fırsata dönüşümde aktarımı. Alan faaliyet kataloğundan "Faaliyet" kodlarıdır,
/// kayıt boru hattından geçer (izin, kapsam, denetim); dönüşümde fırsata kopyalanır, sonrasında iki kayıt birbirinden bağımsızdır.
/// </summary>
// Genel faaliyet kataloğunu (başlangıç paketi kurulumu) kullanan sınıflar sırayla çalışır: eşzamanlı kurulum tekil dizin hatası verir.
[Collection("SectorCatalog")]
public class LeadServiceScopeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead,
        LeadsRead, LeadsCreate, LeadsUpdate, LeadsQualify, LeadsConvert,
    ];

    [Fact]
    public async Task A_lead_keeps_a_normalized_service_scope_and_conversion_copies_it_to_the_opportunity()
    {
        await InstallLogisticsAsync();
        var client = await ClientAsync();
        using var _c = client;

        var lead = await CreateAsync(client, ["karayolu-tasimaciligi", "gumruklu-antrepo", "karayolu-tasimaciligi"]);
        Assert.Equal(["gumruklu-antrepo", "karayolu-tasimaciligi"], lead.ServiceScope);

        // Güncelleme kapsamı değiştirir; serbest metin "İlgilenilen ürün/hizmet" alanı ayrıdır ve etkilenmez.
        var put = await client.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", Request(lead.Subject, ["serbest-depolama", "gumruklu-antrepo"], lead.Version) with { ProductInterest = "Soğuk zincir" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<LeadDto>())!;
        Assert.Equal(["gumruklu-antrepo", "serbest-depolama"], updated.ServiceScope);
        Assert.Equal("Soğuk zincir", updated.ProductInterest);

        // Dönüşüm: kapsam fırsata geçer; Lead'in kendi kapsamı korunur.
        var qualified = await QualifyAsync(client, updated);
        var converted = await ConvertAsync(client, qualified);
        var opportunity = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{converted.OpportunityId}"))!;
        Assert.Equal(["gumruklu-antrepo", "serbest-depolama"], opportunity.ServiceScope);
        Assert.Equal(["gumruklu-antrepo", "serbest-depolama"], converted.Lead.ServiceScope);

        // Dönüşümden sonra iki kayıt bağımsızdır: fırsat kapsamı değişir, Lead'inki değişmez.
        var edited = await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new SaveOpportunityRequest(
            opportunity.Title, opportunity.CompanyId, Version: opportunity.Version, ServiceScope: ["karayolu-tasimaciligi"]));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(["gumruklu-antrepo", "serbest-depolama"], (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!.ServiceScope);

        // Kapsamsız Lead kapsamsız fırsat doğurur.
        var plain = await QualifyAsync(client, await CreateAsync(client, null, "Kapsamsız talep"));
        var plainResult = await ConvertAsync(client, plain);
        Assert.Empty((await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{plainResult.OpportunityId}"))!.ServiceScope ?? []);
    }

    [Fact]
    public async Task Lead_service_scope_rejects_unknown_non_activity_inactive_and_too_many_codes_but_conversion_keeps_old_inactive_ones()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var client = await ClientAsync();
        using var _c = client;
        var lead = await CreateAsync(client, ["karayolu-tasimaciligi"]);

        async Task<string> RejectedAsync(List<string> codes)
        {
            var response = await client.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", Request(lead.Subject, codes, lead.Version));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors["serviceScope"][0];
        }

        Assert.Contains("yok-faaliyet", await RejectedAsync(["yok-faaliyet"]));
        Assert.Contains("lojistik", await RejectedAsync(["lojistik"]));     // sektör
        Assert.Contains("depolama", await RejectedAsync(["depolama"]));     // grup
        Assert.Contains("Geçersiz kod", await RejectedAsync(["Büyük Harf"]));
        Assert.Contains("En çok 20", await RejectedAsync([.. Enumerable.Range(0, 21).Select(i => $"kod-{i}")]));

        // Kayıtta duran kod sonradan pasifleşse bile Lead düzenlenebilir ve fırsata aktarılır; yeni eklenen pasif kod reddedilir.
        var catalog = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors/"))!;
        var temporary = $"gecici-{Guid.NewGuid():N}"[..20];
        var parent = catalog.Nodes.Single(n => n.Code == "lojistik");
        var node = (await (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(temporary, parent.Id, "Activity", "Geçici faaliyet", null, 999))).Content.ReadFromJsonAsync<ActivityNodeDto>())!;
        var withTemporary = (await (await client.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", Request(lead.Subject, ["karayolu-tasimaciligi", temporary], lead.Version)))
            .Content.ReadFromJsonAsync<LeadDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{node.Id}/active", new SetActiveRequest(false))).StatusCode);

        var another = await client.PostAsJsonAsync("/api/crm/leads", Request("Pasif eklenmez", [temporary], null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, another.StatusCode);
        Assert.Contains("Pasif faaliyet", (await another.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors["serviceScope"][0]);

        var converted = await ConvertAsync(client, await QualifyAsync(client, withTemporary));
        Assert.Equal([temporary, "karayolu-tasimaciligi"], (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{converted.OpportunityId}"))!.ServiceScope);

        // Reddedilen denemelerin hiçbiri yazılmadı.
        Assert.Equal([temporary, "karayolu-tasimaciligi"], (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!.ServiceScope);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_change_or_convert_a_leads_service_scope()
    {
        await InstallLogisticsAsync();
        var clientA = await ClientAsync();
        var clientB = await ClientAsync();
        using var _a = clientA;
        using var _b = clientB;
        var leadA = await QualifyAsync(clientA, await CreateAsync(clientA, ["karayolu-tasimaciligi"]));

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/leads/{leadA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/crm/leads/{leadA.Id}", Request(leadA.Subject, ["serbest-depolama"], leadA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"/api/crm/leads/{leadA.Id}/convert", new ConvertLeadRequest(leadA.Version, CreateCompany: true))).StatusCode);

        // B kendi Lead'inde aynı kodu bağımsız kullanır; A'nın kaydı değişmez ve dönüştürülmemiştir.
        var leadB = await CreateAsync(clientB, ["serbest-depolama"]);
        Assert.Equal(["serbest-depolama"], leadB.ServiceScope);
        var untouched = (await clientA.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{leadA.Id}"))!;
        Assert.Equal(["karayolu-tasimaciligi"], untouched.ServiceScope);
        Assert.Null(untouched.ConvertedOpportunityId);
    }

    [Fact]
    public async Task Only_users_with_lead_permissions_change_the_service_scope_and_convert_it()
    {
        await InstallLogisticsAsync();
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var seller = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesPermissions, seller);
        await factory.CreateRoleAsync(tenant.Id, [LeadsRead], reader);
        using var sellerClient = await LoginAsync(seller);
        using var readerClient = await LoginAsync(reader);
        var lead = await QualifyAsync(sellerClient, await CreateAsync(sellerClient, ["karayolu-tasimaciligi"]));

        // Salt okuyan kapsamı görür ama oluşturamaz, değiştiremez ve dönüştüremez; kayıt değişmez.
        Assert.Equal(["karayolu-tasimaciligi"], (await readerClient.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!.ServiceScope);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", Request(lead.Subject, ["serbest-depolama"], lead.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/crm/leads", Request("Yetkisiz", ["serbest-depolama"], null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(lead.Version, CreateCompany: true))).StatusCode);
        var unchanged = (await sellerClient.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Equal(["karayolu-tasimaciligi"], unchanged.ServiceScope);
        Assert.Null(unchanged.ConvertedOpportunityId);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/crm/leads/{lead.Id}")).StatusCode);
    }

    private static SaveLeadRequest Request(string subject, List<string>? serviceScope, long? version) => new(
        subject, FirstName: "Mehmet", LastName: "Kaya", CompanyName: "Kapsam Lojistik", Email: "kaya@kapsam.test", NeedDescription: "Kapsam netleşti",
        EstimatedBudget: 75_000m, Source: "Referral", Version: version, ServiceScope: serviceScope);

    private static async Task<LeadDto> CreateAsync(HttpClient client, List<string>? serviceScope, string? subject = null)
    {
        var response = await client.PostAsJsonAsync("/api/crm/leads", Request(subject ?? $"Talep {Guid.NewGuid():N}"[..16], serviceScope, null));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LeadDto>())!;
    }

    private static async Task<LeadDto> QualifyAsync(HttpClient client, LeadDto lead)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LeadDto>())!;
    }

    private static async Task<ConvertLeadResultDto> ConvertAsync(HttpClient client, LeadDto lead)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(lead.Version, CreateCompany: true, CreateContact: true));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConvertLeadResultDto>())!;
    }

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
