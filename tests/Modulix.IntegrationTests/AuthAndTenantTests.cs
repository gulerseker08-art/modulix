using System.Net;
using System.Net.Http.Json;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>
/// Görev 4: giriş, üyelikler ve aktif tenant seçimi. Tenant izolasyonu kuralları:
/// aktif tenant sunucuda doğrulanır, istemci yalnızca üyesi olduğu tenant'ı seçebilir,
/// üyelik kalkınca erişim bir sonraki istekte kalkar.
/// </summary>
public class AuthAndTenantTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Anonymous_user_cannot_read_session()
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, "Yanlis.Sifre1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_answer_as_wrong_password()
    {
        using var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("yok@test.modulix", ModulixApiFactory.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Single_membership_is_selected_automatically()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();

        var me = await LoginAsync(client, user.Email!);

        Assert.Equal(tenant.Id, me.ActiveTenantId);
        var current = await client.GetFromJsonAsync<CurrentTenantResponse>("/api/tenant/current");
        Assert.Equal(tenant.Id, current!.TenantId);
    }

    [Fact]
    public async Task User_sees_only_own_memberships()
    {
        var own = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        await factory.CreateUserAsync(other);
        var user = await factory.CreateUserAsync(own);
        using var client = factory.CreateBrowserClient();

        var me = await LoginAsync(client, user.Email!);

        var membership = Assert.Single(me.Memberships);
        Assert.Equal(own.Id, membership.TenantId);
    }

    [Fact]
    public async Task User_cannot_select_tenant_without_membership()
    {
        var own = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(own);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var response = await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(other.Id));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var current = await client.GetFromJsonAsync<CurrentTenantResponse>("/api/tenant/current");
        Assert.Equal(own.Id, current!.TenantId);
    }

    [Fact]
    public async Task User_with_several_memberships_must_choose_and_can_switch()
    {
        var first = await factory.CreateTenantAsync();
        var second = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(first, second);
        using var client = factory.CreateBrowserClient();

        var me = await LoginAsync(client, user.Email!);
        Assert.Null(me.ActiveTenantId);
        Assert.Equal(2, me.Memberships.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/tenant/current")).StatusCode);

        await SelectAsync(client, first.Id);
        Assert.Equal(first.Id, (await client.GetFromJsonAsync<CurrentTenantResponse>("/api/tenant/current"))!.TenantId);

        await SelectAsync(client, second.Id);
        Assert.Equal(second.Id, (await client.GetFromJsonAsync<CurrentTenantResponse>("/api/tenant/current"))!.TenantId);
    }

    [Fact]
    public async Task Disabled_membership_loses_access_on_next_request()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenant/current")).StatusCode);

        await factory.DisableMembershipAsync(user.Id, tenant.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/tenant/current")).StatusCode);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.Null(me!.ActiveTenantId);
        Assert.Empty(me.Memberships);
    }

    [Fact]
    public async Task Closed_tenant_loses_access_on_next_request()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        await factory.CloseTenantAsync(tenant.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/tenant/current")).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var logout = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_cookie_is_http_only_secure_and_same_site_strict()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("modulix.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<MeResponse> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }

    private static async Task SelectAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenantId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
