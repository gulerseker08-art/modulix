using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Oluşturulma anı süzgeçleri (<c>createdSince</c> dahil, <c>createdBefore</c> hariç): ana sayfadaki dönem karşılaştırması bu
/// süzgeçlerle sayar. Aralık, geçersiz değer reddi, tenant izolasyonu ve yetki; süzgeç firma, kişi, fırsat ve aktivite
/// listelerinde tanımlıdır.
/// </summary>
public class CreatedRangeFilterTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/companies";

    [Fact]
    public async Task Records_are_counted_only_inside_the_given_creation_range()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, "Aralık A.Ş.");
        await CreateAsync(client, "Aralık Ltd.");
        var (past, future) = (Instant(-1), Instant(1));

        Assert.Equal(2, await CountAsync(client, $"createdSince={past}"));
        Assert.Equal(0, await CountAsync(client, $"createdSince={future}"));
        Assert.Equal(2, await CountAsync(client, $"createdBefore={future}"));
        Assert.Equal(0, await CountAsync(client, $"createdBefore={past}"));
        Assert.Equal(2, await CountAsync(client, $"createdSince={past}&createdBefore={future}"));
        Assert.Equal(0, await CountAsync(client, $"createdSince={Instant(-2)}&createdBefore={past}"));
    }

    [Theory]
    [InlineData("/api/crm/companies")]
    [InlineData("/api/crm/contacts")]
    [InlineData("/api/crm/opportunities")]
    [InlineData("/api/crm/activities")]
    public async Task Every_summarised_list_accepts_the_range_and_rejects_a_value_that_is_not_an_instant(string url)
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var valid = await client.GetAsync($"{url}?pageSize=1&createdSince={Instant(-1)}&createdBefore={Instant(1)}");
        var invalid = await client.GetAsync($"{url}?pageSize=1&createdSince=dun");

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task A_range_never_counts_another_tenants_records()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, "Yalnızca A Firması");
        await CreateAsync(clientA, "A'nın İkinci Firması");
        await CreateAsync(clientB, "Yalnızca B Firması");

        Assert.Equal(2, await CountAsync(clientA, $"createdSince={Instant(-1)}"));
        Assert.Equal(1, await CountAsync(clientB, $"createdSince={Instant(-1)}"));
    }

    [Fact]
    public async Task Without_read_permission_a_ranged_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, "Gizli A.Ş.");
        using var client = await LoginAsync(contactsOnly);

        var response = await client.GetAsync($"{Url}?createdSince={Instant(-1)}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Şimdiden verilen saat kadar uzaktaki an, sorgu dizesine hazır (ISO 8601, UTC).</summary>
    private static string Instant(int hours) => Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(hours).ToString("O"));

    private static async Task<int> CountAsync(HttpClient client, string filters)
    {
        var page = await client.GetFromJsonAsync<CrmPage<CompanyDto>>($"{Url}?pageSize=1&{filters}");
        return page!.TotalCount;
    }

    private static async Task CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Url, new SaveCompanyRequest(name));
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
