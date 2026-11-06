using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;

namespace Modulix.IntegrationTests;

/// <summary>
/// Platform firma listesi başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): ad ve açılış tarihi artan/azalan, geçersiz anahtar reddi
/// ve yetki (platform yöneticisi olmayan, firma kullanıcısı bile olsa sıralı liste açamaz). Platform listesi bilerek tüm firmaları gösterir;
/// tenant izolasyonu yerine "firma kullanıcısı bu uca giremez" denetlenir.
/// </summary>
public class PlatformTenantSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/platform/tenants";

    [Fact]
    public async Task Tenants_sort_by_name_and_created_date_in_both_directions()
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await factory.CreateTenantAsync($"{token} Bravo");
        await Task.Delay(20);
        await factory.CreateTenantAsync($"{token} Alfa");
        await Task.Delay(20);
        await factory.CreateTenantAsync($"{token} Charlie");
        using var client = await LoginAsync(await CreatePlatformAdministratorAsync());

        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync(client, token, null));
        Assert.Equal(["Alfa", "Bravo", "Charlie"], await NamesAsync(client, token, "name"));
        Assert.Equal(["Charlie", "Bravo", "Alfa"], await NamesAsync(client, token, "-name"));
        Assert.Equal(["Bravo", "Alfa", "Charlie"], await NamesAsync(client, token, "createdAt"));
        Assert.Equal(["Charlie", "Alfa", "Bravo"], await NamesAsync(client, token, "-createdAt"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected()
    {
        using var client = await LoginAsync(await CreatePlatformAdministratorAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=activeMemberCount")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{Url}?sort=name%3Bdrop")).StatusCode);
    }

    [Fact]
    public async Task A_tenant_user_cannot_open_the_platform_list_with_or_without_sorting()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, member);
        using var client = await LoginAsync(member);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=name")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Url}?sort=-createdAt")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Url}?sort=name")).StatusCode);
    }

    // Ada eklenen ortak önek (token) çıkarılır; yalnızca "Alfa/Bravo/Charlie" döner.
    private static async Task<List<string>> NamesAsync(HttpClient client, string token, string? sort)
    {
        var url = $"{Url}?pageSize=50&search={token}" + (sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}");
        var page = await client.GetFromJsonAsync<PlatformTenantPageResponse>(url);
        return [.. page!.Items.Select(t => t.Name[(token.Length + 1)..])];
    }

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
