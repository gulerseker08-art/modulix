using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Views;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0036: kişisel liste görünümleri. Üye kendi görünümünü oluşturur, günceller, siler; aynı tenant'taki başka üye ve başka tenant görmez ve
/// değiştiremez; geçersiz sütun, sıralama, süzgeç ve yinelenen ad reddedilir; eski sürüm çakışmadır; oturumsuz istek reddedilir.
/// </summary>
public class GridViewTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/grid-views";
    private const string Screen = "crm.companies";

    [Fact]
    public async Task A_member_creates_lists_updates_and_deletes_a_personal_view()
    {
        var (_, user) = await SetUpAsync();
        using var client = await LoginAsync(user);

        var created = await CreateAsync(client, new SaveGridViewRequest(Screen, "Lojistik takibi", ["name", "city", "phone"], "-city", "status=Prospect"));
        Assert.Equal(["name", "city", "phone"], created.Columns);
        Assert.Equal(("-city", "status=Prospect"), (created.Sort, created.Filters));

        var list = (await client.GetFromJsonAsync<List<GridViewDto>>($"{Url}?screen={Screen}"))!;
        Assert.Equal("Lojistik takibi", Assert.Single(list).Name);

        var updated = await client.PutAsJsonAsync($"{Url}/{created.Id}",
            new SaveGridViewRequest(Screen, "Lojistik takibi", ["name", "industry"], null, null, created.Version));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = (await updated.Content.ReadFromJsonAsync<GridViewDto>())!;
        Assert.Equal(["name", "industry"], after.Columns);
        Assert.Null(after.Sort);
        Assert.True(after.Version > created.Version);

        var deleted = await client.DeleteAsync($"{Url}/{created.Id}?version={after.Version}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<GridViewDto>>($"{Url}?screen={Screen}"))!);
    }

    [Fact]
    public async Task Invalid_views_duplicates_and_stale_versions_are_rejected()
    {
        var (_, user) = await SetUpAsync();
        using var client = await LoginAsync(user);
        var view = await CreateAsync(client, new SaveGridViewRequest(Screen, "Ortak ad", ["name"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, " ", ["name"]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Boş sütun", []))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Kötü sütun", ["na me;drop"]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Tekrar", ["name", "name"]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Kötü sıra", ["name"], "name desc"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Kötü süzgeç", ["name"], null, "pageSize=1000"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest("Kötü Ekran", "Ekran", ["name"]))).StatusCode);

        // Aynı ekranda aynı ad ikinci kez açılamaz; başka ekranda açılabilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Ortak ad", ["city"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url, new SaveGridViewRequest("crm.contacts", "Ortak ad", ["name"]))).StatusCode);

        // Sürümsüz güncelleme 428, eski sürüm 409; görünüm başka ekrana taşınamaz.
        Assert.Equal((HttpStatusCode)428, (await client.PutAsJsonAsync($"{Url}/{view.Id}", new SaveGridViewRequest(Screen, "Ortak ad", ["city"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Url}/{view.Id}", new SaveGridViewRequest(Screen, "Ortak ad", ["city"], null, null, view.Version + 5))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{Url}/{view.Id}", new SaveGridViewRequest("crm.contacts", "Ortak ad", ["city"], null, null, view.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Url}/{view.Id}?version={view.Version + 5}")).StatusCode);
    }

    [Fact]
    public async Task Views_are_private_to_their_owner_and_never_cross_tenants()
    {
        var (tenantA, owner) = await SetUpAsync();
        var colleague = await factory.CreateUserAsync(tenantA);
        var (_, outsider) = await SetUpAsync();
        using var ownerClient = await LoginAsync(owner);
        using var colleagueClient = await LoginAsync(colleague);
        using var outsiderClient = await LoginAsync(outsider);
        var view = await CreateAsync(ownerClient, new SaveGridViewRequest(Screen, "Gizli görünüm", ["name"]));

        // Aynı tenant'taki başka üye ve başka tenant'ın kullanıcısı görmez, değiştirmez, silmez (bulunamadı).
        foreach (var other in new[] { colleagueClient, outsiderClient })
        {
            Assert.Empty((await other.GetFromJsonAsync<List<GridViewDto>>($"{Url}?screen={Screen}"))!);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Url}/{view.Id}", new SaveGridViewRequest(Screen, "Ele geçirildi", ["city"], null, null, view.Version))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Url}/{view.Id}?version={view.Version}")).StatusCode);
        }

        // Aynı ad başka üyede ayrı bir görünümdür.
        await CreateAsync(colleagueClient, new SaveGridViewRequest(Screen, "Gizli görünüm", ["city"]));
        Assert.Equal(["name"], Assert.Single((await ownerClient.GetFromJsonAsync<List<GridViewDto>>($"{Url}?screen={Screen}"))!).Columns);
    }

    [Fact]
    public async Task Without_a_session_the_endpoints_are_closed()
    {
        using var anonymous = factory.CreateBrowserClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Url}?screen={Screen}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Url, new SaveGridViewRequest(Screen, "Anonim", ["name"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Url}/{Guid.NewGuid()}?version=1")).StatusCode);
    }

    private static async Task<GridViewDto> CreateAsync(HttpClient client, SaveGridViewRequest request)
    {
        var response = await client.PostAsJsonAsync(Url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GridViewDto>())!;
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser User)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        return (tenant, user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
