using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using QRCoder;
using Modulix.Api.Auth;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Users;
using Modulix.Shared.Account;

namespace Modulix.Api.Account;

/// <summary>
/// Kişinin kendi hesabı (Y7a): /api/account. Oturum ister; firma ve izin istemez (bilgiler kişiye aittir). Alan
/// hataları 400 ve alan adıyla döner (<see cref="UserEndpoints.ToProblem"/>).
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account").RequireAuthorization().WithTags("Account");

        account.MapGet("/", GetAsync);
        account.MapPut("/profile", UpdateProfileAsync);
        account.MapPost("/password", ChangePasswordAsync);
        account.MapPost("/email", ChangeEmailAsync);
        account.MapGet("/sessions", ListSessionsAsync);
        account.MapDelete("/sessions/{sessionId:guid}", CloseSessionAsync);
        account.MapPost("/sessions/close-others", CloseOtherSessionsAsync);

        // İki adımlı doğrulama (Y7b).
        account.MapGet("/two-factor", GetTwoFactorAsync);
        account.MapPost("/two-factor/setup", StartTwoFactorAsync);
        account.MapPost("/two-factor/enable", EnableTwoFactorAsync);
        account.MapPost("/two-factor/disable", DisableTwoFactorAsync);
        account.MapPost("/two-factor/recovery-codes", RegenerateRecoveryCodesAsync);

        return app;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal principal, UserManager<ModulixUser> users) =>
        await users.GetUserAsync(principal) is { } user
            ? Results.Ok(new AccountProfileResponse(user.DisplayName, user.Email ?? string.Empty))
            : Results.Unauthorized();

    private static Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request, ClaimsPrincipal principal, HttpContext http, UserManager<ModulixUser> users, AccountSecurity account,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            await account.UpdateDisplayNameAsync(user, request.DisplayName, AuthEndpoints.Client(http), ct);
            return Results.Ok(new AccountProfileResponse(user.DisplayName, user.Email ?? string.Empty));
        });

    /// <summary>Şifre değişince güvenlik damgası yenilenir; bu oturumun çerezi aynı oturumla yeniden yazılır, diğerleri kapanır.</summary>
    private static Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        HttpTenantContextAccessor tenant,
        AccountSecurity account,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            var sessionId = AuthEndpoints.SessionId(principal)!.Value;
            await account.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword, sessionId, AuthEndpoints.Client(http), ct);
            await AuthEndpoints.IssueSessionAsync(http, signIn, user, tenant.Current?.TenantId ?? tenant.TwoFactorRequiredTenantId, sessionId);
            return Results.NoContent();
        });

    private static Task<IResult> ChangeEmailAsync(
        ChangeEmailRequest request, ClaimsPrincipal principal, HttpContext http, UserManager<ModulixUser> users, AccountSecurity account,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            await account.RequestEmailChangeAsync(user, request.Email, request.CurrentPassword, PublicUrl.Resolve(http), ct);
            return Results.Accepted();
        });

    private static async Task<IResult> ListSessionsAsync(ClaimsPrincipal principal, UserManager<ModulixUser> users, UserSessions sessions, CancellationToken ct)
    {
        if (!Guid.TryParse(users.GetUserId(principal), out var userId))
        {
            return Results.Unauthorized();
        }

        var current = AuthEndpoints.SessionId(principal);
        var open = await sessions.ListAsync(userId, ct);
        return Results.Ok(open
            .Select(s => new SessionDto(s.Id, s.Device, s.IpAddress, s.CreatedAt, s.LastSeenAt, s.Id == current))
            .OrderByDescending(s => s.IsCurrent)
            .ToList());
    }

    private static Task<IResult> CloseSessionAsync(
        Guid sessionId, ClaimsPrincipal principal, HttpContext http, UserManager<ModulixUser> users, UserSessions sessions, AccountAudit audit,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            if (!await sessions.CloseAsync(user.Id, sessionId, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Oturum bulunamadı veya zaten kapalı.");
            }

            await RecordClosedAsync(audit, user, http, 1, ct);
            return Results.NoContent();
        });

    private static Task<IResult> CloseOtherSessionsAsync(
        ClaimsPrincipal principal, HttpContext http, UserManager<ModulixUser> users, UserSessions sessions, AccountAudit audit, CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            var closed = await sessions.CloseAllAsync(user.Id, AuthEndpoints.SessionId(principal), ct);
            if (closed > 0)
            {
                await RecordClosedAsync(audit, user, http, closed, ct);
            }

            return Results.Ok(new CloseSessionsResponse(closed));
        });

    private static Task<IResult> GetTwoFactorAsync(
        ClaimsPrincipal principal, UserManager<ModulixUser> users, TwoFactorAccounts twoFactor, CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            var status = await twoFactor.GetStatusAsync(user);
            return Results.Ok(new TwoFactorStatusResponse(status.Enabled, status.RecoveryCodesLeft, await twoFactor.RequiringTenantsAsync(user.Id, ct)));
        });

    private static Task<IResult> StartTwoFactorAsync(
        StartTwoFactorRequest request, ClaimsPrincipal principal, UserManager<ModulixUser> users, TwoFactorAccounts twoFactor) =>
        RunAsync(principal, users, async user =>
        {
            var setup = await twoFactor.BeginSetupAsync(user, request.CurrentPassword);
            using var data = QRCodeGenerator.GenerateQrCode(setup.Uri, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(5);
            return Results.Ok(new TwoFactorSetupResponse(setup.SharedKey, $"data:image/png;base64,{Convert.ToBase64String(png)}"));
        });

    /// <summary>Açma ve kapatma güvenlik damgasını yeniler; bu oturumun çerezi aynı oturumla yeniden yazılır.</summary>
    private static Task<IResult> EnableTwoFactorAsync(
        EnableTwoFactorRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        HttpTenantContextAccessor tenant,
        TwoFactorAccounts twoFactor,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            var codes = await twoFactor.EnableAsync(user, request.Code, AuthEndpoints.Client(http), ct);
            await ReissueAsync(http, signIn, user, principal, tenant);
            return Results.Ok(new RecoveryCodesResponse(codes));
        });

    private static Task<IResult> DisableTwoFactorAsync(
        DisableTwoFactorRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        UserManager<ModulixUser> users,
        SignInManager<ModulixUser> signIn,
        HttpTenantContextAccessor tenant,
        TwoFactorAccounts twoFactor,
        CancellationToken ct) =>
        RunAsync(principal, users, async user =>
        {
            await twoFactor.DisableAsync(user, request.CurrentPassword, request.Code, AuthEndpoints.Client(http), ct);
            await ReissueAsync(http, signIn, user, principal, tenant);
            return Results.NoContent();
        });

    private static Task<IResult> RegenerateRecoveryCodesAsync(
        RegenerateRecoveryCodesRequest request, ClaimsPrincipal principal, HttpContext http, UserManager<ModulixUser> users,
        TwoFactorAccounts twoFactor, CancellationToken ct) =>
        RunAsync(principal, users, async user =>
            Results.Ok(new RecoveryCodesResponse(await twoFactor.RegenerateRecoveryCodesAsync(user, request.CurrentPassword, AuthEndpoints.Client(http), ct))));

    private static Task ReissueAsync(
        HttpContext http, SignInManager<ModulixUser> signIn, ModulixUser user, ClaimsPrincipal principal, HttpTenantContextAccessor tenant) =>
        AuthEndpoints.IssueSessionAsync(
            http, signIn, user, tenant.Current?.TenantId ?? tenant.TwoFactorRequiredTenantId, AuthEndpoints.SessionId(principal)!.Value);

    private static Task RecordClosedAsync(AccountAudit audit, ModulixUser user, HttpContext http, int closed, CancellationToken ct) =>
        audit.RecordAsync(user, AuditAction.SessionsClosed, null, AuthEndpoints.Client(http), ct,
            details: new System.Text.Json.Nodes.JsonObject { ["sessions"] = closed });

    private static async Task<IResult> RunAsync(ClaimsPrincipal principal, UserManager<ModulixUser> users, Func<ModulixUser, Task<IResult>> action)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return Results.Unauthorized();
        }

        try
        {
            return await action(user);
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }
}
