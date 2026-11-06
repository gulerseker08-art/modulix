using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Firma listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, boş değerler sonda, geçersiz anahtar reddi,
/// tenant izolasyonu (sıralı listede başka tenant'ın firması görünmez) ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class CompanySortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/companies";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_values_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveCompanyRequest("Bravo A.Ş.", City: "Mersin"));
        await CreateAsync(client, new SaveCompanyRequest("Alfa Ltd.", City: "Bursa"));
        await CreateAsync(client, new SaveCompanyRequest("Charlie San."));

        Assert.Equal(["Alfa Ltd.", "Bravo A.Ş.", "Charlie San."], await NamesAsync(client, "name"));
        Assert.Equal(["Charlie San.", "Bravo A.Ş.", "Alfa Ltd."], await NamesAsync(client, "-name"));
        Assert.Equal(["Alfa Ltd.", "Bravo A.Ş.", "Charlie San."], await NamesAsync(client, "city"));
        Assert.Equal(["Bravo A.Ş.", "Alfa Ltd.", "Charlie San."], await NamesAsync(client, "-city"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_other_filters_still_work_together_with_sorting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveCompanyRequest("Delta A.Ş.", City: "İzmir"));
        await CreateAsync(client, new SaveCompanyRequest("Echo Ltd.", City: "İzmir"));
        await CreateAsync(client, new SaveCompanyRequest("Foxtrot Ltd.", City: "Ankara"));

        var invalid = await client.GetAsync($"{Url}?sort=password");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        Assert.Equal(["Echo Ltd.", "Delta A.Ş."], await NamesAsync(client, "-name", "&city=" + Uri.EscapeDataString("İzmir")));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_companies()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveCompanyRequest("Yalnızca A Firması"));
        await CreateAsync(clientB, new SaveCompanyRequest("Yalnızca B Firması"));

        Assert.Equal(["Yalnızca A Firması"], await NamesAsync(clientA, "name"));
        Assert.Equal(["Yalnızca B Firması"], await NamesAsync(clientB, "-name"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SaveCompanyRequest("Gizli A.Ş."));
        using var client = await LoginAsync(contactsOnly);

        var response = await client.GetAsync($"{Url}?sort=name");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<List<string>> NamesAsync(HttpClient client, string sort, string extra = "")
    {
        var page = await client.GetFromJsonAsync<CrmPage<CompanyDto>>($"{Url}?pageSize=50&sort={Uri.EscapeDataString(sort)}{extra}");
        return [.. page!.Items.Select(c => c.Name)];
    }

    private static async Task CreateAsync(HttpClient client, SaveCompanyRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
