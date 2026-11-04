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
using Modulix.Api.Users;
using Modulix.Shared.Account;
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

            // Güvenlik damgasının yanında sunucudaki oturum da denetlenir: kapatılan oturumun çerezi hemen geçersizdir (Y7a).
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);
                if (context.Principal is not { } principal)
                {
                    return;
                }

                var sessions = context.HttpContext.RequestServices.GetRequiredService<UserSessions>();
                if (AuthEndpoints.SessionId(principal) is not { } sessionId
                    || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                    || !await sessions.ValidateAsync(sessionId, userId, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };
        });

        // Oturum düzenli aralıklarla yenilenirken aktif tenant ve oturum iddiaları korunur (her istekte yine doğrulanır).
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.OnRefreshingPrincipal = context =>
            {
                if (context.NewPrincipal?.Identity is not ClaimsIdentity identity)
                {
                    return Task.CompletedTask;
                }

                foreach (var type in new[] { ModulixClaimTypes.TenantId, ModulixClaimTypes.SessionId, ModulixClaimTypes.CompanyId })
                {
                    if (context.CurrentPrincipal?.FindFirst(type) is { } claim)
                    {
                        identity.AddClaim(new Claim(claim.Type, claim.Value));
                    }
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
        group.MapPost("/login/two-factor", TwoFactorLoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapPost("/active-tenant", SelectTenantAsync).RequireAuthorization();
        group.MapPost("/active-company", SelectCompanyAsync).RequireAuthorization().AddEndpointFilter<RequireTenantFilter>();

        // E-postadaki bağlantılar (Y2): oturum gerektirmez; token doğrulanır.
        group.MapGet("/invitation", GetInvitationAsync).AllowAnonymous();
        group.MapPost("/accept-invitation", AcceptInvitationAsync).AllowAnonymous();
        group.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous();

        // Hesap güvenliği (Y7a): "Şifremi unuttum" ve e-posta değişikliğinin onay bağlantısı.
        group.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous();
        group.MapPost("/confirm-email-change", ConfirmEmailChangeAsync).AllowAnonymous();

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
        UserSessions sessions,
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
            return Locked();
        }

        if (!result.Succeeded)
        {
            await audit.RecordAsync(user, AuditAction.SignInFailed, null, client, ct, AccountAuditReasons.WrongPassword);
            return InvalidCredentials();
        }

        // İki adımlı doğrulama (Y7b): oturum ikinci adımdan sonra açılır. Kişi 5 dakika geçerli ayrı bir çerezle hatırlanır.
        if (user.TwoFactorEnabled)
        {
            var pending = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Id.ToString())], IdentityConstants.TwoFactorUserIdScheme);
            await http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(pending));
            return Results.Json(new LoginChallengeResponse(true), statusCode: StatusCodes.Status202Accepted);
        }

        return await CompleteSignInAsync(http, user, signIn, directory, audit, names, sessions, ct);
    }

    /// <summary>Girişin ikinci adımı: uygulamadaki kod veya bir kurtarma kodu. Hatalı kodlar hatalı şifre gibi sayılır ve kilitler.</summary>
    private static async Task<IResult> TwoFactorLoginAsync(
        TwoFactorLoginRequest request,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        IMembershipDirectory directory,
        AccountAudit audit,
        TenantDisplayNames names,
        UserSessions sessions,
        TwoFactorAccounts twoFactor,
        CancellationToken ct)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || !user.TwoFactorEnabled)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Girişin süresi doldu. E-posta ve şifrenizle yeniden giriş yapın.");
        }

        var client = Client(http);
        if (await users.IsLockedOutAsync(user))
        {
            await audit.RecordAsync(user, AuditAction.SignInFailed, null, client, ct, AccountAuditReasons.Locked);
            return Locked();
        }

        if (await twoFactor.VerifySignInAsync(user, request.Code, PublicUrl.Resolve(http), client, ct) == TwoFactorCodeResult.Invalid)
        {
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user))
            {
                await audit.RecordAsync(user, AuditAction.LockedOut, null, client, ct);
                await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
                return Locked();
            }

            await audit.RecordAsync(user, AuditAction.SignInFailed, null, client, ct, AccountAuditReasons.WrongTwoFactorCode);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Kod doğru değil. Uygulamadaki güncel kodu veya bir kurtarma kodunu girin.");
        }

        await users.ResetAccessFailedCountAsync(user);
        await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        return await CompleteSignInAsync(http, user, signIn, directory, audit, names, sessions, ct);
    }

    private static async Task<IResult> CompleteSignInAsync(
        HttpContext http,
        ModulixUser user,
        SignInManager<ModulixUser> signIn,
        IMembershipDirectory directory,
        AccountAudit audit,
        TenantDisplayNames names,
        UserSessions sessions,
        CancellationToken ct)
    {
        var client = Client(http);

        // Tek üyelik varsa o tenant otomatik seçilir; birden fazlaysa kullanıcı seçer.
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        var active = memberships.Count == 1 ? memberships[0] : null;
        if (active is not null)
        {
            await directory.RecordSignInAsync(active.MembershipId, ct);
            await audit.RecordAsync(user, AuditAction.SignedIn, [active.TenantId], client, ct);
        }

        await IssueSessionAsync(http, signIn, user, active?.TenantId, await sessions.StartAsync(user.Id, client, ct));
        return Results.Ok(await ToResponseAsync(user, active?.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> LogoutAsync(
        ClaimsPrincipal principal, UserManager<ModulixUser> users, SignInManager<ModulixUser> signIn, UserSessions sessions, CancellationToken ct)
    {
        if (SessionId(principal) is { } sessionId && Guid.TryParse(users.GetUserId(principal), out var userId))
        {
            await sessions.CloseAsync(userId, sessionId, ct);
        }

        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        UserManager<ModulixUser> users,
        IMembershipDirectory directory,
        HttpTenantContextAccessor tenant,
        TenantDisplayNames names,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Firma iki adımlı doğrulama istiyorsa (Y7b) firma seçili kalır ama ekranlar kurulum yapılana kadar açılmaz.
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        var response = await ToResponseAsync(user, tenant.Current?.TenantId ?? tenant.TwoFactorRequiredTenantId, memberships, names, ct);
        return Results.Ok(response with { TwoFactorRequired = tenant.TwoFactorRequiredTenantId is not null });
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
        await IssueSessionAsync(http, signIn, user, membership.TenantId, SessionId(principal)!.Value);
        var memberships = await directory.GetActiveForUserAsync(user.Id, ct);
        return Results.Ok(await ToResponseAsync(user, membership.TenantId, memberships, names, ct));
    }

    /// <summary>
    /// Aktif işletici şirketi değiştirir (ADR-0008, adım 2). İstemciye güvenmez: şirketin bu oturumda erişilebilir
    /// olduğu sunucuda doğrulanır (aktif şirket bağlamı her istekte yeniden hesaplanır). Seçim oturum çerezine yazılır.
    /// </summary>
    private static async Task<IResult> SelectCompanyAsync(
        SelectCompanyRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        HttpTenantContextAccessor tenant,
        ICompanyContextAccessor company,
        AccountAudit audit,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (company.Current is not { } companyContext || !companyContext.CanAccess(request.CompanyId))
        {
            // Yetkisiz şirket seçimi güvenlik denetiminde görünür; erişim yine reddedilir.
            await audit.RecordAsync(user, AuditAction.CompanySelectionDenied, [tenant.Current!.TenantId], Client(http), ct);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Bu şirkete erişiminiz yok.");
        }

        await IssueSessionAsync(http, signIn, user, tenant.Current!.TenantId, SessionId(principal)!.Value, request.CompanyId);
        return Results.NoContent();
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
        UserSessions sessions,
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

        await IssueSessionAsync(http, signIn, user, active?.TenantId, await sessions.StartAsync(user.Id, Client(http), ct));
        return Results.Ok(await ToResponseAsync(user, active?.TenantId, memberships, names, ct));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request, HttpContext http, AccountLinks links, AccountAudit audit, CancellationToken ct)
    {
        var result = await links.ResetPasswordAsync(request.UserId, request.Token, request.Password, ct);
        if (!result.Succeeded)
        {
            return result.InvalidLink
                ? InvalidLink("Şifre sıfırlama bağlantısı geçersiz, kullanılmış veya süresi dolmuş. Giriş sayfasındaki \"Şifremi unuttum\" ile yeni bağlantı isteyin.")
                : WeakPassword(result);
        }

        await audit.RecordAsync(result.User!, AuditAction.PasswordReset, null, Client(http), ct);
        return Results.NoContent();
    }

    /// <summary>Hesap olsun olmasın aynı yanıt (202) döner; yalnızca IP sınırı aşılırsa 429.</summary>
    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request, HttpContext http, AccountSecurity account, CancellationToken ct) =>
        await account.RequestPasswordResetAsync(request.Email, PublicUrl.Resolve(http), Client(http), ct) == ForgotPasswordResult.TooManyRequests
            ? Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Çok sayıda istek yapıldı. Bir süre sonra tekrar deneyin.")
            : Results.Accepted();

    private static async Task<IResult> ConfirmEmailChangeAsync(
        ConfirmEmailChangeRequest request, HttpContext http, AccountSecurity account, CancellationToken ct) =>
        await account.ConfirmEmailChangeAsync(request.UserId, request.Email, request.Token, PublicUrl.Resolve(http), Client(http), ct) switch
        {
            EmailChangeResult.Changed => Results.NoContent(),
            EmailChangeResult.AddressUnavailable => InvalidLink("Bu e-posta adresi kullanılamıyor. Hesabınızın e-postası değişmedi."),
            _ => InvalidLink("Onay bağlantısı geçersiz, kullanılmış veya süresi dolmuş. Hesabım sayfasından yeniden isteyin."),
        };

    internal static Guid? SessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ModulixClaimTypes.SessionId), out var id) ? id : null;

    internal static AccountClient Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null);

    /// <summary>Oturum çerezini yazar; şifre değişince (güvenlik damgası yenilenir) aynı oturumla yeniden yazılır.</summary>
    internal static async Task IssueSessionAsync(
        HttpContext http, SignInManager<ModulixUser> signIn, ModulixUser user, Guid? tenantId, Guid sessionId, Guid? companyId = null)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(ModulixClaimTypes.SessionId, sessionId.ToString()));
        if (tenantId is { } id)
        {
            identity.AddClaim(new Claim(ModulixClaimTypes.TenantId, id.ToString()));
        }

        // Aktif şirket yalnızca açık seçimle (ADR-0008 "ayrı bir uç") yazılır; yoksa varsayılan her istekte hesaplanır.
        if (companyId is { } company)
        {
            identity.AddClaim(new Claim(ModulixClaimTypes.CompanyId, company.ToString()));
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
            user.IsPlatformAdministrator,
            user.TwoFactorEnabled,
            PlatformRoles: [.. Modulix.Core.Platform.PlatformRoles.Split(Modulix.Core.Platform.PlatformRoles.Effective(user.IsPlatformAdministrator, user.PlatformRoles)).Select(r => r.ToString())]);
    }

    private static IResult InvalidLink(string message) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: message);

    private static IResult WeakPassword(AccountLinkResult result) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { ["password"] = [.. result.PasswordErrors] },
            title: "Şifre kurallara uymuyor.",
            statusCode: StatusCodes.Status422UnprocessableEntity);

    private static IResult Locked() =>
        Results.Problem(
            statusCode: StatusCodes.Status423Locked,
            title: "Hesap geçici olarak kilitlendi.",
            detail: "Çok sayıda hatalı deneme yapıldı. 15 dakika sonra tekrar deneyin.");

    // Kullanıcının var olup olmadığı belli edilmez.
    private static IResult InvalidCredentials() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "E-posta veya şifre hatalı.");
}
