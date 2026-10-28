using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0011: firma–kişi ilişkisi. Aynı kişi birden fazla firmayla farklı unvan ve rollerle bağlanır, ilişki bitince geçmiş
/// korunur, ana irtibat firma başına tektir (eşzamanlı isteklerde de), eski tek firma alanı ilişkilerden türetilir.
/// </summary>
public class CompanyRelationshipTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Links = "/api/crm/company-contacts";

    private static readonly string[] SalesAccess =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, ActivitiesRead, ActivitiesWrite,
    ];

    [Fact]
    public async Task Purchasing_and_finance_officers_are_tracked_separately_within_one_company()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Roller A.Ş.");
        var buyer = await CreateContactAsync(client, "Burak", "Alıcı");
        var accountant = await CreateContactAsync(client, "Fatma", "Muhasebe");

        var purchasing = await CreateLinkAsync(client, new SaveCompanyContactRequest(
            company.Id, buyer.Id, "Satın alma", "Satın alma müdürü", [CrmRelationshipRoles.Purchasing, CrmRelationshipRoles.Management],
            IsPrimary: true, IsDecisionMaker: true));
        var finance = await CreateLinkAsync(client, new SaveCompanyContactRequest(
            company.Id, accountant.Id, "Muhasebe", "Mali işler", [CrmRelationshipRoles.Finance]));

        Assert.Equal("Burak Alıcı", purchasing.ContactName);
        Assert.Equal([CrmRelationshipRoles.Purchasing, CrmRelationshipRoles.Management], purchasing.Roles);
        Assert.True(purchasing.IsPrimary);
        Assert.True(purchasing.IsDecisionMaker);
        Assert.False(finance.IsPrimary);

        var list = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?companyId={company.Id}"))!;
        Assert.Equal(2, list.TotalCount);
        Assert.Equal(CrmRelationshipRoles.Finance, Assert.Single(list.Items, l => l.ContactId == accountant.Id).Roles.Single());

        // Bilinmeyen ilişki rolü reddedilir (güvenlik rolleriyle karıştırılamaz).
        var wrongRole = await client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(company.Id, accountant.Id, Roles: ["Administrator"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongRole.StatusCode);
    }

    [Fact]
    public async Task Same_person_in_two_companies_keeps_separate_titles_and_roles()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var first = await CreateCompanyAsync(client, "Birinci A.Ş.");
        var second = await CreateCompanyAsync(client, "İkinci Ltd.");
        var person = await CreateContactAsync(client, "Çift", "Görevli", title: "Danışman");

        var a = await CreateLinkAsync(client, new SaveCompanyContactRequest(first.Id, person.Id, JobTitle: "Finans direktörü", Roles: [CrmRelationshipRoles.Finance]));
        var b = await CreateLinkAsync(client, new SaveCompanyContactRequest(second.Id, person.Id, JobTitle: "Teknik müdür", Roles: [CrmRelationshipRoles.Technical]));

        var links = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={person.Id}"))!;
        Assert.Equal(["Finans direktörü", "Teknik müdür"], links.Items.Select(l => l.JobTitle).Order());

        // Firmaya özgü unvan kişinin genel unvanını (ve diğer ilişkiyi) bozmaz; kişi tek kayıttır.
        var updated = await client.PutAsJsonAsync($"{Links}/{a.Id}", Save(a) with { JobTitle = "Mali işler başkanı" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var reloaded = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{person.Id}"))!;
        Assert.Equal("Danışman", reloaded.Title);
        Assert.Equal("Teknik müdür", (await client.GetFromJsonAsync<CompanyContactDto>($"{Links}/{b.Id}"))!.JobTitle);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<ContactDto>>("/api/crm/contacts?search=Görevli"))!.TotalCount);

        // Aynı firma–kişi çifti için ikinci aktif ilişki açılamaz.
        var duplicate = await client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(first.Id, person.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
    }

    [Fact]
    public async Task Ending_a_relationship_keeps_the_person_and_history()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Ayrılış A.Ş.");
        var person = await CreateContactAsync(client, "Ayrılan", "Kişi");
        var link = await CreateLinkAsync(client, new SaveCompanyContactRequest(company.Id, person.Id, IsPrimary: true));
        var activity = await client.PostAsJsonAsync("/api/crm/activities",
            new SaveActivityRequest("Görüşme", "Call", CompanyId: company.Id, ContactId: person.Id));
        Assert.Equal(HttpStatusCode.Created, activity.StatusCode);
        Assert.Equal(company.Id, (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{person.Id}"))!.CompanyId);

        var ended = await client.PutAsJsonAsync($"{Links}/{link.Id}", Save(link) with { EndDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        var endedLink = (await ended.Content.ReadFromJsonAsync<CompanyContactDto>())!;
        Assert.False(endedLink.IsActive);
        Assert.False(endedLink.IsPrimary);

        // Kişi, ilişki kaydı ve aktivite durur; birincil firma kalmaz; aktif listede ilişki yoktur.
        var contact = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{person.Id}"))!;
        Assert.Null(contact.CompanyId);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?companyId={company.Id}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?companyId={company.Id}&active=true"))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?contactId={person.Id}"))!.TotalCount);

        // Bitiş tarihi başlangıçtan önce olamaz.
        var invalid = await client.PutAsJsonAsync($"{Links}/{link.Id}", Save(endedLink) with { Version = endedLink.Version, EndDate = new DateOnly(2000, 1, 1) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task Primary_contact_is_unique_per_company_even_under_concurrent_requests()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Tekil A.Ş.");
        var other = await CreateCompanyAsync(client, "Başka A.Ş.");
        var people = new List<ContactDto>();
        for (var i = 0; i < 6; i++)
        {
            people.Add(await CreateContactAsync(client, $"Aday{i}", "Kişi"));
        }

        var first = await CreateLinkAsync(client, new SaveCompanyContactRequest(company.Id, people[0].Id, IsPrimary: true));
        var second = await client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(company.Id, people[1].Id, IsPrimary: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("isPrimary", await second.Content.ReadAsStringAsync());

        // Başka firmanın ana irtibatı etkilenmez; aynı kişi başka firmada da ana irtibat olabilir.
        await CreateLinkAsync(client, new SaveCompanyContactRequest(other.Id, people[0].Id, IsPrimary: true));

        // Değiştirme: eskisinin işareti kalkınca yenisi alınır.
        await client.PutAsJsonAsync($"{Links}/{first.Id}", Save(first) with { IsPrimary = false });
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(company.Id, people[1].Id, IsPrimary: true))).StatusCode);

        // Eşzamanlı: aynı firmada aynı anda ana irtibat isteyen 4 istekten yalnızca biri geçer; veritabanı da tekilliği korur.
        var concurrent = await Task.WhenAll(people.Skip(2).Select(p =>
            client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(company.Id, p.Id, IsPrimary: true))));
        Assert.Equal(0, concurrent.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(concurrent, r => Assert.Contains((int)r.StatusCode, new[] { 409, 422 }));

        // Önce hepsinde ana irtibat yokken aynı anda dört istek: tam biri kazanır.
        var third = await CreateCompanyAsync(client, "Yarış A.Ş.");
        var race = await Task.WhenAll(people.Take(4).Select(p =>
            client.PostAsJsonAsync(Links, new SaveCompanyContactRequest(third.Id, p.Id, IsPrimary: true))));
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(race.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Contains((int)r.StatusCode, new[] { 409, 422 }));
        var primaries = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?companyId={third.Id}&pageSize=50"))!.Items.Count(l => l.IsPrimary);
        Assert.Equal(1, primaries);
    }

    [Fact]
    public async Task Legacy_company_field_is_derived_from_relationships_without_losing_links()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var oldCompany = await CreateCompanyAsync(client, "Eski A.Ş.");
        var newCompany = await CreateCompanyAsync(client, "Yeni A.Ş.");
        var third = await CreateCompanyAsync(client, "Üçüncü A.Ş.");

        // Eski istemci: kişiyi firmayla oluşturur; ilişki oluşur.
        var contact = await CreateContactAsync(client, "Eski", "İstemci", companyId: oldCompany.Id);
        Assert.Equal(oldCompany.Id, contact.CompanyId);
        var links = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={contact.Id}"))!;
        Assert.Equal(oldCompany.Id, Assert.Single(links.Items).CompanyId);

        // Yeni API ile ikinci firma bağlanır; eski alan en eski aktif ilişkiyi göstermeye devam eder.
        await CreateLinkAsync(client, new SaveCompanyContactRequest(third.Id, contact.Id));
        var unchanged = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{contact.Id}"))!;
        Assert.Equal(oldCompany.Id, unchanged.CompanyId);

        // Ekranın kaydı aynı firmayla yeniden göndermesi hiçbir ilişkiyi bozmaz.
        var resend = await client.PutAsJsonAsync($"/api/crm/contacts/{contact.Id}",
            new SaveContactRequest("Eski", "İstemci", unchanged.CompanyId, Version: unchanged.Version));
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={contact.Id}&active=true"))!.TotalCount);

        // Eski alanla firma değiştirme: önceki birincil ilişki biter, yenisi açılır, üçüncü korunur.
        var moved = await client.PutAsJsonAsync($"/api/crm/contacts/{contact.Id}",
            new SaveContactRequest("Eski", "İstemci", newCompany.Id, Version: (await resend.Content.ReadFromJsonAsync<ContactDto>())!.Version));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var all = (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={contact.Id}"))!.Items;
        Assert.Equal(3, all.Count);
        Assert.False(all.Single(l => l.CompanyId == oldCompany.Id).IsActive);
        Assert.True(all.Single(l => l.CompanyId == newCompany.Id).IsActive);
        Assert.True(all.Single(l => l.CompanyId == third.Id).IsActive);

        // Birincil firma yeniden türetilir: kalan aktif ilişkilerin en eskisi (üçüncü, yeniden açılan yeniden eski değil).
        var current = (await client.GetFromJsonAsync<ContactDto>($"/api/crm/contacts/{contact.Id}"))!;
        Assert.Equal(third.Id, current.CompanyId);

        // Kişi silinince ilişkileri de gider.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/crm/contacts/{contact.Id}?version={current.Version}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={contact.Id}"))!.TotalCount);
    }

    [Fact]
    public async Task Relationships_require_access_to_both_ends_and_follow_the_company_scope()
    {
        var (tenant, admin) = await SetUpAsync();
        var repA = await factory.CreateUserAsync(tenant);
        var repB = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, repA, repB);
        using var adminClient = await LoginAsync(admin);
        using var clientA = await LoginAsync(repA);
        using var clientB = await LoginAsync(repB);

        var companyA = await CreateCompanyAsync(clientA, "A firması");
        var companyB = await CreateCompanyAsync(clientB, "B firması");
        var shared = await CreateContactAsync(adminClient, "Ortak", "Kişi");
        var contactA = await CreateContactAsync(clientA, "Ali", "Kişisi");

        // A, B'nin firmasına ilişki kuramaz; B, A'nın kişisini bağlayamaz (kişiyi göremez).
        var toForeignCompany = await clientA.PostAsJsonAsync(Links, new SaveCompanyContactRequest(companyB.Id, contactA.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toForeignCompany.StatusCode);
        Assert.Contains("companyId", await toForeignCompany.Content.ReadAsStringAsync());
        var foreignContact = await clientB.PostAsJsonAsync(Links, new SaveCompanyContactRequest(companyB.Id, contactA.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignContact.StatusCode);
        Assert.Contains("contactId", await foreignContact.Content.ReadAsStringAsync());

        // Ortak (yönetici sorumlulu) kişi iki firmayla bağlanınca her temsilci yalnızca kendi firmasındaki ilişkiyi görür.
        await adminClient.PostAsJsonAsync(Links, new SaveCompanyContactRequest(companyA.Id, shared.Id, JobTitle: "A tarafı"));
        await adminClient.PostAsJsonAsync(Links, new SaveCompanyContactRequest(companyB.Id, shared.Id, JobTitle: "B tarafı"));
        var seenByA = (await clientA.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={shared.Id}"))!;
        Assert.Equal("A tarafı", Assert.Single(seenByA.Items).JobTitle);
        var seenByB = (await clientB.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={shared.Id}"))!;
        Assert.Equal("B tarafı", Assert.Single(seenByB.Items).JobTitle);
        Assert.Equal(2, (await adminClient.GetFromJsonAsync<CrmPage<CompanyContactDto>>($"{Links}?contactId={shared.Id}"))!.TotalCount);

        // Doğrudan kimlikle de kapsam dışı ilişki bulunamaz.
        var bLink = seenByB.Items.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"{Links}/{bLink.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientA.PutAsJsonAsync($"{Links}/{bLink.Id}", Save(bLink) with { JobTitle = "ele geçir" })).StatusCode);

        // Kişi, görünür firmalardan biri üzerinden görünür: A de B de ortak kişiyi görür.
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync($"/api/crm/contacts/{shared.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync($"/api/crm/contacts/{shared.Id}")).StatusCode);
    }

    [Fact]
    public async Task Relationship_changes_are_audited_and_stale_versions_are_rejected()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Denetim A.Ş.");
        var person = await CreateContactAsync(client, "Denetlenen", "Kişi");
        var link = await CreateLinkAsync(client, new SaveCompanyContactRequest(company.Id, person.Id, JobTitle: "Eski unvan"));

        var update = await client.PutAsJsonAsync($"{Links}/{link.Id}", Save(link) with { JobTitle = "Yeni unvan" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var stale = await client.PutAsJsonAsync($"{Links}/{link.Id}", Save(link) with { JobTitle = "Bayat" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PutAsJsonAsync($"{Links}/{link.Id}", Save(link) with { Version = null })).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.Where(a => a.RecordId == link.Id).OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal([AuditAction.Created, AuditAction.Updated], audit.Select(a => a.Action));
        Assert.Contains("Yeni unvan", audit[1].Changes);
        Assert.Equal(2, await db.OutboxMessages.CountAsync(e => e.RecordId == link.Id));
    }

    private static SaveCompanyContactRequest Save(CompanyContactDto link) => new(
        link.CompanyId, link.ContactId, link.Department, link.JobTitle, link.Roles, link.IsPrimary, link.IsDecisionMaker,
        link.StartDate, link.EndDate, link.Version);

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> SetUpAsync()
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

    private static async Task<ContactDto> CreateContactAsync(HttpClient client, string first, string last, Guid? companyId = null, string? title = null)
    {
        var response = await client.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest(first, last, companyId, title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ContactDto>())!;
    }

    private static async Task<CompanyContactDto> CreateLinkAsync(HttpClient client, SaveCompanyContactRequest request)
    {
        var response = await client.PostAsJsonAsync(Links, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CompanyContactDto>())!;
    }
}
