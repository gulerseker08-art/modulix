using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Fırsat listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, boş tarihler sonda, varsayılan en yeni önce, geçersiz anahtar reddi
/// (tutar ve olasılık hassas alandır, sıralanamaz), tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class OpportunitySortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/opportunities";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_dates_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Firsat Firmasi A.S.");
        await CreateAsync(client, new SaveOpportunityRequest("Bravo firsat", company.Id, ExpectedCloseDate: new DateOnly(2027, 3, 1)));
        await CreateAsync(client, new SaveOpportunityRequest("Alfa firsat", company.Id, ExpectedCloseDate: new DateOnly(2027, 1, 1)));
        await CreateAsync(client, new SaveOpportunityRequest("Charlie firsat", company.Id));

        Assert.Equal(["Alfa firsat", "Bravo firsat", "Charlie firsat"], await TitlesAsync(client, "title"));
        Assert.Equal(["Charlie firsat", "Bravo firsat", "Alfa firsat"], await TitlesAsync(client, "-title"));
        Assert.Equal(["Alfa firsat", "Bravo firsat", "Charlie firsat"], await TitlesAsync(client, "closeDate"));
        Assert.Equal(["Bravo firsat", "Alfa firsat", "Charlie firsat"], await TitlesAsync(client, "-closeDate"));

        // Varsayılan sıra en yeni oluşturulan önce.
        Assert.Equal(["Charlie firsat", "Alfa firsat", "Bravo firsat"], await TitlesAsync(client, null));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_other_filters_still_work_together_with_sorting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Filtre Firmasi A.S.");
        await CreateAsync(client, new SaveOpportunityRequest("Delta firsat", company.Id));
        await CreateAsync(client, new SaveOpportunityRequest("Echo firsat", company.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=amount")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=probability")).StatusCode);

        Assert.Equal(["Echo firsat", "Delta firsat"], await TitlesAsync(client, "-title", "&companyId=" + company.Id));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_opportunities()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var companyA = await CreateCompanyAsync(clientA, "A Firmasi");
        var companyB = await CreateCompanyAsync(clientB, "B Firmasi");
        await CreateAsync(clientA, new SaveOpportunityRequest("Yalnizca A firsati", companyA.Id));
        await CreateAsync(clientB, new SaveOpportunityRequest("Yalnizca B firsati", companyB.Id));

        Assert.Equal(["Yalnizca A firsati"], await TitlesAsync(clientA, "title"));
        Assert.Equal(["Yalnizca B firsati"], await TitlesAsync(clientB, "-title"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var companiesOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own, companiesOnly);
        using var adminClient = await LoginAsync(admin);
        var company = await CreateCompanyAsync(adminClient, "Gizli Firma");
        await CreateAsync(adminClient, new SaveOpportunityRequest("Gizli firsat", company.Id));
        using var client = await LoginAsync(companiesOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=title")).StatusCode);
    }

    private static async Task<List<string>> TitlesAsync(HttpClient client, string? sort, string extra = "")
    {
        var sortPart = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<CrmPage<OpportunityDto>>($"{Url}?pageSize=50{sortPart}{extra}");
        return [.. page!.Items.Select(o => o.Title)];
    }

    private static async Task CreateAsync(HttpClient client, SaveOpportunityRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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
