using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Teklif listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): artan/azalan, boş geçerlilik sonda, geçersiz anahtar reddi,
/// tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class QuoteSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/quotes";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_validity_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Müşteri A.Ş.");
        var now = DateTimeOffset.UtcNow;
        await CreateAsync(client, new SaveQuoteRequest("Bravo teklifi", company, ValidUntil: now.AddDays(20)));
        await CreateAsync(client, new SaveQuoteRequest("Alfa teklifi", company, ValidUntil: now.AddDays(10)));
        await CreateAsync(client, new SaveQuoteRequest("Charlie teklifi", company));

        Assert.Equal(["Alfa teklifi", "Bravo teklifi", "Charlie teklifi"], await TitlesAsync(client, "title"));
        Assert.Equal(["Charlie teklifi", "Bravo teklifi", "Alfa teklifi"], await TitlesAsync(client, "-title"));
        Assert.Equal(["Alfa teklifi", "Bravo teklifi", "Charlie teklifi"], await TitlesAsync(client, "validUntil"));
        Assert.Equal(["Bravo teklifi", "Alfa teklifi", "Charlie teklifi"], await TitlesAsync(client, "-validUntil"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_the_default_order_is_newest_first()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Müşteri B A.Ş.");
        await CreateAsync(client, new SaveQuoteRequest("İlk teklif", company));
        await CreateAsync(client, new SaveQuoteRequest("İkinci teklif", company));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=internalNotes")).StatusCode);

        // Varsayılan sıra ve açık "-createdAt" aynıdır: en yeni teklif önce.
        Assert.Equal(["İkinci teklif", "İlk teklif"], await TitlesAsync(client, null));
        Assert.Equal(["İkinci teklif", "İlk teklif"], await TitlesAsync(client, "-createdAt"));
        Assert.Equal(["İlk teklif", "İkinci teklif"], await TitlesAsync(client, "createdAt"));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_quotes()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveQuoteRequest("Yalnızca A teklifi", await CreateCompanyAsync(clientA, "A Müşterisi")));
        await CreateAsync(clientB, new SaveQuoteRequest("Yalnızca B teklifi", await CreateCompanyAsync(clientB, "B Müşterisi")));

        Assert.Equal(["Yalnızca A teklifi"], await TitlesAsync(clientA, "title"));
        Assert.Equal(["Yalnızca B teklifi"], await TitlesAsync(clientB, "-title"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var contactsOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ContactsRead], Scope.Own, contactsOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SaveQuoteRequest("Gizli teklif", await CreateCompanyAsync(adminClient, "Gizli Müşteri")));
        using var client = await LoginAsync(contactsOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=title")).StatusCode);
    }

    private static async Task<List<string>> TitlesAsync(HttpClient client, string? sort)
    {
        var query = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<CrmPage<QuoteDto>>($"{Url}?pageSize=50{query}");
        return [.. page!.Items.Select(q => q.Title)];
    }

    private static async Task CreateAsync(HttpClient client, SaveQuoteRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!.Id;
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
