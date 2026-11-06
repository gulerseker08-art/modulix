using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Kullanıcı listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): e-posta artan/azalan, geçersiz anahtar reddi, tenant izolasyonu
/// ve yetki (kullanıcı yönetimi izni olmadan sıralı liste açılmaz).
/// </summary>
public class UserSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/users";

    [Fact]
    public async Task Users_sort_by_email_in_both_directions_and_by_name_by_default()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        var second = await factory.CreateUserAsync(tenant);
        var third = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(admin);
        var expected = new[] { admin.Email!, second.Email!, third.Email! }.Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(expected, await EmailsAsync(client, "email"));
        Assert.Equal(Enumerable.Reverse(expected), await EmailsAsync(client, "-email"));

        // Varsayılan (ad) ve "-name": üç kullanıcının adı aynı olduğundan e-postaya göre eşitlenir; liste yine tam ve tekrarsızdır.
        Assert.Equal(3, (await EmailsAsync(client, null)).Distinct().Count());
        Assert.Equal(3, (await EmailsAsync(client, "-name")).Distinct().Count());
        Assert.Equal(3, (await EmailsAsync(client, "-lastSignInAt")).Distinct().Count());
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=passwordHash")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=roles")).StatusCode);
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_users()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var adminA = await factory.CreateUserAsync(a);
        var adminB = await factory.CreateUserAsync(b);
        await factory.MakeAdministratorAsync(a.Id, adminA);
        await factory.MakeAdministratorAsync(b.Id, adminB);
        using var client = await LoginAsync(adminA);

        Assert.Equal([adminA.Email!], await EmailsAsync(client, "-email"));
    }

    [Fact]
    public async Task Without_the_user_management_permission_a_sorted_list_is_forbidden()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageCompanies], Scope.Own, member);
        using var client = await LoginAsync(member);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=email")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Url}?sort=email")).StatusCode);
    }

    private static async Task<List<string>> EmailsAsync(HttpClient client, string? sort)
    {
        var url = sort is null ? $"{Url}?pageSize=50" : $"{Url}?pageSize=50&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<UserPageResponse>(url);
        return [.. page!.Items.Select(u => u.Email)];
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
