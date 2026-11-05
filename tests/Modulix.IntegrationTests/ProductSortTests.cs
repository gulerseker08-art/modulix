using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ürün/hizmet listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): artan/azalan, boş değerler sonda, kategori adına göre sıralama,
/// geçersiz anahtar reddi, tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class ProductSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/products";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_values_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var unit = await CreateUnitAsync(client);
        var hardware = await CreateCategoryAsync(client, "Donanım");
        var software = await CreateCategoryAsync(client, "Yazılım");
        await CreateAsync(client, new SaveProductRequest("C-1", "Cıvata", unit, Brand: "Zeta", CategoryId: software));
        await CreateAsync(client, new SaveProductRequest("A-1", "Anahtar", unit, Brand: "Beta", CategoryId: hardware));
        await CreateAsync(client, new SaveProductRequest("B-1", "Boru", unit));

        Assert.Equal(["A-1", "B-1", "C-1"], await CodesAsync(client, null));
        Assert.Equal(["C-1", "B-1", "A-1"], await CodesAsync(client, "-code"));
        Assert.Equal(["A-1", "B-1", "C-1"], await CodesAsync(client, "name"));
        Assert.Equal(["C-1", "B-1", "A-1"], await CodesAsync(client, "-name"));

        // Boş marka/kategori her iki yönde de sonda.
        Assert.Equal(["A-1", "C-1", "B-1"], await CodesAsync(client, "brand"));
        Assert.Equal(["C-1", "A-1", "B-1"], await CodesAsync(client, "-brand"));
        Assert.Equal(["A-1", "C-1", "B-1"], await CodesAsync(client, "category"));
        Assert.Equal(["C-1", "A-1", "B-1"], await CodesAsync(client, "-category"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_sorting_works_together_with_other_filters()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var unit = await CreateUnitAsync(client);
        await CreateAsync(client, new SaveProductRequest("P-1", "Birinci", unit));
        await CreateAsync(client, new SaveProductRequest("P-2", "İkinci", unit, Kind: "Service"));
        await CreateAsync(client, new SaveProductRequest("P-3", "Üçüncü", unit, Kind: "Service"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=cost")).StatusCode);

        Assert.Equal(["P-3", "P-2"], await CodesAsync(client, "-code", "&kind=Service"));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_products()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveProductRequest("A-ONLY", "Yalnızca A", await CreateUnitAsync(clientA)));
        await CreateAsync(clientB, new SaveProductRequest("B-ONLY", "Yalnızca B", await CreateUnitAsync(clientB)));

        Assert.Equal(["A-ONLY"], await CodesAsync(clientA, "name"));
        Assert.Equal(["B-ONLY"], await CodesAsync(clientB, "-name"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SaveProductRequest("GIZLI", "Gizli ürün", await CreateUnitAsync(adminClient)));
        using var client = await LoginAsync(contactsOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=name")).StatusCode);
    }

    private static async Task<List<string>> CodesAsync(HttpClient client, string? sort, string extra = "")
    {
        var query = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<CrmPage<ProductDto>>($"{Url}?pageSize=50{query}{extra}");
        return [.. page!.Items.Select(p => p.Code)];
    }

    private static async Task CreateAsync(HttpClient client, SaveProductRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateUnitAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MeasureUnitDto>())!.Id;
    }

    private static async Task<Guid> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/product-categories", new SaveProductCategoryRequest(name));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductCategoryDto>())!.Id;
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
