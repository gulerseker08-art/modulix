using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Account;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>Y7a: kişinin kendi hesabı (ad, şifre, e-posta), "Şifremi unuttum" ve sunucudaki oturumlar.</summary>
public partial class AccountSecurityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string NewPassword = "Yeni.Sifre.2026";

    [Fact]
    public async Task Name_change_is_shown_in_every_tenant_and_audited()
    {
        var first = await factory.CreateTenantAsync();
        var second = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(first, second);
        await factory.CreateRoleAsync(second.Id, [ReadAudit], user);
        using var client = await LoginAsync(user);

        var empty = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("  "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("displayName", await empty.Content.ReadAsStringAsync());

        var saved = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(" Yeni Ad "));
        Assert.Equal("Yeni Ad", (await saved.Content.ReadFromJsonAsync<AccountProfileResponse>())!.DisplayName);
        Assert.Equal("Yeni Ad", (await client.GetFromJsonAsync<MeResponse>("/api/auth/me"))!.DisplayName);

        await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(second.Id));
        var entry = (await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account&userId={user.Id}"))!
            .Items.First(i => i.Action == AuditOperation.Updated);
        Assert.Equal("Hesap", entry.EntityName);
        var change = Assert.Single(entry.Changes, c => c.Field == "displayName");
        Assert.Equal((user.DisplayName, "Yeni Ad"), (change.Old, change.New));
    }

    [Fact]
    public async Task Password_change_keeps_this_session_and_closes_the_others()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user);
        using var other = await LoginAsync(user);

        var wrong = await client.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest("yanlis", NewPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("currentPassword", await wrong.Content.ReadAsStringAsync());
        var weak = await client.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(ModulixApiFactory.Password, "kisa"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("\"password\"", await weak.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(ModulixApiFactory.Password, NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenant/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);

        using var fresh = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, NewPassword))).StatusCode);
    }

    [Fact]
    public async Task Sessions_are_listed_and_closed_by_their_owner_only()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var stranger = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user);
        using var phone = await LoginAsync(user);
        using var tablet = await LoginAsync(user);
        using var strangerClient = await LoginAsync(stranger);

        var sessions = (await client.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions"))!;
        Assert.Equal(3, sessions.Count);
        Assert.True(sessions[0].IsCurrent);
        Assert.Single(sessions, s => s.IsCurrent);

        var phoneSession = (await phone.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions"))!.Single(s => s.IsCurrent);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.DeleteAsync($"/api/account/sessions/{phoneSession.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/account/sessions/{phoneSession.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/account/sessions/{phoneSession.Id}")).StatusCode);

        var closed = await client.PostAsync("/api/account/sessions/close-others", null);
        Assert.Equal(1, (await closed.Content.ReadFromJsonAsync<CloseSessionsResponse>())!.Closed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await strangerClient.GetAsync("/api/auth/me")).StatusCode);

        // Çıkış oturumu sunucuda da kapatır.
        var current = (await client.GetFromJsonAsync<List<SessionDto>>("/api/account/sessions"))!.Single();
        await client.PostAsync("/api/auth/logout", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.NotNull((await db.UserSessions.AsNoTracking().SingleAsync(s => s.Id == current.Id)).ClosedAt);
    }

    [Fact]
    public async Task Administrator_closes_a_users_sessions_and_only_an_administrator_closes_an_administrators()
    {
        var tenant = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var administrator = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ManageUsers, ReadAudit], manager);
        await MakeAdministratorAsync(tenant.Id, administrator);
        using var managerClient = await LoginAsync(manager);
        using var memberClient = await LoginAsync(member);
        using var memberSecond = await LoginAsync(member);
        using var administratorClient = await LoginAsync(administrator);
        var memberId = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        var administratorId = await factory.GetMembershipIdAsync(administrator.Id, tenant.Id);
        var managerId = await factory.GetMembershipIdAsync(manager.Id, tenant.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PostAsync($"/api/users/{managerId}/close-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsync($"/api/users/{administratorId}/close-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await administratorClient.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await managerClient.PostAsync($"/api/users/{memberId}/close-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await memberClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await memberSecond.GetAsync("/api/auth/me")).StatusCode);

        var entry = (await managerClient.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.membership&userId={manager.Id}"))!.Items[0];
        Assert.Equal("sessionsClosedAt", Assert.Single(entry.Changes).Field);
    }

    [Fact]
    public async Task Email_change_needs_the_link_closes_sessions_and_tells_the_old_address()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var oldEmail = user.Email!;
        var newEmail = UniqueEmail();
        using var client = await LoginAsync(user);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(newEmail, "yanlis"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(oldEmail.ToUpperInvariant(), ModulixApiFactory.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest("adres-degil", ModulixApiFactory.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(newEmail, ModulixApiFactory.Password))).StatusCode);

        // E-posta bağlantı açılana kadar değişmez.
        Assert.Equal(oldEmail, (await client.GetFromJsonAsync<AccountProfileResponse>("/api/account"))!.Email);
        var (userId, email, token) = await ReadEmailChangeLinkAsync(newEmail);
        Assert.Equal((user.Id, newEmail), (userId, email));

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("/api/auth/confirm-email-change", new ConfirmEmailChangeRequest(userId, UniqueEmail(), token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await anonymous.PostAsJsonAsync("/api/auth/confirm-email-change", new ConfirmEmailChangeRequest(userId, email, token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("/api/auth/confirm-email-change", new ConfirmEmailChangeRequest(userId, email, token))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Contains(newEmail, (await LatestEmailAsync(oldEmail)).TextBody);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(oldEmail, ModulixApiFactory.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(newEmail, ModulixApiFactory.Password))).StatusCode);
    }

    [Fact]
    public async Task Email_change_to_an_address_in_use_fails_only_at_the_link()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var taken = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user);

        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/account/email", new ChangeEmailRequest(taken.Email!, ModulixApiFactory.Password))).StatusCode);
        var (userId, email, token) = await ReadEmailChangeLinkAsync(taken.Email!);
        var confirm = await client.PostAsJsonAsync("/api/auth/confirm-email-change", new ConfirmEmailChangeRequest(userId, email, token));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Contains("kullanılamıyor", await confirm.Content.ReadAsStringAsync());
        Assert.Equal(user.Email, (await client.GetFromJsonAsync<AccountProfileResponse>("/api/account"))!.Email);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_for_everyone_and_sends_one_link_per_interval()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], reader);
        using var anonymous = factory.CreateBrowserClient();

        var unknown = UniqueEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(unknown))).StatusCode);
        Assert.Equal(0, await EmailCountAsync(unknown));

        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email!))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email!))).StatusCode);
        Assert.Equal(1, await EmailCountAsync(user.Email!));

        var mail = await LatestEmailAsync(user.Email!);
        var match = PasswordResetLink().Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/auth/reset-password",
            new ResetPasswordRequest(Guid.Parse(match.Groups["id"].Value), Uri.UnescapeDataString(match.Groups["token"].Value), NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, NewPassword))).StatusCode);

        using var readerClient = await LoginAsync(reader);
        var actions = (await readerClient.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account&userId={user.Id}"))!
            .Items.Select(i => i.Action).ToList();
        Assert.Contains(AuditOperation.PasswordResetRequested, actions);
        Assert.Contains(AuditOperation.PasswordReset, actions);
    }

    [Fact]
    public async Task Forgot_password_is_limited_per_address_of_the_requester()
    {
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Account:ForgotPasswordPerIpPerHour", "2"));
        using var anonymous = BrowserClient(host);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted,
                (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(UniqueEmail()))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(UniqueEmail()))).StatusCode);
    }

    [Fact]
    public void Device_names_are_read_from_the_user_agent()
    {
        Assert.Equal("Chrome · Windows", DeviceName.Of("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"));
        Assert.Equal("Edge · Windows", DeviceName.Of("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0"));
        Assert.Equal("Safari · iOS", DeviceName.Of("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile Safari/604.1"));
        Assert.Equal("Firefox · Linux", DeviceName.Of("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0"));
        Assert.Equal("Bilinmeyen tarayıcı", DeviceName.Of(null));
    }

    [GeneratedRegex(@"/sifre-sifirla\?kullanici=(?<id>[0-9a-f-]+)&token=(?<token>[^\s""<]+)")]
    private static partial Regex PasswordResetLink();

    [GeneratedRegex(@"/eposta-onayla\?kullanici=(?<id>[0-9a-f-]+)&eposta=(?<email>[^&\s]+)&token=(?<token>[^\s""<]+)")]
    private static partial Regex EmailChangeLink();

    private async Task<(Guid UserId, string Email, string Token)> ReadEmailChangeLinkAsync(string address)
    {
        var mail = await LatestEmailAsync(address);
        var match = EmailChangeLink().Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        return (Guid.Parse(match.Groups["id"].Value), Uri.UnescapeDataString(match.Groups["email"].Value), Uri.UnescapeDataString(match.Groups["token"].Value));
    }

    private async Task<Infrastructure.Email.QueuedEmail> LatestEmailAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await db.EmailMessages.AsNoTracking().Where(e => e.ToAddress == address).OrderByDescending(e => e.CreatedAt).FirstAsync();
    }

    private async Task<int> EmailCountAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EmailMessages.CountAsync(e => e.ToAddress == address);
    }

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static HttpClient BrowserClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    private static string UniqueEmail() => $"h-{Guid.NewGuid():N}@test.modulix";
}
