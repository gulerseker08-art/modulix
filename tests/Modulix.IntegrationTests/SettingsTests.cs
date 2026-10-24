using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Settings;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>Y6a: firma ayarları. Varsayılanlar, izin, doğrulama, çakışma ve denetim kaydı.</summary>
public class SettingsTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Members_read_settings_with_defaults_and_only_managers_write()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageSettings], manager);
        using var memberClient = await LoginAsync(member);
        using var managerClient = await LoginAsync(manager);

        var defaults = await GetAsync(memberClient);
        Assert.Equal("Europe/Istanbul", defaults[SettingKeys.TimeZone].Value);
        Assert.Equal("TRY", defaults[SettingKeys.Currency].Value);
        Assert.Equal(0, defaults[SettingKeys.Currency].Version);
        Assert.Equal(SettingKind.Choice, defaults[SettingKeys.Currency].Kind);
        Assert.Contains(defaults[SettingKeys.Currency].Options, o => o.Value == "EUR");
        Assert.Equal("Firma profili", defaults[SettingKeys.DisplayName].Group);
        Assert.Equal("", defaults[SettingKeys.DisplayName].Value);

        var request = Save((SettingKeys.Currency, "USD", 0), (SettingKeys.DisplayName, "Anka", 0));
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync("/api/settings", request)).StatusCode);

        var saved = await managerClient.PutAsJsonAsync("/api/settings", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = (await saved.Content.ReadFromJsonAsync<SettingsResponse>())!.Settings.ToDictionary(s => s.Key);
        Assert.Equal(("USD", 1L), (after[SettingKeys.Currency].Value, after[SettingKeys.Currency].Version));
        Assert.Equal("USD", (await GetAsync(memberClient))[SettingKeys.Currency].Value);

        // Başka firma kendi (varsayılan) değerini görür.
        var other = await factory.CreateTenantAsync();
        using var otherClient = await LoginAsync(await factory.CreateUserAsync(other));
        Assert.Equal("TRY", (await GetAsync(otherClient))[SettingKeys.Currency].Value);
    }

    [Fact]
    public async Task Unknown_keys_and_invalid_values_are_rejected_and_nothing_is_written()
    {
        var (client, tenantId) = await ManagerAsync();

        var unknown = await client.PutAsJsonAsync("/api/settings", Save(("core.yok", "x", 0)));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.True((await unknown.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().ContainsKey("core.yok"));

        var invalid = await client.PutAsJsonAsync("/api/settings",
            Save((SettingKeys.Currency, "XYZ", 0), (SettingKeys.Email, "muhasebe", 0), (SettingKeys.LegalName, "Anka Gıda A.Ş.", 0)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.True(errors.ContainsKey(SettingKeys.Currency));
        Assert.True(errors.ContainsKey(SettingKeys.Email));
        Assert.False(errors.ContainsKey(SettingKeys.LegalName));

        Assert.Equal("", (await GetAsync(client))[SettingKeys.LegalName].Value);
        using var scope = factory.OpenScope(tenantId);
        Assert.False(await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().TenantSettings.AnyAsync());
    }

    [Fact]
    public async Task Stale_version_conflicts_and_writes_nothing()
    {
        var (client, _) = await ManagerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "EUR", 0)))).StatusCode);

        // Aynı ekranı açık tutan ikinci kişi eski sürümle kaydeder; ad değişikliği de yazılmaz.
        var stale = await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "GBP", 0), (SettingKeys.DisplayName, "Anka", 0)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await GetAsync(client);
        Assert.Equal(("EUR", 1L), (current[SettingKeys.Currency].Value, current[SettingKeys.Currency].Version));
        Assert.Equal("", current[SettingKeys.DisplayName].Value);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "GBP", 1)))).StatusCode);
    }

    [Fact]
    public async Task Changes_are_audited_with_setting_names_and_unchanged_values_are_not()
    {
        var (client, _) = await ManagerAsync();
        await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "USD", 0)));
        await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "EUR", 1)));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings", Save((SettingKeys.Currency, "EUR", 2)))).StatusCode);

        var page = (await client.GetFromJsonAsync<AuditPageResponse>("/api/audit?entityKey=core.tenant-setting"))!;
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, i => Assert.Equal(("Firma ayarı", "Para birimi"), (i.EntityName, i.RecordTitle)));
        var change = Assert.Single(page.Items[0].Changes);
        Assert.Equal(("USD", "EUR"), (change.Old, change.New));
    }

    [Fact]
    public async Task Generic_record_endpoint_also_follows_the_definitions()
    {
        var (client, _) = await ManagerAsync();

        var unknown = await client.PostAsJsonAsync("/api/records/core.tenant-setting",
            new { values = new JsonObject { ["key"] = "core.yok", ["value"] = "x" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var created = await client.PostAsJsonAsync("/api/records/core.tenant-setting",
            new { values = new JsonObject { ["key"] = SettingKeys.Currency, ["value"] = "USD" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var invalid = await client.PutAsJsonAsync($"/api/records/core.tenant-setting/{id}",
            new { values = new JsonObject { ["value"] = "XYZ" }, version = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public void Shared_setting_keys_match_the_core_module()
    {
        // Arayüz ayarları bu anahtarlarla okur; çekirdekteki bir değişiklik arayüzü sessizce bozmasın.
        static IEnumerable<string> Keys(Type type) => type.GetFields().Select(f => (string)f.GetValue(null)!).Order();
        Assert.Equal(Keys(typeof(Core.Modules.CoreModule.SettingKeys)), Keys(typeof(SettingKeys)));
    }

    private async Task<(HttpClient Client, Guid TenantId)> ManagerAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageSettings, ReadAudit], manager);
        return (await LoginAsync(manager), tenant.Id);
    }

    private static SaveSettingsRequest Save(params (string Key, string Value, long Version)[] values) =>
        new(values.ToDictionary(v => v.Key, v => new SettingWriteDto(v.Value, v.Version)));

    private static async Task<Dictionary<string, SettingDto>> GetAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.ToDictionary(s => s.Key);

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
