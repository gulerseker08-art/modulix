using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 Dilim F3: teklifin sunum anında sabitlenen hizmet kapsamı. Bağlı fırsatın o anki kapsamı faaliyet adlarıyla teklifin
/// <c>serviceScopeSnapshot</c> alanına yazılır; fırsat ya da katalog sonradan değişse de sunulmuş teklif aynı kalır, revizyon yeni
/// taslak olarak snapshot'sız başlar ve kendi sunumunda yeniden sabitlenir. Alan yalnızca sunum eylemiyle yazılır.
/// </summary>
// Genel faaliyet kataloğunu (başlangıç paketi kurulumu) kullanan sınıflar sırayla çalışır: eşzamanlı kurulum tekil dizin hatası verir.
[Collection("SectorCatalog")]
public class QuoteServiceScopeSnapshotTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Presenting_freezes_the_opportunitys_scope_with_names_and_later_changes_do_not_touch_it()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        // Geçici bir faaliyet: adı sonradan değişecek.
        var temporary = $"gecici-{Guid.NewGuid():N}"[..20];
        var parent = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors/"))!.Nodes.Single(n => n.Code == "lojistik");
        var node = (await (await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest(temporary, parent.Id, "Activity", "İlk ad", null, 999))).Content.ReadFromJsonAsync<ActivityNodeDto>())!;
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities",
            new SaveOpportunityRequest("Kapsamlı fırsat", s.CompanyId, ServiceScope: ["karayolu-tasimaciligi", temporary]));

        // Taslakta snapshot yoktur; sunumda fırsatın kapsamı adlarıyla sabitlenir.
        var draft = await PostAsync<QuoteDto>(admin, "/api/crm/quotes",
            new SaveQuoteRequest("Teklif", s.CompanyId, OpportunityId: opportunity.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Null(draft.ServiceScopeSnapshot);
        await AddLineAsync(admin, draft.Id);
        var presented = await TransitionAsync(admin, draft.Id, "present");
        var roadName = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors/"))!.Nodes.Single(n => n.Code == "karayolu-tasimaciligi").Name;
        Assert.Equal([(temporary, "İlk ad"), ("karayolu-tasimaciligi", roadName)],
            presented.ServiceScopeSnapshot!.Select(i => (i.Code, i.Name)).OrderBy(i => i.Code));

        // Fırsat kapsamı ve katalog adı sonradan değişir; sunulmuş teklif aynı kalır.
        var changed = await admin.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new SaveOpportunityRequest(
            opportunity.Title, s.CompanyId, Version: opportunity.Version, ServiceScope: ["serbest-depolama"]));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var rename = await platform.PutAsJsonAsync($"/api/platform/sectors/nodes/{node.Id}", new UpdateActivityNodeRequest("Yeni ad", null, node.SortOrder, node.Readiness, node.Version));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var again = await GetQuoteAsync(admin, presented.Id);
        Assert.Equal(presented.ServiceScopeSnapshot!.Select(i => (i.Code, i.Name)).Order(), again.ServiceScopeSnapshot!.Select(i => (i.Code, i.Name)).Order());

        // Revizyon snapshot'sız taslak olarak başlar; kendi sunumunda fırsatın o günkü kapsamını sabitler. Eski sürüm değişmez.
        var revision = (await (await admin.PostAsJsonAsync($"/api/crm/quotes/{presented.Id}/revise", new ReviseQuoteRequest("Kapsam değişti"))).Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Null(revision.ServiceScopeSnapshot);
        var presentedRevision = await TransitionAsync(admin, revision.Id, "present");
        var storageName = (await platform.GetFromJsonAsync<SectorCatalogResponse>("/api/platform/sectors/"))!.Nodes.Single(n => n.Code == "serbest-depolama").Name;
        Assert.Equal([("serbest-depolama", storageName)], presentedRevision.ServiceScopeSnapshot!.Select(i => (i.Code, i.Name)));
        Assert.Equal(2, (await GetQuoteAsync(admin, presented.Id)).ServiceScopeSnapshot!.Count);

        // Fırsatsız ya da kapsamsız fırsatlı teklif snapshot taşımaz.
        var plain = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Fırsatsız", s.CompanyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await AddLineAsync(admin, plain.Id);
        Assert.Null((await TransitionAsync(admin, plain.Id, "present")).ServiceScopeSnapshot);
        var empty = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Kapsamsız", s.CompanyId));
        var emptyQuote = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Kapsamsız teklif", s.CompanyId, OpportunityId: empty.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await AddLineAsync(admin, emptyQuote.Id);
        Assert.Null((await TransitionAsync(admin, emptyQuote.Id, "present")).ServiceScopeSnapshot);
    }

    [Fact]
    public async Task The_snapshot_cannot_be_written_by_clients_and_malformed_values_are_rejected_or_ignored_without_effect()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", s.CompanyId, ServiceScope: ["karayolu-tasimaciligi"]));
        var draft = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Teklif", s.CompanyId, OpportunityId: opportunity.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));

        // Genel kayıt ucundan gelen sahte snapshot yok sayılır; biçimsiz değer alan hatası verir. İkisinde de teklif değişmez.
        var forged = await admin.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Quote}/{draft.Id}", new RecordWriteRequest(
            new JsonObject { ["serviceScopeSnapshot"] = new JsonArray(new JsonObject { ["code"] = "sahte", ["name"] = "Sahte kapsam" }) }, draft.Version));
        Assert.True(forged.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, forged.StatusCode.ToString());
        Assert.Null((await GetQuoteAsync(admin, draft.Id)).ServiceScopeSnapshot);
        var malformed = await admin.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Quote}/{draft.Id}", new RecordWriteRequest(
            new JsonObject { ["serviceScopeSnapshot"] = "metin" }, (await GetQuoteAsync(admin, draft.Id)).Version));
        Assert.True(malformed.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, malformed.StatusCode.ToString());
        Assert.Null((await GetQuoteAsync(admin, draft.Id)).ServiceScopeSnapshot);

        // Sunumdan sonra da yalnızca sunumda sabitlenen değer kalır (sahte değer onu ezmez).
        await AddLineAsync(admin, draft.Id);
        var presented = await TransitionAsync(admin, draft.Id, "present");
        await admin.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Quote}/{presented.Id}", new RecordWriteRequest(
            new JsonObject { ["serviceScopeSnapshot"] = new JsonArray(new JsonObject { ["code"] = "sahte", ["name"] = "Sahte kapsam" }) }, presented.Version));
        Assert.Equal(["karayolu-tasimaciligi"], (await GetQuoteAsync(admin, presented.Id)).ServiceScopeSnapshot!.Select(i => i.Code));
    }

    [Fact]
    public async Task Another_tenants_quotes_and_snapshots_stay_separate()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var opportunityA = await PostAsync<OpportunityDto>(adminA, "/api/crm/opportunities", new SaveOpportunityRequest("A fırsatı", a.CompanyId, ServiceScope: ["karayolu-tasimaciligi"]));
        var quoteA = await PostAsync<QuoteDto>(adminA, "/api/crm/quotes", new SaveQuoteRequest("A teklifi", a.CompanyId, OpportunityId: opportunityA.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await AddLineAsync(adminA, quoteA.Id);
        var presentedA = await TransitionAsync(adminA, quoteA.Id, "present");

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/quotes/{presentedA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/crm/quotes/{presentedA.Id}/revise", new ReviseQuoteRequest("Yabancı"))).StatusCode);

        // B, A'nın fırsatına teklif açamaz; kendi fırsatında aynı kodla bağımsız snapshot alır.
        var foreign = await adminB.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Yabancı fırsat", b.CompanyId, OpportunityId: opportunityA.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        var opportunityB = await PostAsync<OpportunityDto>(adminB, "/api/crm/opportunities", new SaveOpportunityRequest("B fırsatı", b.CompanyId, ServiceScope: ["serbest-depolama"]));
        var quoteB = await PostAsync<QuoteDto>(adminB, "/api/crm/quotes", new SaveQuoteRequest("B teklifi", b.CompanyId, OpportunityId: opportunityB.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await AddLineAsync(adminB, quoteB.Id);
        var presentedB = await TransitionAsync(adminB, quoteB.Id, "present");
        Assert.Equal(["serbest-depolama"], presentedB.ServiceScopeSnapshot!.Select(i => i.Code));
        Assert.Equal(["karayolu-tasimaciligi"], (await GetQuoteAsync(adminA, presentedA.Id)).ServiceScopeSnapshot!.Select(i => i.Code));
    }

    [Fact]
    public async Task Reading_needs_quote_read_permission_and_presenting_freezes_only_with_the_present_permission()
    {
        using var platform = await PlatformAsync();
        await InstallLogisticsAsync(platform);
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var viewer = await LoginAsync(s.Viewer);
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", s.CompanyId, ServiceScope: ["karayolu-tasimaciligi"]));
        var draft = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Teklif", s.CompanyId, OpportunityId: opportunity.Id, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await AddLineAsync(admin, draft.Id);

        // Yalnızca okuma izni olan kullanıcı sunamaz; teklif değişmez, snapshot oluşmaz.
        var denied = await viewer.PostAsJsonAsync($"/api/crm/quotes/{draft.Id}/present", new QuoteTransitionRequest(draft.Version));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null((await GetQuoteAsync(admin, draft.Id)).ServiceScopeSnapshot);

        // Sunum sonrası okuma izni yeter: fırsat okuma izni olmayan kullanıcı da teklifin sabitlenmiş kapsamını görür.
        var presented = await TransitionAsync(admin, draft.Id, "present");
        Assert.Equal(["karayolu-tasimaciligi"], (await GetQuoteAsync(viewer, presented.Id)).ServiceScopeSnapshot!.Select(i => i.Code));

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/crm/quotes/{presented.Id}")).StatusCode);
    }

    private static async Task AddLineAsync(HttpClient client, Guid quoteId) =>
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines", new SaveQuoteLineRequest(quoteId, "Kalem", "Free", null, 2m, 1000m, null, "Percent", 10m, 20m, null, "Standard"));

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Viewer, Guid CompanyId, Guid TenantId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [QuotesRead, CompaniesRead], Scope.All, viewer);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, viewer, company.Id, tenant.Id);
    }

    private async Task InstallLogisticsAsync(HttpClient platform)
    {
        var response = await platform.PostAsJsonAsync("/api/platform/sectors/starter-packages/logistics/install", new { });
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

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
