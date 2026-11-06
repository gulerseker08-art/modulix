using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Müşteri adayı listesi başlığa göre sıralama (<c>sort</c> anahtarı): artan/azalan, boş değerler sonda, varsayılan en yeni önce, geçersiz
/// anahtar reddi (sorumlu ve bütçe gibi sıralanamaz alanlar dahil), tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class LeadSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/crm/leads";

    [Fact]
    public async Task Sorting_by_a_column_orders_the_list_in_both_directions_with_empty_values_last()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveLeadRequest("Bravo konu", LastName: "A", City: "Mersin"));
        await CreateAsync(client, new SaveLeadRequest("Alfa konu", LastName: "B", City: "Bursa"));
        await CreateAsync(client, new SaveLeadRequest("Charlie konu", LastName: "C"));

        Assert.Equal(["Alfa konu", "Bravo konu", "Charlie konu"], await SubjectsAsync(client, "subject"));
        Assert.Equal(["Charlie konu", "Bravo konu", "Alfa konu"], await SubjectsAsync(client, "-subject"));
        Assert.Equal(["Alfa konu", "Bravo konu", "Charlie konu"], await SubjectsAsync(client, "city"));
        Assert.Equal(["Bravo konu", "Alfa konu", "Charlie konu"], await SubjectsAsync(client, "-city"));

        // Varsayılan sıra en yeni gelen önce; "received" artan en eski önce getirir.
        Assert.Equal(["Charlie konu", "Alfa konu", "Bravo konu"], await SubjectsAsync(client, null));
        Assert.Equal(["Bravo konu", "Alfa konu", "Charlie konu"], await SubjectsAsync(client, "received"));
        Assert.Equal(["Charlie konu", "Alfa konu", "Bravo konu"], await SubjectsAsync(client, "-received"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_and_other_filters_still_work_together_with_sorting()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        await CreateAsync(client, new SaveLeadRequest("Delta konu", LastName: "D", Priority: "High"));
        await CreateAsync(client, new SaveLeadRequest("Echo konu", LastName: "E", Priority: "High"));
        await CreateAsync(client, new SaveLeadRequest("Foxtrot konu", LastName: "F"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=owner")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=estimatedBudget")).StatusCode);

        Assert.Equal(["Echo konu", "Delta konu"], await SubjectsAsync(client, "-subject", "&priority=High"));
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_leads()
    {
        var (_, adminA) = await SetUpAsync();
        var (_, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        await CreateAsync(clientA, new SaveLeadRequest("Yalnizca A adayi", LastName: "A"));
        await CreateAsync(clientB, new SaveLeadRequest("Yalnizca B adayi", LastName: "B"));

        Assert.Equal(["Yalnizca A adayi"], await SubjectsAsync(clientA, "subject"));
        Assert.Equal(["Yalnizca B adayi"], await SubjectsAsync(clientB, "-subject"));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var (tenant, admin) = await SetUpAsync();
        var companiesOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.Own, companiesOnly);
        using var adminClient = await LoginAsync(admin);
        await CreateAsync(adminClient, new SaveLeadRequest("Gizli aday", LastName: "G"));
        using var client = await LoginAsync(companiesOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=subject")).StatusCode);
    }

    private static async Task<List<string>> SubjectsAsync(HttpClient client, string? sort, string extra = "")
    {
        var sortPart = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<CrmPage<LeadDto>>($"{Url}?pageSize=50{sortPart}{extra}");
        return [.. page!.Items.Select(l => l.Subject)];
    }

    private static async Task CreateAsync(HttpClient client, SaveLeadRequest request)
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
