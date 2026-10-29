using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0010: firma detay özetlerinin sunucuda hesaplanması ve kişi erişim politikası. Özetler sayfa boyutundan bağımsızdır,
/// para birimleri toplanmaz, erişim olmayan veri sıfır gibi gösterilmez; firmaya bağlı kişi firmanın kapsamına, firmasız
/// kişi kendi sorumlusuna göre görünür; genel ortak kayıt uçları bu kısıtı dolanamaz.
/// </summary>
public class CrmAccessPolicyTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite,
        OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead, ActivitiesRead, ActivitiesWrite,
    ];

    [Fact]
    public async Task Summary_counts_every_accessible_record_not_just_the_first_page()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Özet A.Ş.");

        // 200'ü aşan kayıt: 150 açık TRY, 60 açık USD, 3 kazanılan TRY, 1 kaybedilen.
        for (var i = 0; i < 150; i++)
        {
            await CreateOpportunityAsync(client, company.Id, "Lead", 100m, "TRY");
        }

        for (var i = 0; i < 60; i++)
        {
            await CreateOpportunityAsync(client, company.Id, "Proposal", 10.5m, "USD");
        }

        for (var i = 0; i < 3; i++)
        {
            await CreateOpportunityAsync(client, company.Id, "Won", 1000m, "TRY");
        }

        await CreateOpportunityAsync(client, company.Id, "Lost", 999m, "TRY");

        var past = DateTimeOffset.UtcNow.AddDays(-2);
        for (var i = 0; i < 205; i++)
        {
            var overdue = i < 7;
            var created = await client.PostAsJsonAsync("/api/crm/activities",
                new SaveActivityRequest($"Görev {i}", "Task", overdue ? past : DateTimeOffset.UtcNow.AddDays(3), CompanyId: company.Id));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var summary = (await client.GetFromJsonAsync<CompanySummaryDto>($"/api/crm/companies/{company.Id}/summary"))!;
        Assert.Equal(210, summary.OpenOpportunities);
        Assert.Equal(3, summary.WonOpportunities);
        Assert.Equal(205, summary.OpenActivities);
        Assert.Equal(7, summary.OverdueActivities);

        // Para birimleri ayrı ayrı toplanır; hiçbir zaman tek tutara çevrilmez.
        Assert.Equal(
            [("TRY", 15000m, 150), ("USD", 630m, 60)],
            summary.OpenTotals!.Select(t => (t.Currency, t.Amount, t.Count)));
        Assert.Equal([("TRY", 3000m, 3)], summary.WonTotals!.Select(t => (t.Currency, t.Amount, t.Count)));
    }

    [Fact]
    public async Task Summary_hides_amounts_and_sections_the_user_cannot_read()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        using var adminClient = await LoginAsync(admin);
        var company = await CreateCompanyAsync(adminClient, "Gizli Tutar A.Ş.");
        await CreateOpportunityAsync(adminClient, company.Id, "Lead", 500m, "TRY");

        var noAmounts = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead], Scope.All, noAmounts);
        using var noAmountsClient = await LoginAsync(noAmounts);
        var summary = (await noAmountsClient.GetFromJsonAsync<CompanySummaryDto>($"/api/crm/companies/{company.Id}/summary"))!;
        Assert.Equal(1, summary.OpenOpportunities);
        Assert.Null(summary.OpenTotals);
        Assert.Null(summary.WonTotals);

        // Aktivite okuma izni yok: bölüm yok (null), sıfır değil.
        Assert.Null(summary.OpenActivities);
        Assert.Null(summary.OverdueActivities);

        var onlyCompany = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, onlyCompany);
        using var onlyCompanyClient = await LoginAsync(onlyCompany);
        var bare = (await onlyCompanyClient.GetFromJsonAsync<CompanySummaryDto>($"/api/crm/companies/{company.Id}/summary"))!;
        Assert.Null(bare.OpenOpportunities);
        Assert.Null(bare.WonOpportunities);
    }

    [Fact]
    public async Task Summary_respects_the_data_scope_of_the_company_and_the_opportunities()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        using var adminClient = await LoginAsync(admin);
        using var clientA = await LoginAsync(repA);
        using var clientB = await LoginAsync(repB);

        var company = await CreateCompanyAsync(clientA, "A'nın firması");
        await CreateOpportunityAsync(clientA, company.Id, "Lead", 10m, "TRY");
        await CreateOpportunityAsync(adminClient, company.Id, "Lead", 20m, "TRY", owner: admin.Id);

        // A yalnızca kendi fırsatını sayar; yönetici ikisini.
        Assert.Equal(1, (await clientA.GetFromJsonAsync<CompanySummaryDto>($"/api/crm/companies/{company.Id}/summary"))!.OpenOpportunities);
        Assert.Equal(2, (await adminClient.GetFromJsonAsync<CompanySummaryDto>($"/api/crm/companies/{company.Id}/summary"))!.OpenOpportunities);

        // B firmayı göremez: özet de yoktur.
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/companies/{company.Id}/summary")).StatusCode);
    }

    [Fact]
    public async Task Contacts_follow_the_company_scope_and_loose_contacts_follow_their_owner()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        using var adminClient = await LoginAsync(admin);
        using var clientA = await LoginAsync(repA);
        using var clientB = await LoginAsync(repB);

        var companyA = await CreateCompanyAsync(clientA, "A firması");
        var companyB = await CreateCompanyAsync(clientB, "B firması");
        var linked = await CreateContactAsync(clientA, "Ali", "Bağlı", companyA.Id);
        var loose = await CreateContactAsync(clientA, "Ayşe", "Firmasız", null);

        // A kendi kişilerini görür; B hiçbirini göremez (liste, ayrıntı, güncelleme, silme).
        Assert.Equal(2, (await clientA.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts"))!.TotalCount);
        Assert.Equal(0, (await clientB.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts"))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/contacts/{linked.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/contacts/{loose.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/crm/contacts/{linked.Id}",
            new SaveContactRequest("Ali", "Ele", companyA.Id, Version: linked.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/crm/contacts/{loose.Id}?version={loose.Version}")).StatusCode);

        // Firmaya bağlı kişi, firmanın sorumlusu değişince kapsamıyla birlikte el değiştirir.
        var reassigned = await adminClient.PutAsJsonAsync($"/api/crm/companies/{companyA.Id}",
            new SaveCompanyRequest(companyA.Name, Version: companyA.Version, OwnerId: repB.Id));
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync($"/api/crm/contacts/{linked.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/api/crm/contacts/{linked.Id}")).StatusCode);

        // İlişki kurmak için firmaya erişim gerekir: A, B'nin firmasına kişi bağlayamaz.
        var attach = await clientA.PutAsJsonAsync($"/api/crm/contacts/{loose.Id}",
            new SaveContactRequest("Ayşe", "Firmasız", companyB.Id, Version: loose.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, attach.StatusCode);
        Assert.Contains("companyId", await attach.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clientA.PostAsJsonAsync("/api/crm/contacts",
            new SaveContactRequest("Yeni", "Kişi", companyB.Id))).StatusCode);

        // Yönetici (tüm kayıtlar) hepsini görür.
        Assert.Equal(2, (await adminClient.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts"))!.TotalCount);
    }

    [Fact]
    public async Task Team_scope_sees_loose_contacts_of_teammates()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var lead = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Team, lead);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, rep, outsider);
        using var adminClient = await LoginAsync(admin);
        var team = (await (await adminClient.PostAsJsonAsync("/api/teams", new Modulix.Shared.Users.SaveTeamRequest($"Takım {Guid.NewGuid():N}"[..12], null))).Content
            .ReadFromJsonAsync<Modulix.Shared.Users.TeamDetailDto>())!;
        var members = new[] { await factory.GetMembershipIdAsync(lead.Id, tenant.Id), await factory.GetMembershipIdAsync(rep.Id, tenant.Id) };
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync($"/api/teams/{team.Id}/members", new Modulix.Shared.Users.SetTeamMembersRequest(members))).StatusCode);

        using var repClient = await LoginAsync(rep);
        using var leadClient = await LoginAsync(lead);
        using var outsiderClient = await LoginAsync(outsider);
        var loose = await CreateContactAsync(repClient, "Takım", "Kişisi", null);

        Assert.Equal(HttpStatusCode.OK, (await leadClient.GetAsync($"/api/crm/contacts/{loose.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/api/crm/contacts/{loose.Id}")).StatusCode);
    }

    [Fact]
    public async Task Generic_shared_record_endpoints_cannot_bypass_the_crm_scope()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        using var adminClient = await LoginAsync(admin);
        using var clientA = await LoginAsync(repA);
        using var clientB = await LoginAsync(repB);

        var company = (await (await clientA.PostAsJsonAsync("/api/crm/companies",
            new SaveCompanyRequest("Gizli Ltd.", TaxNumber: "5550001110", Phone: "0212 000 00 00", Email: "gizli@firma.test", City: "Bursa"))).Content
            .ReadFromJsonAsync<CompanyDto>())!;
        var contact = await CreateContactAsync(clientA, "Gizli", "Kişi", company.Id, email: "gizli.kisi@firma.test", phone: "0555 000 00 00");

        // Sahibi tam profili görür.
        var own = (await clientA.GetFromJsonAsync<RecordResponse>($"/api/records/{SharedEntities.Person}/{contact.PersonId}"))!;
        Assert.Equal("gizli.kisi@firma.test", own.Values["email"]!.GetValue<string>());

        // Kapsam dışındaki kullanıcı yalnızca asgari kimliği görür: iletişim ve firma bağı gelmez.
        var person = (await clientB.GetFromJsonAsync<RecordResponse>($"/api/records/{SharedEntities.Person}/{contact.PersonId}"))!;
        Assert.Equal("Gizli", person.Values["firstName"]!.GetValue<string>());
        Assert.Null(person.Values["email"]);
        Assert.Null(person.Values["phone"]);
        Assert.Null(person.Values["partyId"]);

        var party = (await clientB.GetFromJsonAsync<RecordResponse>($"/api/records/{SharedEntities.Party}/{company.PartyId}"))!;
        Assert.Equal("Gizli Ltd.", party.Values["name"]!.GetValue<string>());
        Assert.Equal("5550001110", party.Values["taxNumber"]!.GetValue<string>());
        Assert.Null(party.Values["email"]);
        Assert.Null(party.Values["phone"]);
        Assert.Null(party.Values["city"]);

        // Listede de aynı; değiştirme ve silme bulunamadı döner.
        var list = (await clientB.GetFromJsonAsync<JsonNode>($"/api/records/{SharedEntities.Person}?search=Gizli"))!;
        Assert.Null(list["items"]![0]!["values"]!["email"]);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/records/{SharedEntities.Person}/{contact.PersonId}",
            new RecordWriteRequest(new JsonObject { ["email"] = "ele.gecir@x.test" }, person.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.DeleteAsync($"/api/records/{SharedEntities.Party}/{company.PartyId}?version={party.Version}")).StatusCode);

        // Yönetici (tüm kayıtlar) kısıtlanmaz.
        var admins = (await adminClient.GetFromJsonAsync<RecordResponse>($"/api/records/{SharedEntities.Person}/{contact.PersonId}"))!;
        Assert.Equal("gizli.kisi@firma.test", admins.Values["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task Company_kind_and_tax_office_are_served_by_the_crm_contract()
    {
        var (_, admin) = await TenantWithAdminAsync();
        using var client = await LoginAsync(admin);

        var created = await client.PostAsJsonAsync("/api/crm/companies",
            new SaveCompanyRequest("Şahıs Firması", TaxNumber: "12345678901", Kind: "Individual", TaxOffice: "Kadıköy"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal("Individual", company.Kind);
        Assert.Equal("Kadıköy", company.TaxOffice);

        // Tüzel kişide 10 hane beklenir; gerçek kişide 11.
        var wrong = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Hatalı", TaxNumber: "123", Kind: "Organization"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Contains("taxNumber", await wrong.Content.ReadAsStringAsync());

        // Eski istemci türü ve vergi dairesini göndermez: mevcut değer korunur.
        var oldClient = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}",
            new SaveCompanyRequest("Şahıs Firması", TaxNumber: "12345678901", City: "İstanbul", Version: company.Version));
        Assert.Equal(HttpStatusCode.OK, oldClient.StatusCode);
        var kept = (await oldClient.Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal("Individual", kept.Kind);
        Assert.Equal("Kadıköy", kept.TaxOffice);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> TenantWithAdminAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return (tenant, admin);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<ContactDto> CreateContactAsync(
        HttpClient client, string first, string last, Guid? companyId, string? email = null, string? phone = null)
    {
        var response = await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last, companyId, Email: email, Phone: phone));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContactDto>())!;
    }

    private static async Task CreateOpportunityAsync(
        HttpClient client, Guid companyId, string stage, decimal amount, string currency, Guid? owner = null)
    {
        // Kapalı aşamada doğrudan açılamaz (ADR-0017): açık açılır, sonra kazan / kaybet eylemiyle kapatılır.
        var closing = stage is "Won" or "Lost";
        var response = await client.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest($"Fırsat {Guid.NewGuid():N}"[..14], companyId, Stage: closing ? "Qualified" : stage, Amount: amount, Currency: currency, OwnerId: owner));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        if (closing)
        {
            var created = (await response.Content.ReadFromJsonAsync<OpportunityDto>())!;
            var close = stage == "Won"
                ? await client.PostAsJsonAsync($"/api/crm/opportunities/{created.Id}/win", new WinOpportunityRequestDto(created.Version))
                : await client.PostAsJsonAsync($"/api/crm/opportunities/{created.Id}/lose", new LoseOpportunityRequestDto(created.Version, "Price"));
            Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        }
    }
}
