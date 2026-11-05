using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Account;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Settings;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0034: arayüz dili. Kullanıcı tercihi kişiye aittir; firma varsayılanı ayarla yönetilir.</summary>
public class LanguagePreferenceTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public void Client_and_server_language_lists_are_identical()
    {
        Assert.Equal(
            LanguageOptions.Languages.Select(l => (l.Value, l.Label)),
            LanguageCodes.All.Select(l => (l.Code, l.Name)));
        Assert.Equal(LanguageOptions.Default, LanguageCodes.Default);
        Assert.Equal(Modulix.Core.Modules.CoreModule.SettingKeys.Language, SettingKeys.Language);
    }

    [Fact]
    public async Task User_picks_a_language_and_it_follows_them_across_tenants_and_is_audited()
    {
        var first = await factory.CreateTenantAsync();
        var second = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(first, second);
        await factory.CreateRoleAsync(second.Id, [ReadAudit], user);
        using var client = await LoginAsync(user);

        Assert.Null((await client.GetFromJsonAsync<AccountProfileResponse>("/api/account"))!.PreferredLanguage);
        Assert.Null((await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);

        var saved = await client.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest(" AZ "));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("az", (await saved.Content.ReadFromJsonAsync<AccountProfileResponse>())!.PreferredLanguage);

        // Kişiye aittir: firma değişince de aynıdır.
        Assert.Equal("az", (await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);
        await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(second.Id));
        Assert.Equal("az", (await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);

        var entry = (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account&userId={user.Id}"))!
            .Items.First(i => i.Action == AuditOperation.Updated);
        var change = Assert.Single(entry.Changes, c => c.Field == "language");
        Assert.Equal((null, "az"), (change.Old, change.New));

        // Boş gönderim tercihi kaldırır; firmanın varsayılanı geçerli olur.
        var cleared = await client.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest(null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);
    }

    [Fact]
    public async Task Unsupported_language_is_rejected_and_nothing_is_written()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user);
        await client.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest("en"));

        foreach (var invalid in new[] { "de", "tr-TR", "english" })
        {
            var response = await client.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest(invalid));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("language", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("en", (await client.GetFromJsonAsync<AccountProfileResponse>("/api/account"))!.PreferredLanguage);
    }

    [Fact]
    public async Task One_users_language_does_not_affect_another_user_or_tenant()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(other);
        using var client = await LoginAsync(user);
        await client.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest("az"));

        using var colleagueClient = await LoginAsync(colleague);
        using var outsiderClient = await LoginAsync(outsider);
        Assert.Null((await colleagueClient.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);
        Assert.Null((await outsiderClient.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.PreferredLanguage);

        using var scope = factory.OpenScope(tenant.Id);
        var stored = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == colleague.Id).Select(u => u.PreferredLanguage).SingleAsync();
        Assert.Null(stored);
    }

    [Fact]
    public async Task Anonymous_caller_cannot_change_a_language()
    {
        using var anonymous = factory.CreateBrowserClient();

        var response = await anonymous.PutAsJsonAsync("/api/account/language", new UpdateLanguageRequest("en"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_default_language_is_turkish_validated_isolated_and_manager_only()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageSettings], manager);
        var outsider = await factory.CreateUserAsync(other);
        using var memberClient = await LoginAsync(member);
        using var managerClient = await LoginAsync(manager);
        using var outsiderClient = await LoginAsync(outsider);

        var defaults = await SettingAsync(memberClient);
        Assert.Equal("tr", defaults.Value);
        Assert.Equal(["tr", "en", "az"], defaults.Options.Select(o => o.Value));

        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync("/api/settings", Save("en", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerClient.PutAsJsonAsync("/api/settings", Save("de", 0))).StatusCode);
        Assert.Equal("tr", (await SettingAsync(memberClient)).Value);

        Assert.Equal(HttpStatusCode.OK, (await managerClient.PutAsJsonAsync("/api/settings", Save("az", 0))).StatusCode);
        Assert.Equal("az", (await SettingAsync(memberClient)).Value);

        // Başka firma kendi varsayılanını görür.
        Assert.Equal("tr", (await SettingAsync(outsiderClient)).Value);
    }

    private static async Task<SettingDto> SettingAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.Single(s => s.Key == SettingKeys.Language);

    private static SaveSettingsRequest Save(string value, long version) =>
        new(new Dictionary<string, SettingWriteDto> { [SettingKeys.Language] = new(value, version) });

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
