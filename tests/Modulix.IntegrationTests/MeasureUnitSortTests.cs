using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ölçü birimi listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, geçersiz anahtar reddi,
/// tenant izolasyonu (sıralı listede başka tenant'ın birimi görünmez) ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class MeasureUnitSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/units";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveMeasureUnitRequest("SRT-B", "Bravo", "count", 12m));
        await CreateAsync(client, new SaveMeasureUnitRequest("SRT-A", "Charlie", "weight", 1m));
        await CreateAsync(client, new SaveMeasureUnitRequest("SRT-C", "Alfa", "count", 6m));

        Assert.Equal(["SRT-A", "SRT-B", "SRT-C"], await CodesAsync(client, "code"));
        Assert.Equal(["SRT-C", "SRT-B", "SRT-A"], await CodesAsync(client, "-code"));
        Assert.Equal(["SRT-C", "SRT-B", "SRT-A"], await CodesAsync(client, "name"));
        Assert.Equal(["SRT-B", "SRT-C", "SRT-A"], await CodesAsync(client, "-factorToBase"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var invalid = await client.GetAsync($"{Url}?sort=password");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_units()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveMeasureUnitRequest("SRT-YA", "Yalnızca A", "count"));
        await CreateAsync(clientB, new SaveMeasureUnitRequest("SRT-YB", "Yalnızca B", "count"));

        Assert.Equal(["SRT-YA"], await CodesAsync(clientA, "code"));
        Assert.Equal(["SRT-YB"], await CodesAsync(clientB, "-code"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, _) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var client = await LoginAsync(contactsOnly);

        var response = await client.GetAsync($"{Url}?sort=name");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Tenant'a hazır gelen birimler olabilir; yalnızca bu testin oluşturdukları karşılaştırılır.
    private static async Task<List<string>> CodesAsync(HttpClient client, string sort)
    {
        var page = await client.GetFromJsonAsync<CrmPage<MeasureUnitDto>>($"{Url}?pageSize=200&sort={Uri.EscapeDataString(sort)}");
        return [.. page!.Items.Select(u => u.Code).Where(c => c.StartsWith("SRT-", StringComparison.Ordinal))];
    }

    private static async Task CreateAsync(HttpClient client, SaveMeasureUnitRequest request)
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
