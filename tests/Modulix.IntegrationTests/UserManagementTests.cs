using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Auth;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y2a: kullanıcı listesi, davet, devre dışı bırakma, şifre sıfırlama bağlantısı, kilit açma ve yönetici kuralı.
/// E-postalar kuyrukta kalır (testlerde işleyici çalışmaz); bağlantılar kuyruktaki e-postadan okunur.
/// </summary>
public partial class UserManagementTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string NewPassword = "Yeni.Sifre.2026";

    [Fact]
    public async Task List_shows_only_own_tenant_members_and_filters_them()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(other);
        await MakeAdministratorsAsync(tenant.Id, admin);
        var role = await factory.CreateRoleAsync(tenant.Id, ["crm.companies.read"], member);
        using var client = await LoginAsync(admin);

        var all = await client.GetFromJsonAsync<UserPageResponse>("/api/users");
        Assert.Equal(2, all!.TotalCount);
        Assert.DoesNotContain(all.Items, u => u.UserId == outsider.Id);
        var me = Assert.Single(all.Items, u => u.UserId == admin.Id);
        Assert.True(me.IsCurrentUser);
        Assert.True(me.IsAdministrator);
        Assert.NotNull(me.LastSignInAt);

        var byRole = await client.GetFromJsonAsync<UserPageResponse>($"/api/users?roleId={role.Id}");
        Assert.Equal(member.Id, Assert.Single(byRole!.Items).UserId);

        var bySearch = await client.GetFromJsonAsync<UserPageResponse>($"/api/users?search={Uri.EscapeDataString(member.Email![..10])}");
        Assert.Equal(member.Id, Assert.Single(bySearch!.Items).UserId);

        await factory.DisableMembershipAsync(member.Id, tenant.Id);
        var disabled = await client.GetFromJsonAsync<UserPageResponse>("/api/users?status=Disabled");
        Assert.Equal(member.Id, Assert.Single(disabled!.Items).UserId);

        var paged = await client.GetFromJsonAsync<UserPageResponse>("/api/users?pageSize=1&page=2");
        Assert.Single(paged!.Items);
        Assert.Equal(2, paged.TotalCount);

        // Başka tenant'ın üyeliği yok sayılır.
        var outsiderMembership = await factory.GetMembershipIdAsync(outsider.Id, other.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/users/{outsiderMembership}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/users/{outsiderMembership}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/users/{outsiderMembership}/password-reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/memberships/{outsiderMembership}/roles", new SetMembershipRolesRequest([]))).StatusCode);
    }

    [Fact]
    public async Task User_management_requires_permission()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(member);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), "Ali Veli", null))).StatusCode);
    }

    [Fact]
    public async Task Invitation_link_sets_the_password_once_and_signs_in()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        var role = await factory.CreateRoleAsync(tenant.Id, ["crm.companies.read"]);
        using var client = await LoginAsync(admin);
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest($" {email} ", "Zeynep Kaya", [role.Id]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invited = (await response.Content.ReadFromJsonAsync<InviteUserResponse>())!;
        Assert.False(invited.ExistingAccount);
        Assert.Equal(UserStatus.Invited, invited.User.Status);
        Assert.Equal("Zeynep Kaya", invited.User.DisplayName);
        Assert.Equal(role.Id, Assert.Single(invited.User.Roles).Id);

        // Davet bekleyen kişi giriş yapamaz ve sorumlu listesinde görünmez.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, NewPassword))).StatusCode);
        var members = await client.GetFromJsonAsync<List<TenantMemberResponse>>("/api/tenant/members");
        Assert.DoesNotContain(members!, m => m.UserId == invited.User.UserId);

        var link = await ReadLinkAsync(email, InvitationLink());
        Assert.Equal(invited.User.MembershipId, link.Id);
        var mail = await LatestEmailAsync(email);
        Assert.Contains(tenant.Name, mail.Subject);
        Assert.Contains("Test Kullanıcı", mail.HtmlBody);

        var info = await anonymous.GetFromJsonAsync<InvitationInfoResponse>(
            $"/api/auth/invitation?uyelik={link.Id}&token={Uri.EscapeDataString(link.Token)}");
        Assert.Equal(tenant.Name, info!.TenantName);
        Assert.Equal(email, info.Email);

        // Kurallara uymayan şifre bağlantıyı harcamaz.
        var weak = await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, "kisa"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.Contains("en az 10 karakter", await weak.Content.ReadAsStringAsync());

        var accepted = await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, NewPassword));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var session = (await accepted.Content.ReadFromJsonAsync<MeResponse>())!;
        Assert.Equal(tenant.Id, session.ActiveTenantId);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/tenant/current")).StatusCode);

        var detail = await client.GetFromJsonAsync<UserDto>($"/api/users/{link.Id}");
        Assert.Equal(UserStatus.Active, detail!.Status);
        Assert.NotNull(detail.LastSignInAt);

        // Tek kullanımlık.
        using var again = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await again.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, "Baska.Sifre.2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, NewPassword))).StatusCode);
    }

    [Fact]
    public async Task Inviting_an_existing_account_adds_it_at_once_without_touching_the_password()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(other);
        await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);

        var response = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(colleague.Email!.ToUpperInvariant(), "Başka Ad", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invited = (await response.Content.ReadFromJsonAsync<InviteUserResponse>())!;
        Assert.True(invited.ExistingAccount);
        Assert.Equal(UserStatus.Active, invited.User.Status);
        Assert.Equal(colleague.DisplayName, invited.User.DisplayName);

        var mail = await LatestEmailAsync(colleague.Email!);
        Assert.Contains("eklendiniz", mail.Subject);
        Assert.DoesNotContain("token=", mail.HtmlBody);

        using var colleagueClient = factory.CreateBrowserClient();
        var login = await colleagueClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(colleague.Email!, ModulixApiFactory.Password));
        Assert.Equal(2, (await login.Content.ReadFromJsonAsync<MeResponse>())!.Memberships.Count);

        // Zaten üye: çakışma.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(colleague.Email!, "X", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(admin.Email!, "X", null))).StatusCode);
    }

    [Fact]
    public async Task Invalid_invitation_fields_are_reported_per_field()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);

        var badEmail = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest("Ad <a@b.com>", "Ali", null));
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);
        Assert.Contains("\"email\"", await badEmail.Content.ReadAsStringAsync());

        var noName = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), " ", null));
        Assert.Contains("\"displayName\"", await noName.Content.ReadAsStringAsync());

        var unknownRole = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), "Ali", [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
    }

    [Fact]
    public async Task Cancelled_invitation_link_stops_working_and_resend_is_throttled()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin);
        var email = UniqueEmail();

        var invited = await InviteAsync(client, email);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsync($"/api/users/{invited.MembershipId}/invitation/resend", null)).StatusCode);

        var link = await ReadLinkAsync(email, InvitationLink());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{invited.MembershipId}/invitation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/users/{invited.MembershipId}")).StatusCode);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.GetAsync($"/api/auth/invitation?uyelik={link.Id}&token={Uri.EscapeDataString(link.Token)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, NewPassword))).StatusCode);

        // Aynı kişi yeniden davet edilebilir; aynı hesap kullanılır.
        var reinvited = await InviteAsync(client, email);
        Assert.Equal(invited.UserId, reinvited.UserId);
        Assert.NotEqual(invited.MembershipId, reinvited.MembershipId);
    }

    [Fact]
    public async Task Expired_invitation_link_is_rejected()
    {
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<InvitationTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromSeconds(-1))));
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin, host);
        var email = UniqueEmail();

        var invited = await InviteAsync(client, email);
        var link = await ReadLinkAsync(email, InvitationLink());

        using var anonymous = BrowserClient(host);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(invited.MembershipId, link.Token, NewPassword))).StatusCode);
    }

    [Fact]
    public async Task Invitations_per_hour_are_limited()
    {
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Users:MaxInvitationsPerHour", "2"));
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        using var client = await LoginAsync(admin, host);

        await InviteAsync(client, UniqueEmail());
        await InviteAsync(client, UniqueEmail());
        var third = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), "Üçüncü", null));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_grants_the_administrator_role_or_manages_administrators()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var userManager = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        var system = await MakeAdministratorsAsync(tenant.Id, admin);
        var managerRole = await factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.ManageUsers], userManager);
        var adminMembership = await factory.GetMembershipIdAsync(admin.Id, tenant.Id);
        var managerMembership = await factory.GetMembershipIdAsync(userManager.Id, tenant.Id);
        var memberMembership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(userManager);
        Assert.False((await client.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access"))!.IsAdministrator);

        // Kendini veya başkasını yönetici yapamaz, yönetici rolüyle davet edemez.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/memberships/{managerMembership}/roles", new SetMembershipRolesRequest([managerRole.Id, system.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/memberships/{memberMembership}/roles", new SetMembershipRolesRequest([system.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), "Ali", [system.Id]))).StatusCode);

        // Yöneticinin hesabında işlem yapamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/users/{adminMembership}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/users/{adminMembership}/password-reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/memberships/{adminMembership}/roles", new SetMembershipRolesRequest([]))).StatusCode);

        // Diğer kullanıcıları yönetir.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/memberships/{memberMembership}/roles", new SetMembershipRolesRequest([managerRole.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{memberMembership}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(UniqueEmail(), "Ali", [managerRole.Id]))).StatusCode);

        // Yönetici yönetici rolü verebilir.
        using var adminClient = await LoginAsync(admin);
        Assert.True((await adminClient.GetFromJsonAsync<MyAccessResponse>("/api/tenant/access"))!.IsAdministrator);
        Assert.Equal(HttpStatusCode.NoContent,
            (await adminClient.PutAsJsonAsync($"/api/memberships/{managerMembership}/roles", new SetMembershipRolesRequest([system.Id]))).StatusCode);
    }

    [Fact]
    public async Task Disabling_blocks_access_to_this_tenant_only_and_protects_self()
    {
        var tenant = await factory.CreateTenantAsync();
        var other = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var secondAdmin = await factory.CreateUserAsync(tenant, other);
        await MakeAdministratorsAsync(tenant.Id, admin, secondAdmin);
        var adminMembership = await factory.GetMembershipIdAsync(admin.Id, tenant.Id);
        var secondMembership = await factory.GetMembershipIdAsync(secondAdmin.Id, tenant.Id);
        using var client = await LoginAsync(admin);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/users/{adminMembership}/disable", null)).StatusCode);

        using var secondClient = factory.CreateBrowserClient();
        await secondClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(secondAdmin.Email!, ModulixApiFactory.Password));
        await secondClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id));
        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/api/tenant/current")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{secondMembership}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secondClient.GetAsync("/api/tenant/current")).StatusCode);
        Assert.Equal(UserStatus.Disabled, (await client.GetFromJsonAsync<UserDto>($"/api/users/{secondMembership}"))!.Status);

        // Diğer tenant'taki üyeliği etkilenmez.
        Assert.Equal(HttpStatusCode.OK, (await secondClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(other.Id))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{secondMembership}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondClient.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(tenant.Id))).StatusCode);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var statusChanges = await db.AuditLog.CountAsync(a => a.EntityKey == "core.membership" && a.RecordId == secondMembership);
        Assert.True(statusChanges >= 3, $"Rol ataması, devre dışı bırakma ve etkinleştirme denetim kaydında olmalı ({statusChanges}).");
    }

    [Fact]
    public async Task Password_reset_link_sets_a_new_password_once()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        var memberMembership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(admin);

        var sent = await client.PostAsync($"/api/users/{memberMembership}/password-reset", null);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.Empty(await sent.Content.ReadAsStringAsync());

        var mail = await LatestEmailAsync(member.Email!);
        Assert.Contains(tenant.Name, mail.HtmlBody);
        var link = await ReadLinkAsync(member.Email!, PasswordResetLink());
        Assert.Equal(member.Id, link.Id);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(member.Id, link.Token, "kisa"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(member.Id, link.Token, NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(member.Id, link.Token, "Baska.Sifre.2026"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, ModulixApiFactory.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, NewPassword))).StatusCode);
    }

    [Fact]
    public async Task Administrator_unlocks_a_locked_account()
    {
        var tenant = await factory.CreateTenantAsync();
        var admin = await factory.CreateUserAsync(tenant);
        var member = await factory.CreateUserAsync(tenant);
        await MakeAdministratorsAsync(tenant.Id, admin);
        var memberMembership = await factory.GetMembershipIdAsync(member.Id, tenant.Id);
        using var client = await LoginAsync(admin);

        using var memberClient = factory.CreateBrowserClient();
        for (var i = 0; i < 5; i++)
        {
            await memberClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, "Yanlis.Sifre.1"));
        }

        Assert.Equal(HttpStatusCode.Locked,
            (await memberClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, ModulixApiFactory.Password))).StatusCode);
        Assert.True((await client.GetFromJsonAsync<UserDto>($"/api/users/{memberMembership}"))!.IsLockedOut);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/users/{memberMembership}/unlock", null)).StatusCode);

        Assert.False((await client.GetFromJsonAsync<UserDto>($"/api/users/{memberMembership}"))!.IsLockedOut);
        Assert.Equal(HttpStatusCode.OK,
            (await memberClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email!, ModulixApiFactory.Password))).StatusCode);
    }

    [GeneratedRegex(@"/davet\?uyelik=(?<id>[0-9a-f-]+)&token=(?<token>[^\s""<]+)")]
    private static partial Regex InvitationLink();

    [GeneratedRegex(@"/sifre-sifirla\?kullanici=(?<id>[0-9a-f-]+)&token=(?<token>[^\s""<]+)")]
    private static partial Regex PasswordResetLink();

    private async Task<UserDto> InviteAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Davetli Kişi", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InviteUserResponse>())!.User;
    }

    private async Task<Infrastructure.Email.QueuedEmail> LatestEmailAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.EmailMessages.AsNoTracking()
            .Where(e => e.ToAddress == address)
            .OrderByDescending(e => e.CreatedAt)
            .FirstAsync();
    }

    /// <summary>Kişiye giden son e-postadaki bağlantının kimliği ve token'ı.</summary>
    private async Task<(Guid Id, string Token)> ReadLinkAsync(string address, Regex pattern)
    {
        var mail = await LatestEmailAsync(address);
        var match = pattern.Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        return (Guid.Parse(match.Groups["id"].Value), Uri.UnescapeDataString(match.Groups["token"].Value));
    }

    private async Task<RoleSummary> MakeAdministratorsAsync(Guid tenantId, params ModulixUser[] users)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        foreach (var user in users)
        {
            var membershipId = await factory.GetMembershipIdAsync(user.Id, tenantId);
            var current = (await roles.GetMembershipRolesAsync(membershipId, CancellationToken.None))!.Select(r => r.Id);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [.. current, system.Id], CancellationToken.None));
        }

        return system;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user, WebApplicationFactory<Program>? host = null)
    {
        var client = BrowserClient(host ?? factory);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static HttpClient BrowserClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    private static string UniqueEmail() => $"d-{Guid.NewGuid():N}@test.modulix";
}
