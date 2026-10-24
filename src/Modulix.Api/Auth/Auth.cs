using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Settings;
using Modulix.Infrastructure.Users;
using Modulix.Shared.Auth;
using Modulix.Shared.Users;

namespace Modulix.Api.Auth;

public static class AuthSetup
{
    public const string CookieName = "modulix.auth";

    public static IServiceCollection AddModulixAuth(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

            // Arayüz ile API aynı adreste çalışır; başka sitelerden gelen isteklere çerez eklenmez (CSRF koruması).
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;

            // API: giriş sayfasına yönlendirmek yerine durum kodu döner.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // Oturum düzenli aralıklarla yenilenirken aktif tenant iddiası korunur (her istekte yine doğrulanır).
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.OnRefreshingPrincipal = context =>
            {
                var tenantClaim = context.CurrentPrincipal?.FindFirst(ModulixClaimTypes.TenantId);
                if (tenantClaim is not null && context.NewPrincipal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(tenantClaim.Type, tenantClaim.Value));
                }

                return Task.CompletedTask;
            };
        });

        services
            .AddIdentityCore<ModulixUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<ModulixDbContext>()
            .AddSignInManager()
            .AddErrorDescriber<TurkishIdentityErrorDescriber>()

            // Şifre sıfırlama (varsayılan sağlayıcı, 24 saat) ve davet (7 gün) bağlantıları: AccountTokens.
            .AddDefaultTokenProviders()
            .AddTokenProvider<InvitationTokenProvider>(AccountTokens.InvitationProvider);
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = AccountTokens.PasswordResetLifespan);

        services.AddAuthorization();
        return services;
    }
}

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapPost("/active-tenant", SelectTenantAsync).RequireAuthorization();

        // E-postadaki bağlantılar (Y2): oturum gerektirmez; token doğrulanır.
        group.MapGet("/invitation", GetInvitationAsync).AllowAnonymous();
        group.MapPost("/accept-invitation", AcceptInvitationAsync).AllowAnonymous();
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        IMembershipDirectory directory,
        AccountAudit audit,
        TenantDisplayNames names,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "E-posta ve şifre zorunludur.");
        }

        var client = Client(http);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            audit.UnknownAccount(client);
            return InvalidCredentials();
        }

        // Hatalı şifre ve kilitlenme kişinin üye olduğu tüm firmaların denetim kaydına yazılır (Y5b).
        var wasLockedOut = await users.IsLockedOutAsync(user);
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            await (wasLockedOut
                ? audit.RecordAsync(user, AuditAction.SignInFailed, null, client, ct, AccountAuditReasons.Locked)
                : audit.RecordAsync(user, AuditAction.LockedOut, null, client, ct));
            return Results.Problem(
                statusCode: StatusCodes.Status423Locked,
                title: "Hesap geçici olarak kilitlendi.",
                detail: "Çok sayıda hatalı deneme yapıldı. 15 dakika sonra tekrar deneyin.");
        }

        if (!result.Succeeded)
        {
            await audit.RecordAsync(user, AuditAction.SignInFailed, null, client, ct, AccountAuditReasons.WrongPassword);
            return InvalidCredentials();
        }

        // Tek üyelik varsa o tenant otomatik seçilir; birden fazlaysa kullanıcı seçer.
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        var active = memberships.Count == 1 ? memberships[0] : null;
        if (active is not null)
        {
            await directory.RecordSignInAsync(active.MembershipId, ct);
            await audit.RecordAsync(user, AuditAction.SignedIn, [active.TenantId], client, ct);
        }

        await IssueSessionAsync(http, signIn, user, active?.TenantId);
        return Results.Ok(await ToResponseAsync(user, active?.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> LogoutAsync(SignInManager<ModulixUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        UserManager<ModulixUser> users,
        IMembershipDirectory directory,
        ITenantContextAccessor tenant,
        TenantDisplayNames names,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        return Results.Ok(await ToResponseAsync(user, tenant.Current?.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> SelectTenantAsync(
        SelectTenantRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        IMembershipDirectory directory,
        AccountAudit audit,
        TenantDisplayNames names,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Tenant seçimi istemciye güvenmez: etkin üyelik sunucuda doğrulanır.
        var membership = await directory.FindActiveAsync(user.Id, request.TenantId, ct);
        if (membership is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Bu tenant'a erişiminiz yok.");
        }

        // Firma seçmek o firmaya giriştir; denetim kaydına yalnızca seçilen firmada yazılır.
        await directory.RecordSignInAsync(membership.MembershipId, ct);
        await audit.RecordAsync(user, AuditAction.SignedIn, [membership.TenantId], Client(http), ct);
        await IssueSessionAsync(http, signIn, user, membership.TenantId);
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        return Results.Ok(await ToResponseAsync(user, membership.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> GetInvitationAsync(Guid uyelik, string? token, AccountLinks links, CancellationToken ct) =>
        await links.FindInvitationAsync(uyelik, token, ct) is { } invitation
            ? Results.Ok(new InvitationInfoResponse(invitation.TenantName, invitation.Email, invitation.DisplayName))
            : InvalidLink("Davet bağlantısı geçersiz veya süresi dolmuş. Kuruluşunuzun yöneticisinden yeni davet isteyin.");

    /// <summary>Şifreyi belirler, daveti kabul eder ve davet eden tenant'ta oturum açar.</summary>
    private static async Task<IResult> AcceptInvitationAsync(
        AcceptInvitationRequest request,
        HttpContext http,
        AccountLinks links,
        SignInManager<ModulixUser> signIn,
        IMembershipDirectory directory,
        AccountAudit audit,
        TenantDisplayNames names,
        CancellationToken ct)
    {
        var result = await links.AcceptInvitationAsync(request.MembershipId, request.Token, request.Password, ct);
        if (!result.Succeeded)
        {
            return result.InvalidLink
                ? InvalidLink("Davet bağlantısı geçersiz veya süresi dolmuş. Kuruluşunuzun yöneticisinden yeni davet isteyin.")
                : WeakPassword(result);
        }

        var user = result.User!;
        await audit.RecordAsync(user, AuditAction.InvitationAccepted, result.AcceptedTenantIds, Client(http), ct);
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        var active = memberships.SingleOrDefault(m => m.TenantId == result.TenantId);
        if (active is not null)
        {
            await directory.RecordSignInAsync(active.MembershipId, ct);
        }

        await IssueSessionAsync(http, signIn, user, active?.TenantId);
        return Results.Ok(await ToResponseAsync(user, active?.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request, HttpContext http, AccountLinks links, AccountAudit audit, CancellationToken ct)
    {
        var result = await links.ResetPasswordAsync(request.UserId, request.Token, request.Password, ct);
        if (!result.Succeeded)
        {
            return result.InvalidLink
                ? InvalidLink("Şifre sıfırlama bağlantısı geçersiz, kullanılmış veya süresi dolmuş. Yöneticinizden yeni bağlantı isteyin.")
                : WeakPassword(result);
        }

        await audit.RecordAsync(result.User!, AuditAction.PasswordReset, null, Client(http), ct);
        return Results.NoContent();
    }

    private static AccountClient Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null);

    private static async Task IssueSessionAsync(HttpContext http, SignInManager<ModulixUser> signIn, ModulixUser user, Guid? tenantId)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        if (tenantId is { } id && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(ModulixClaimTypes.TenantId, id.ToString()));
        }

        await http.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IsPersistent = false });
    }

    private static async Task<MeResponse> ToResponseAsync(
        ModulixUser user, Guid? activeTenantId, IReadOnlyList<MembershipSummary> memberships, TenantDisplayNames names, CancellationToken ct)
    {
        var shown = await names.GetAsync(memberships.Select(m => m.TenantId), user.Id, ct);
        return new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            activeTenantId,
            memberships.Select(m => new MembershipDto(m.TenantId, m.TenantName, m.TenantSlug, shown.GetValueOrDefault(m.TenantId))).ToList(),
            user.IsPlatformAdministrator);
    }

    private static IResult InvalidLink(string message) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: message);

    private static IResult WeakPassword(AccountLinkResult result) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { ["password"] = [.. result.PasswordErrors] },
            title: "Şifre kurallara uymuyor.",
            statusCode: StatusCodes.Status422UnprocessableEntity);

    // Kullanıcının var olup olmadığı belli edilmez.
    private static IResult InvalidCredentials() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "E-posta veya şifre hatalı.");
}
