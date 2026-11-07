using System.Security.Claims;
using Modulix.Api.Access;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Destek erişimi (ADR-0028 Dilim 7, Y1.3). Platform tarafı: <c>/api/platform/tenants/{id}/support-access</c> (aç),
/// <c>/api/platform/support-access</c> (liste) ve <c>.../{grantId}/revoke</c> (kapat); rol tablosu <see cref="PlatformAccessRules"/>.
/// Firma tarafı: <c>/api/tenant/support-access</c> (firma yöneticisi kendi firmasındaki erişimleri görür ve kapatır). Erişim her zaman
/// gerekçeli, süreli, salt okunur ve tek firmayla sınırlıdır; süresi dolan erişim her istekte reddedilir.
/// </summary>
public static class SupportAccessEndpoints
{
    public static IEndpointRouteBuilder MapSupportAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        platform.MapPost("/tenants/{tenantId:guid}/support-access", (Guid tenantId, GrantSupportAccessRequest request, ClaimsPrincipal user, ISupportAccess access, CancellationToken ct) =>
            RunAsync(async () => Results.Created(
                "/api/platform/support-access",
                ToDto(await access.GrantAsync(tenantId, PlatformEndpoints.UserId(user), request.Reason, request.Minutes ?? SupportAccessLimits.DefaultMinutes, ct)))));

        platform.MapGet("/support-access", async (Guid? tenantId, bool? activeOnly, ISupportAccess access, CancellationToken ct) =>
            (await access.ListAsync(tenantId, activeOnly ?? false, ct)).Select(ToDto).ToList());

        platform.MapPost("/support-access/{grantId:guid}/revoke", (Guid grantId, string? reason, ClaimsPrincipal user, ISupportAccess access, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await access.RevokeAsync(grantId, null, PlatformEndpoints.UserId(user), reason, ct);
                return Results.NoContent();
            }));

        platform.MapGet("/support-access/{grantId:guid}/reads", (Guid grantId, ISupportAccess access, CancellationToken ct) =>
            RunAsync(async () => Results.Ok((await access.ListReadsAsync(grantId, null, 200, ct)).Select(ToDto).ToList())));

        // Firma tarafı: firma yöneticisi (kullanıcı yönetimi izni) kendi firmasının erişimlerini görür ve kapatır; başka firmanınki "yok" sayılır.
        var tenant = app.MapGroup("/api/tenant/support-access")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("Tenant");

        tenant.MapGet("", async (Modulix.Core.Contracts.Tenancy.ITenantContextAccessor current, ISupportAccess access, CancellationToken ct) =>
            (await access.ListAsync(current.Current!.TenantId, false, ct)).Select(ToDto).ToList());

        tenant.MapPost("/{grantId:guid}/revoke", (Guid grantId, string? reason, Modulix.Core.Contracts.Tenancy.ITenantContextAccessor current, ISupportAccess access, CancellationToken ct) =>
            RunAsync(async () =>
            {
                await access.RevokeAsync(grantId, current.Current!.TenantId, current.Current.UserId, reason ?? "Firma yöneticisi kapattı", ct);
                return Results.NoContent();
            }));

        tenant.MapGet("/{grantId:guid}/reads", (Guid grantId, Modulix.Core.Contracts.Tenancy.ITenantContextAccessor current, ISupportAccess access, CancellationToken ct) =>
            RunAsync(async () => Results.Ok((await access.ListReadsAsync(grantId, current.Current!.TenantId, 200, ct)).Select(ToDto).ToList())));

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (PlatformEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static SupportAccessReadDto ToDto(SupportAccessReadInfo info) => new(info.At, info.Method, info.Path);

    private static SupportAccessDto ToDto(SupportAccessInfo info) => new(
        info.Id, info.TenantId, info.TenantName, info.UserId, info.UserName, info.Reason, info.GrantedAt, info.ExpiresAt, info.EndedAt, (SupportAccessStateDto)info.State);
}
