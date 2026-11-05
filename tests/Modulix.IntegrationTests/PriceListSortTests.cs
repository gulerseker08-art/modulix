using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Fiyat listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): artan/azalan, boş geçerlilik sonda, varsayılan sıra durum + ad,
/// geçersiz anahtar reddi, tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class PriceListSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/price-lists";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_dates_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var now = DateTimeOffset.UtcNow;
        await CreateAsync(client, new SavePriceListRequest("Bravo listesi", "USD", ValidFrom: now.AddDays(20)));
        await CreateAsync(client, new SavePriceListRequest("Alfa listesi", "TRY", ValidFrom: now.AddDays(10)));
        await CreateAsync(client, new SavePriceListRequest("Charlie listesi", "EUR"));

        Assert.Equal(["Alfa listesi", "Bravo listesi", "Charlie listesi"], await NamesAsync(client, null));
        Assert.Equal(["Charlie listesi", "Bravo listesi", "Alfa listesi"], await NamesAsync(client, "-name"));
        Assert.Equal(["Alfa listesi", "Bravo listesi", "Charlie listesi"], await NamesAsync(client, "validFrom"));
        Assert.Equal(["Bravo listesi", "Alfa listesi", "Charlie listesi"], await NamesAsync(client, "-validFrom"));
        Assert.Equal(["Charlie listesi", "Alfa listesi", "Bravo listesi"], await NamesAsync(client, "currency"));
        Assert.Equal(["Bravo listesi", "Alfa listesi", "Charlie listesi"], await NamesAsync(client, "-currency"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_sorting_works_together_with_the_status_filter()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SavePriceListRequest("Birinci"));
        await CreateAsync(client, new SavePriceListRequest("İkinci"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=tierMethod")).StatusCode);

        Assert.Equal(["İkinci", "Birinci"], await NamesAsync(client, "-name", "&status=Draft"));
        Assert.Empty(await NamesAsync(client, "name", "&status=Archived"));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_price_lists()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SavePriceListRequest("Yalnızca A listesi"));
        await CreateAsync(clientB, new SavePriceListRequest("Yalnızca B listesi"));

        Assert.Equal(["Yalnızca A listesi"], await NamesAsync(clientA, "name"));
        Assert.Equal(["Yalnızca B listesi"], await NamesAsync(clientB, "-name"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SavePriceListRequest("Gizli liste"));
        using var client = await LoginAsync(contactsOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=name")).StatusCode);
    }

    private static async Task<List<string>> NamesAsync(HttpClient client, string? sort, string extra = "")
    {
        var query = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<CrmPage<PriceListDto>>($"{Url}?pageSize=50{query}{extra}");
        return [.. page!.Items.Select(l => l.Name)];
    }

    private static async Task CreateAsync(HttpClient client, SavePriceListRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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
