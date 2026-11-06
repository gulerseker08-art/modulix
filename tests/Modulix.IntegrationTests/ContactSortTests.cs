using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Kişi listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, boş değerler sonda, geçersiz anahtar reddi,
/// tenant izolasyonu (sıralı listede başka tenant'ın kişisi görünmez) ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class ContactSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/contacts";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_values_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveContactRequest("Ali", "Yilmaz", Title: "Mudur"));
        await CreateAsync(client, new SaveContactRequest("Veli", "Kaya"));
        await CreateAsync(client, new SaveContactRequest("Can", "Demir", Title: "Asistan"));

        Assert.Equal(["Demir", "Kaya", "Yilmaz"], await LastNamesAsync(client, "name"));
        Assert.Equal(["Yilmaz", "Kaya", "Demir"], await LastNamesAsync(client, "-name"));
        Assert.Equal(["Demir", "Yilmaz", "Kaya"], await LastNamesAsync(client, "title"));
        Assert.Equal(["Yilmaz", "Demir", "Kaya"], await LastNamesAsync(client, "-title"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_other_filters_still_work_together_with_sorting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Kisi Firmasi A.S.");
        await CreateAsync(client, new SaveContactRequest("Ece", "Aksoy", CompanyId: company.Id));
        await CreateAsync(client, new SaveContactRequest("Efe", "Bulut", CompanyId: company.Id));
        await CreateAsync(client, new SaveContactRequest("Eda", "Celik"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=company")).StatusCode);

        Assert.Equal(["Bulut", "Aksoy"], await LastNamesAsync(client, "-name", "&companyId=" + company.Id));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_contacts()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveContactRequest("Ayse", "SadeceA"));
        await CreateAsync(clientB, new SaveContactRequest("Berk", "SadeceB"));

        Assert.Equal(["SadeceA"], await LastNamesAsync(clientA, "name"));
        Assert.Equal(["SadeceB"], await LastNamesAsync(clientB, "-name"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var companiesOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own, companiesOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SaveContactRequest("Gizli", "Kisi"));
        using var client = await LoginAsync(companiesOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=name")).StatusCode);
    }

    private static async Task<List<string>> LastNamesAsync(HttpClient client, string sort, string extra = "")
    {
        var page = await client.GetFromJsonAsync<CrmPage<ContactDto>>($"{Url}?pageSize=50&sort={Uri.EscapeDataString(sort)}{extra}");
        return [.. page!.Items.Select(c => c.LastName)];
    }

    private static async Task CreateAsync(HttpClient client, SaveContactRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

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
}
