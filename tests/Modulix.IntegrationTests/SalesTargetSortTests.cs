using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Satış hedefi listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, geçersiz anahtar reddi, süzgeçle birlikte
/// çalışma, tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class SalesTargetSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/sales-targets";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, Target("WonAmount", 2026, 300_000m));
        await CreateAsync(client, Target("WonCount", 2027, 4m));
        await CreateAsync(client, Target("WonAmount", 2025, 900_000m));

        Assert.Equal([2025, 2026, 2027], await YearsAsync(client, "periodStart"));
        Assert.Equal([2027, 2026, 2025], await YearsAsync(client, "-periodStart"));
        Assert.Equal([2027, 2026, 2025], await YearsAsync(client, "value"));
        Assert.Equal([2026, 2025, 2027], await YearsAsync(client, "metric"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_filters_still_work_together_with_sorting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, Target("WonAmount", 2026, 300_000m));
        await CreateAsync(client, Target("WonCount", 2027, 4m));
        await CreateAsync(client, Target("WonAmount", 2025, 900_000m));

        var invalid = await client.GetAsync($"{Url}?sort=password");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        Assert.Equal([2025, 2026], await YearsAsync(client, "-value", "&metric=WonAmount"));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_targets()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, Target("WonAmount", 2026, 100m));
        await CreateAsync(clientB, Target("WonAmount", 2030, 200m));

        Assert.Equal([2026], await YearsAsync(clientA, "periodStart"));
        Assert.Equal([2030], await YearsAsync(clientB, "-periodStart"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, _) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var client = await LoginAsync(contactsOnly);

        var response = await client.GetAsync($"{Url}?sort=periodStart");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static SaveSalesTargetRequest Target(string metric, int year, decimal value) =>
        new(metric, "Tenant", null, metric == "WonAmount" ? "TRY" : null, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), value);

    private static async Task<List<int>> YearsAsync(HttpClient client, string sort, string extra = "")
    {
        var page = await client.GetFromJsonAsync<CrmPage<SalesTargetDto>>($"{Url}?sort={Uri.EscapeDataString(sort)}{extra}");
        return [.. page!.Items.Select(t => t.PeriodStart.Year)];
    }

    private static async Task CreateAsync(HttpClient client, SaveSalesTargetRequest request)
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
