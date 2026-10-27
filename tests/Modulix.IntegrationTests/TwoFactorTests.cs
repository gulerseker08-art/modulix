using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Account;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;
using Modulix.Shared.Settings;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>Y7b: iki adımlı doğrulama (kurulum, giriş, kurtarma kodları), firmada zorunlu kılma ve platform sıfırlaması.</summary>
public class TwoFactorTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Setup_needs_the_password_and_a_valid_code_and_shows_recovery_codes()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], user);
        using var client = await LoginAsync(user);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/two-factor/setup", new StartTwoFactorRequest("yanlis"))).StatusCode);
        var setup = (await (await client.PostAsJsonAsync("/api/account/two-factor/setup",
            new StartTwoFactorRequest(ModulixApiFactory.Password))).Content.ReadFromJsonAsync<TwoFactorSetupResponse>())!;
        Assert.StartsWith("data:image/png;base64,", setup.QrCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/two-factor/enable", new EnableTwoFactorRequest("000000"))).StatusCode);
        var enabled = await client.PostAsJsonAsync("/api/account/two-factor/enable", new EnableTwoFactorRequest(Totp(setup.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(10, (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.Codes.Distinct().Count());

        // Güvenlik damgası değişti; bu oturum yeniden yazıldığı için açık kalır.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenant/current")).StatusCode);
        Assert.True((await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.TwoFactorEnabled);
        var status = (await client.GetFromJsonAsync<TwoFactorStatusResponse>("/api/account/two-factor"))!;
        Assert.Equal((true, 10), (status.Enabled, status.RecoveryCodesLeft));
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/account/two-factor/setup", new StartTwoFactorRequest(ModulixApiFactory.Password))).StatusCode);

        var regenerated = await client.PostAsJsonAsync("/api/account/two-factor/recovery-codes", new RegenerateRecoveryCodesRequest(ModulixApiFactory.Password));
        Assert.Equal(10, (await regenerated.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.Codes.Count);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/two-factor/disable", new DisableTwoFactorRequest(ModulixApiFactory.Password, "000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/account/two-factor/disable", new DisableTwoFactorRequest(ModulixApiFactory.Password, Totp(setup.SharedKey)))).StatusCode);
        Assert.False((await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.TwoFactorEnabled);

        var actions = (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account&userId={user.Id}"))!.Items.Select(i => i.Action).ToList();
        Assert.Contains(AuditOperation.TwoFactorEnabled, actions);
        Assert.Contains(AuditOperation.RecoveryCodesRegenerated, actions);
        Assert.Contains(AuditOperation.TwoFactorDisabled, actions);
    }

    [Fact]
    public async Task Sign_in_asks_for_the_second_step_and_a_recovery_code_works_once()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var (key, codes) = await EnableAsync(user);

        using var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest(Totp(key)))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        Assert.True((await login.Content.ReadFromJsonAsync<LoginChallengeResponse>())!.TwoFactorRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest("123456"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest(Totp(key)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenant/current")).StatusCode);

        // Kurtarma kodu küçük harfle ve tiresiz de yazılabilir; bir kez kullanılır ve e-posta gider.
        using var second = factory.CreateBrowserClient();
        await second.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/auth/login/two-factor",
            new TwoFactorLoginRequest(codes[0].Replace("-", string.Empty).ToLowerInvariant()))).StatusCode);
        using var third = factory.CreateBrowserClient();
        await third.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await third.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest(codes[0]))).StatusCode);
        Assert.Equal(9, (await second.GetFromJsonAsync<TwoFactorStatusResponse>("/api/account/two-factor"))!.RecoveryCodesLeft);
        Assert.Contains("9 kurtarma kodunuz kaldı", await LatestEmailBodyAsync(user.Email!));
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await EnableAsync(user);
        using var client = factory.CreateBrowserClient();
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest("ABCDE-FGHIJ"))).StatusCode);
        }

        Assert.Equal((HttpStatusCode)423,
            (await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest("ABCDE-FGHIJ"))).StatusCode);
        Assert.Equal((HttpStatusCode)423,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);
    }

    [Fact]
    public async Task Tenant_requirement_applies_only_in_that_tenant_until_the_person_sets_it_up()
    {
        var strict = await factory.CreateTenantAsync();
        var relaxed = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(strict);
        var member = await factory.CreateUserAsync(strict, relaxed);
        await factory.CreateRoleAsync(strict.Id, [ManageSettings, ManageUsers], manager);
        using var managerClient = await LoginAsync(manager);

        // Yönetici kendisi doğrulamasızken zorunlu kılamaz.
        var refused = await managerClient.PutAsJsonAsync("/api/settings", Require());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("iki adımlı", await refused.Content.ReadAsStringAsync());

        var (managerKey, _) = await EnableAsync(manager);
        using var managerAgain = await LoginAsync(manager, managerKey);
        Assert.Equal(HttpStatusCode.OK, (await managerAgain.PutAsJsonAsync("/api/settings", Require())).StatusCode);

        using var memberClient = await LoginAsync(member);
        await memberClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(strict.Id));
        var blocked = await memberClient.GetAsync("/api/tenant/current");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("two-factor-required", await blocked.Content.ReadAsStringAsync());
        var me = (await memberClient.GetFromJsonAsync<MeResponse>("/api/auth/me"))!;
        Assert.Equal((strict.Id, true, false), (me.ActiveTenantId!.Value, me.TwoFactorRequired, me.TwoFactorEnabled));

        // Diğer firmada çalışır.
        await memberClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(relaxed.Id));
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);

        // Kurulumdan sonra zorunlu firmaya girer; kapatamaz.
        await memberClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(strict.Id));
        var setup = (await (await memberClient.PostAsJsonAsync("/api/account/two-factor/setup",
            new StartTwoFactorRequest(ModulixApiFactory.Password))).Content.ReadFromJsonAsync<TwoFactorSetupResponse>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await memberClient.PostAsJsonAsync("/api/account/two-factor/enable", new EnableTwoFactorRequest(Totp(setup.SharedKey)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);
        Assert.Contains(strict.Name, (await memberClient.GetFromJsonAsync<TwoFactorStatusResponse>("/api/account/two-factor"))!.RequiredBy);
        Assert.Equal(HttpStatusCode.Conflict, (await memberClient.PostAsJsonAsync("/api/account/two-factor/disable",
            new DisableTwoFactorRequest(ModulixApiFactory.Password, Totp(setup.SharedKey)))).StatusCode);

        // Kullanıcı listesinde durum ve filtre.
        var on = (await managerAgain.GetFromJsonAsync<UserPageResponse>("/api/users?twoFactor=true"))!.Items;
        Assert.Equal(new[] { manager.Id, member.Id }.Order(), on.Select(u => u.UserId).Order());
        Assert.All(on, u => Assert.True(u.TwoFactorEnabled));
        Assert.Empty((await managerAgain.GetFromJsonAsync<UserPageResponse>("/api/users?twoFactor=false"))!.Items);
    }

    [Fact]
    public async Task Platform_administrator_resets_two_factor_and_closes_sessions()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], reader);
        var (key, _) = await EnableAsync(user);
        using var userClient = await LoginAsync(user, key);
        var platform = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(platform);
        using var platformClient = await LoginAsync(platform);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await userClient.PostAsJsonAsync("/api/platform/users/reset-two-factor", new ResetTwoFactorRequest(user.Email!))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await platformClient.PostAsJsonAsync("/api/platform/users/reset-two-factor", new ResetTwoFactorRequest("yok@test.modulix"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await platformClient.PostAsJsonAsync("/api/platform/users/reset-two-factor", new ResetTwoFactorRequest(user.Email!))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).StatusCode);
        using var fresh = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);

        using var readerClient = await LoginAsync(reader);
        var reset = (await readerClient.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account"))!
            .Items.Single(i => i.Action == AuditOperation.TwoFactorReset);
        Assert.Equal(platform.Id, reset.UserId);
        Assert.Equal(user.Id, reset.RecordId);
    }

    /// <summary>Kişinin iki adımlı doğrulamasını API ile açar; anahtarı ve kurtarma kodlarını döner.</summary>
    private async Task<(string Key, IReadOnlyList<string> Codes)> EnableAsync(ModulixUser user)
    {
        using var client = await LoginAsync(user);
        var setup = (await (await client.PostAsJsonAsync("/api/account/two-factor/setup",
            new StartTwoFactorRequest(ModulixApiFactory.Password))).Content.ReadFromJsonAsync<TwoFactorSetupResponse>())!;
        var enabled = await client.PostAsJsonAsync("/api/account/two-factor/enable", new EnableTwoFactorRequest(Totp(setup.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        return (setup.SharedKey, (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.Codes);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user, string? key = null)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        if (key is not null)
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            response = await client.PostAsJsonAsync("/api/auth/login/two-factor", new TwoFactorLoginRequest(Totp(key)));
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task SetPlatformAdministratorAsync(ModulixUser user)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
    }

    private async Task<string> LatestEmailBodyAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return (await db.EmailMessages.AsNoTracking().Where(e => e.ToAddress == address).OrderByDescending(e => e.CreatedAt).FirstAsync()).TextBody!;
    }

    private static SaveSettingsRequest Require() =>
        new(new Dictionary<string, SettingWriteDto> { [SettingKeys.RequireTwoFactor] = new("true", 0) });

    /// <summary>RFC 6238: doğrulama uygulamasının o anki 6 haneli kodu (30 saniyelik adım, HMAC-SHA1).</summary>
    private static string Totp(string sharedKey)
    {
        var key = Base32(sharedKey.Replace(" ", string.Empty).ToUpperInvariant());
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
