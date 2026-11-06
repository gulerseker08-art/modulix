using System.Net;
using System.Net.Http.Json;
using Modulix.Core.Contracts.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Marketing;
using Modulix.Shared.Records;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Segment ve kampanya listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): artan/azalan, boş değerler sonda, geçersiz anahtar reddi,
/// tenant izolasyonu ve yetki (okuma izni olmadan sıralı liste açılmaz).
/// </summary>
public class MarketingSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Segments = "/api/marketing/segments";
    private const string Campaigns = "/api/marketing/campaigns";

    [Fact]
    public async Task Segments_sort_by_a_column_in_both_directions()
    {
        var (client, _) = await ManagerAsync();
        await CreateSegmentAsync(client, "bravo", "Bravo", "Static");
        await CreateSegmentAsync(client, "alfa", "Alfa", "Dynamic");
        await CreateSegmentAsync(client, "charlie", "Charlie", "Static");

        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<SegmentDto>(client, Segments, "name", s => s.Name));
        Assert.Equal(["Charlie", "Bravo", "Alfa"], await NamesAsync<SegmentDto>(client, Segments, "-name", s => s.Name));

        // Tür: Dynamic < Static; eşitler ada göre.
        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<SegmentDto>(client, Segments, "kind", s => s.Name));
        Assert.Equal(["Bravo", "Charlie", "Alfa"], await NamesAsync<SegmentDto>(client, Segments, "-kind", s => s.Name));

        // Üye sayısı henüz hesaplanmadıysa boştur; boşlar iki yönde de sonda ve ada göre sıralı kalır.
        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<SegmentDto>(client, Segments, "lastMemberCount", s => s.Name));
        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<SegmentDto>(client, Segments, "-lastMemberCount", s => s.Name));
    }

    [Fact]
    public async Task Campaigns_sort_by_a_column_in_both_directions_with_empty_dates_last()
    {
        var (client, _) = await ManagerAsync();
        await CreateCampaignAsync(client, "bravo", "Bravo", new DateOnly(2026, 9, 1));
        await CreateCampaignAsync(client, "alfa", "Alfa", new DateOnly(2026, 3, 1));
        await CreateCampaignAsync(client, "charlie", "Charlie", null);

        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<CampaignDto>(client, Campaigns, "name", c => c.Name));
        Assert.Equal(["Charlie", "Bravo", "Alfa"], await NamesAsync<CampaignDto>(client, Campaigns, "-name", c => c.Name));
        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync<CampaignDto>(client, Campaigns, "startsOn", c => c.Name));
        Assert.Equal(["Bravo", "Alfa", "Charlie"], await NamesAsync<CampaignDto>(client, Campaigns, "-startsOn", c => c.Name));

        // Varsayılan sıra en yeni önce; açık "-createdAt" aynısıdır.
        Assert.Equal(["Charlie", "Alfa", "Bravo"], await NamesAsync<CampaignDto>(client, Campaigns, "-createdAt", c => c.Name));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected_for_segments_and_campaigns()
    {
        var (client, _) = await ManagerAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Segments}?sort=password")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Segments}?sort=ownerId")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Campaigns}?sort=recipientRule")).StatusCode);
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_rows()
    {
        var (clientA, _) = await ManagerAsync();
        var (clientB, _) = await ManagerAsync();
        await CreateSegmentAsync(clientA, "yalniz-a", "Yalnızca A", "Dynamic");
        await CreateCampaignAsync(clientB, "yalniz-b", "Yalnızca B", null);

        Assert.Equal(["Yalnızca A"], await NamesAsync<SegmentDto>(clientA, Segments, "-name", s => s.Name));
        Assert.Empty(await NamesAsync<SegmentDto>(clientB, Segments, "name", s => s.Name));
        Assert.Equal(["Yalnızca B"], await NamesAsync<CampaignDto>(clientB, Campaigns, "name", c => c.Name));
        Assert.Empty(await NamesAsync<CampaignDto>(clientA, Campaigns, "-name", c => c.Name));
    }

    [Fact]
    public async Task Without_read_permission_a_sorted_list_is_forbidden()
    {
        var tenant = await TenantAsync();
        var nobody = await UserClientAsync(tenant, [CampaignsWrite]);

        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"{Segments}?sort=name")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"{Campaigns}?sort=name")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Segments}?sort=name")).StatusCode);
    }

    private static async Task<List<string>> NamesAsync<T>(HttpClient client, string url, string sort, Func<T, string> name)
    {
        var page = await client.GetFromJsonAsync<PagedResult<T>>($"{url}?pageSize=50&sort={Uri.EscapeDataString(sort)}");
        return [.. page!.Items.Select(name)];
    }

    private static async Task CreateSegmentAsync(HttpClient client, string code, string name, string kind)
    {
        var response = await client.PostAsJsonAsync(Segments, new SaveSegmentRequest(Code: code, Name: name, Target: "Company", Kind: kind));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task CreateCampaignAsync(HttpClient client, string code, string name, DateOnly? startsOn)
    {
        var response = await client.PostAsJsonAsync(
            Campaigns, new SaveCampaignRequest(Code: code, Name: name, Target: "Company", Channel: "Email", RecipientRule: "PrimaryContact", StartsOn: startsOn));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // createdAt sırası kararlı olsun: kayıtlar arasında en az bir saat adımı.
        await Task.Delay(20);
    }

    private async Task<Modulix.Core.Platform.Tenant> TenantAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, Modulix.Modules.Crm.CrmModule.ModuleKey, enabled: true);
        await factory.SetModuleAsync(tenant.Id, Modulix.Modules.Marketing.MarketingModule.ModuleKey, enabled: true);
        return tenant;
    }

    private async Task<(HttpClient Client, Guid TenantId)> ManagerAsync()
    {
        var tenant = await TenantAsync();
        return (await UserClientAsync(tenant, [SegmentsRead, SegmentsWrite, CampaignsRead, CampaignsWrite]), tenant.Id);
    }

    private async Task<HttpClient> UserClientAsync(Modulix.Core.Platform.Tenant tenant, string[] permissions)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, RecordScope.All, user);
        return await LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
