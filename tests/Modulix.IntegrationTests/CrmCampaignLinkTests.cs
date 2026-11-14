using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Lookups;
using Modulix.Shared.Marketing;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Müşteri adayı ve fırsatın kampanya bağı (<c>campaignId</c>; ADR-0014, ADR-0017, ADR-0025). Kampanya Pazarlama modülünün kaydıdır: CRM onun tablosunu
/// görmez, bağ kayıt sırasında kayıt boru hattından çağıranın yetkisiyle doğrulanır. Dört test türü: mutlu yol, doğrulama, tenant izolasyonu, yetki.
/// </summary>
public class CrmCampaignLinkTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Sales =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead,
        LeadsRead, LeadsCreate, LeadsUpdate, LeadsQualify, LeadsConvert,
    ];

    private static readonly string[] Marketing = [MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsWrite];

    [Fact]
    public async Task Campaign_is_linked_to_a_lead_filtered_and_carried_to_the_converted_opportunity()
    {
        var tenant = await TenantAsync();
        using var client = await UserClientAsync(tenant, [.. Sales, .. Marketing]);
        var campaign = await CampaignAsync(client, "bahar");
        var other = await CampaignAsync(client, "yaz");

        var lead = await PostAsync<LeadDto>(client, "/api/crm/leads", new SaveLeadRequest(
            "Fuar talebi", FirstName: "Mehmet", LastName: "Kaya", CompanyName: "Kaya Lojistik", Email: "kaya@firma.test",
            NeedDescription: "Kapsam netleşti", EstimatedBudget: 75_000m, Source: "Referral", CampaignId: campaign.Id));
        Assert.Equal(campaign.Id, lead.CampaignId);
        await PostAsync<LeadDto>(client, "/api/crm/leads", new SaveLeadRequest("Başka talep", LastName: "Demir", CampaignId: other.Id));
        await PostAsync<LeadDto>(client, "/api/crm/leads", new SaveLeadRequest("Kampanyasız talep", LastName: "Çelik"));

        // Süzgeç: kampanyanın müşteri adayları.
        var byCampaign = (await client.GetFromJsonAsync<CrmPage<LeadDto>>($"/api/crm/leads?campaignId={campaign.Id}"))!;
        Assert.Equal(lead.Id, Assert.Single(byCampaign.Items).Id);

        // Lookup: kampanya adıyla aranır, seçili kimlik etikete çözülür.
        var search = (await client.GetFromJsonAsync<LookupPageDto>($"/api/lookups/{CrmLookupDefinitions.LeadCampaign}/search?q=bahar"))!;
        Assert.Equal(campaign.Id, Assert.Single(search.Items).Id);

        // Dönüşüm: kampanya fırsata geçer; fırsatlar da kampanyaya göre süzülür.
        var qualified = await ReadAsync<LeadDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version)));
        var converted = await ReadAsync<ConvertLeadResultDto>(
            await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert", new ConvertLeadRequest(qualified.Version, CreateCompany: true)));
        var opportunity = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{converted.OpportunityId}"))!;
        Assert.Equal(campaign.Id, opportunity.CampaignId);
        var opportunities = (await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?campaignId={campaign.Id}"))!;
        Assert.Equal(opportunity.Id, Assert.Single(opportunities.Items).Id);

        // Fırsatta kampanya elle değiştirilebilir ve boşaltılabilir.
        var moved = await ReadAsync<OpportunityDto>(await client.PutAsJsonAsync(
            $"/api/crm/opportunities/{opportunity.Id}", Save(opportunity) with { CampaignId = other.Id }));
        Assert.Equal(other.Id, moved.CampaignId);
        var cleared = await ReadAsync<OpportunityDto>(await client.PutAsJsonAsync(
            $"/api/crm/opportunities/{opportunity.Id}", Save(moved) with { CampaignId = null }));
        Assert.Null(cleared.CampaignId);
    }

    [Fact]
    public async Task Unknown_campaign_is_rejected_on_lead_and_opportunity()
    {
        var tenant = await TenantAsync();
        using var client = await UserClientAsync(tenant, [.. Sales, .. Marketing]);

        var lead = await ProblemAsync(await client.PostAsJsonAsync(
            "/api/crm/leads", new SaveLeadRequest("Konu", LastName: "Kaya", CampaignId: Guid.NewGuid())));
        Assert.Contains("campaignId", lead.Errors.Keys);

        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Kaya Lojistik"));
        var opportunity = await ProblemAsync(await client.PostAsJsonAsync(
            "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", company.Id, CampaignId: Guid.NewGuid())));
        Assert.Contains("campaignId", opportunity.Errors.Keys);
    }

    [Fact]
    public async Task Campaign_of_another_tenant_cannot_be_linked_or_seen()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        using var a = await UserClientAsync(tenantA, [.. Sales, .. Marketing]);
        using var b = await UserClientAsync(tenantB, [.. Sales, .. Marketing]);
        var campaignA = await CampaignAsync(a, "bahar");
        var leadA = await PostAsync<LeadDto>(a, "/api/crm/leads", new SaveLeadRequest("Konu", LastName: "Kaya", CampaignId: campaignA.Id));

        // B, A'nın kampanyasını bağlayamaz: var olmayan kampanyayla aynı cevap.
        var problem = await ProblemAsync(await b.PostAsJsonAsync(
            "/api/crm/leads", new SaveLeadRequest("Konu", LastName: "Demir", CampaignId: campaignA.Id)));
        Assert.Contains("campaignId", problem.Errors.Keys);

        // B'nin süzgeci ve lookup'ı A'nın verisini döndürmez.
        Assert.Empty((await b.GetFromJsonAsync<CrmPage<LeadDto>>($"/api/crm/leads?campaignId={campaignA.Id}"))!.Items);
        Assert.Empty((await b.GetFromJsonAsync<LookupPageDto>($"/api/lookups/{CrmLookupDefinitions.LeadCampaign}/search?q=bahar"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/crm/leads/{leadA.Id}")).StatusCode);
    }

    [Fact]
    public async Task User_without_campaign_permission_cannot_link_but_keeps_an_existing_link()
    {
        var tenant = await TenantAsync();
        using var marketer = await UserClientAsync(tenant, [.. Sales, .. Marketing]);
        using var seller = await UserClientAsync(tenant, Sales);
        var campaign = await CampaignAsync(marketer, "bahar");
        var lead = await PostAsync<LeadDto>(marketer, "/api/crm/leads", new SaveLeadRequest("Konu", LastName: "Kaya", CampaignId: campaign.Id));

        // Kampanya okuma izni olmayan satışçı kampanya bağlayamaz ve kampanyaları arayamaz.
        var problem = await ProblemAsync(await seller.PostAsJsonAsync(
            "/api/crm/leads", new SaveLeadRequest("Konu", LastName: "Demir", CampaignId: campaign.Id)));
        Assert.Contains("campaignId", problem.Errors.Keys);
        var search = await seller.GetAsync($"/api/lookups/{CrmLookupDefinitions.LeadCampaign}/search?q=bahar");
        Assert.True(
            search.StatusCode == HttpStatusCode.Forbidden || (await search.Content.ReadFromJsonAsync<LookupPageDto>())!.Items.Count == 0,
            "Kampanya okuma izni olmayan kullanıcı kampanya adlarını görmemeli.");

        // Değişmeyen bağ yeniden gönderilirse denetlenmez: satışçı başka alanı düzenlerken mevcut bağ engel olmaz ve korunur.
        var edited = await ReadAsync<LeadDto>(await seller.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new SaveLeadRequest(
            "Konu güncellendi", LastName: "Kaya", Version: lead.Version, CampaignId: campaign.Id)));
        Assert.Equal(campaign.Id, edited.CampaignId);
        Assert.Equal("Konu güncellendi", edited.Subject);

        // Bağı boşaltmak serbesttir.
        var cleared = await ReadAsync<LeadDto>(await seller.PutAsJsonAsync($"/api/crm/leads/{lead.Id}", new SaveLeadRequest(
            "Konu güncellendi", LastName: "Kaya", Version: edited.Version)));
        Assert.Null(cleared.CampaignId);
    }

    // ---- Yardımcılar

    private static SaveOpportunityRequest Save(OpportunityDto o) =>
        new(o.Title, o.CompanyId, o.ContactId, Currency: o.Currency, Version: o.Version, OwnerId: o.OwnerId, CampaignId: o.CampaignId);

    private async Task<Modulix.Core.Platform.Tenant> TenantAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        await factory.SetModuleAsync(tenant.Id, MarketingModule.ModuleKey, enabled: true);
        return tenant;
    }

    private async Task<HttpClient> UserClientAsync(Modulix.Core.Platform.Tenant tenant, string[] permissions)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, user);
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<CampaignDto> CampaignAsync(HttpClient client, string code) =>
        PostAsync<CampaignDto>(client, "/api/marketing/campaigns", new SaveCampaignRequest(
            Code: code, Name: $"Kampanya {code}", Target: "Company", Channel: "Email", RecipientRule: "PrimaryContact"));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
