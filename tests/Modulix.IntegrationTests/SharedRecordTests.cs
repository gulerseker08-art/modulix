using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm;
using Modulix.Modules.Logistics;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ortak firma ve kişi kaydı (ADR-0006): CRM firması ve kişisi ortak kaydın uzantısıdır. Ortak alanlar tek yerde durur,
/// aynı vergi numarası mevcut tarafa bağlanır, kullanılan ortak kayıt silinemez, erişim modüllerin bildirdiği izinlerle olur.
/// </summary>
public class SharedRecordTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string People = "/api/records/" + SharedEntities.Person;

    private static readonly string[] CrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
    ];

    [Fact]
    public async Task Crm_company_fields_live_on_the_shared_party()
    {
        var client = await ClientAsync(CrmPermissions);
        using var _ = client;

        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies",
            new SaveCompanyRequest("Anka Lojistik", TaxNumber: "1234567890", City: "İzmir", Status: "Customer"));
        Assert.NotNull(company.PartyId);

        var party = await client.GetFromJsonAsync<RecordResponse>($"{Parties}/{company.PartyId}");
        Assert.Equal("Anka Lojistik", party!.Values["name"]!.GetValue<string>());
        Assert.Equal("İzmir", party.Values["city"]!.GetValue<string>());
        Assert.Equal("Organization", party.Values["kind"]!.GetValue<string>());
        Assert.False(party.Values.ContainsKey("status"));

        // Ortak kayıt üzerinden yapılan değişiklik CRM firmasında görünür ve eski sürümle gelen CRM güncellemesini durdurur.
        var changed = await client.PutAsJsonAsync($"{Parties}/{company.PartyId}",
            new RecordWriteRequest(new JsonObject { ["name"] = "Anka Lojistik A.Ş.", ["taxOffice"] = "Konak" }, party.Version));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var reloaded = await client.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}");
        Assert.Equal("Anka Lojistik A.Ş.", reloaded!.Name);
        Assert.Equal("1234567890", reloaded.TaxNumber);
        Assert.True(reloaded.Version > company.Version);

        var stale = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}",
            new SaveCompanyRequest("Eski ad", TaxNumber: "1234567890", Version: company.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}",
            new SaveCompanyRequest("Anka", TaxNumber: "1234567890", City: "İzmir", Version: reloaded.Version));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("Anka", (await client.GetFromJsonAsync<RecordResponse>($"{Parties}/{company.PartyId}"))!.Values["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Same_tax_number_links_the_new_company_to_the_existing_party()
    {
        var client = await ClientAsync(CrmPermissions);
        using var _ = client;

        // Başka bir modülde açılmış gibi: yalnızca taraf var.
        var party = await PostRecordAsync(client, Parties, new JsonObject
        {
            ["name"] = "Delta Taşımacılık", ["taxNumber"] = "9876543210", ["phone"] = "0232 000 00 00",
        });

        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies",
            new SaveCompanyRequest("Delta Taşımacılık Ltd.", TaxNumber: "9876543210", City: "Manisa"));
        Assert.Equal(party.Id, company.PartyId);
        Assert.Equal("Delta Taşımacılık Ltd.", company.Name);
        Assert.Equal("Manisa", company.City);

        // Boş gönderilen telefon taraftaki değeri silmez.
        Assert.Equal("0232 000 00 00", company.Phone);

        var second = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Delta 2", TaxNumber: "9876543210"));
        Assert.Equal(["Bu vergi numarasıyla kayıtlı bir firma var."], (await ValidationProblemAsync(second)).Errors["taxNumber"]);

        var duplicate = await client.PostAsJsonAsync(Parties,
            new RecordWriteRequest(new JsonObject { ["name"] = "Kopya", ["taxNumber"] = "9876543210" }));
        Assert.Equal(["Bu vergi numarasıyla kayıtlı bir firma var."], (await ValidationProblemAsync(duplicate)).Errors["taxNumber"]);

        // Ortak kayda açıkça bağlanmak da mümkündür; bağlı kayıt sonradan değiştirilemez.
        var other = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Epsilon" });
        var linked = await client.PostAsJsonAsync($"/api/records/{CrmModule.Entities.Company}",
            new RecordWriteRequest(new JsonObject { ["name"] = "Epsilon", ["partyId"] = other.Id.ToString() }));
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var linkedCompany = (await linked.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(other.Id.ToString(), linkedCompany.Values["partyId"]!.GetValue<string>());
        var relink = await client.PutAsJsonAsync($"/api/records/{CrmModule.Entities.Company}/{linkedCompany.Id}",
            new RecordWriteRequest(new JsonObject { ["partyId"] = party.Id.ToString() }, linkedCompany.Version));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], (await ValidationProblemAsync(relink)).Errors["partyId"]);

        var taken = await client.PutAsJsonAsync($"/api/crm/companies/{linkedCompany.Id}",
            new SaveCompanyRequest("Epsilon", TaxNumber: "9876543210", Version: linkedCompany.Version));
        Assert.Equal(["Bu vergi numarasıyla kayıtlı bir firma var."], (await ValidationProblemAsync(taken)).Errors["taxNumber"]);
    }

    [Fact]
    public async Task Party_in_use_cannot_be_deleted_but_survives_its_company()
    {
        var client = await ClientAsync(CrmPermissions);
        using var _ = client;
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Zeta"));
        var contact = await PostAsync<ContactDto>(client, "/api/crm/contacts", new SaveContactRequest("Selin", "Ak", company.Id));

        var inUse = await DeleteCurrentAsync(client, $"{Parties}/{company.PartyId}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        var problem = (await inUse.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Contains("1 CRM firması", problem.Title);
        Assert.Contains("1 kişi", problem.Title);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteCurrentAsync(client, $"/api/crm/contacts/{contact.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteCurrentAsync(client, $"{People}/{contact.PersonId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteCurrentAsync(client, $"/api/crm/companies/{company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Parties}/{company.PartyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteCurrentAsync(client, $"{Parties}/{company.PartyId}")).StatusCode);
    }

    [Fact]
    public async Task Contact_person_works_at_the_party_of_its_company()
    {
        var client = await ClientAsync(CrmPermissions);
        using var _ = client;
        var first = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Eta"));
        var second = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Theta"));

        var contact = await PostAsync<ContactDto>(client, "/api/crm/contacts",
            new SaveContactRequest("Can", "Demir", first.Id, Title: "Operasyon müdürü", Email: "can@eta.test"));
        var person = await client.GetFromJsonAsync<RecordResponse>($"{People}/{contact.PersonId}");
        Assert.Equal("Can", person!.Values["firstName"]!.GetValue<string>());
        Assert.Equal("Operasyon müdürü", person.Values["title"]!.GetValue<string>());
        Assert.Equal(first.PartyId.ToString(), person.Values["partyId"]!.GetValue<string>());

        var moved = await client.PutAsJsonAsync($"/api/crm/contacts/{contact.Id}",
            new SaveContactRequest("Can", "Demir", second.Id, Version: contact.Version));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        person = await client.GetFromJsonAsync<RecordResponse>($"{People}/{contact.PersonId}");
        Assert.Equal(second.PartyId.ToString(), person!.Values["partyId"]!.GetValue<string>());
        Assert.Null(person.Values["title"]);

        var search = await client.GetFromJsonAsync<RecordPageResponse>($"{People}?partyId={second.PartyId}");
        Assert.Equal(contact.PersonId, Assert.Single(search!.Items).Id);
    }

    [Fact]
    public async Task Access_comes_from_the_permissions_modules_declare()
    {
        var reader = await ClientAsync([CrmModule.Permissions.CompaniesRead]);
        using var _r = reader;
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Parties)).StatusCode);
        var write = await reader.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject { ["name"] = "Yazamaz" }));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(People)).StatusCode);

        // Bildirilmemiş izin ortak kayda erişim vermez: Lojistik maliyet izni için firma erişimi bildirmedi (sevkiyat ve talep izinleri bildirir).
        var logistics = await ClientAsync([LogisticsModule.Permissions.CostsRead], LogisticsModule.ModuleKey);
        using var _l = logistics;
        Assert.Equal(HttpStatusCode.Forbidden, (await logistics.GetAsync(Parties)).StatusCode);
    }

    private async Task<HttpClient> ClientAsync(IReadOnlyCollection<string> permissions, string module = CrmModule.ModuleKey)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, module, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, user);
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> PostRecordAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Silme sürüm ister: kaydın güncel sürümü okunup gönderilir.</summary>
    private static async Task<HttpResponseMessage> DeleteCurrentAsync(HttpClient client, string url)
    {
        var current = (await client.GetFromJsonAsync<JsonNode>(url))!;
        return await client.DeleteAsync($"{url}?version={current["version"] ?? current["Version"]}");
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
