using System.Security.Claims;
using Modulix.Core.Platform;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Ödeme kayıtları (ADR-0028 Dilim 7c): /api/platform/billing ve /api/platform/tenants/{id}/billing. Yalnızca platform yöneticisine
/// açıktır (yazma Finans rolü, <see cref="PlatformAccessRules"/>). Sağlayıcıdan bağımsız defterdir; erişimi değiştirmez, hiçbir kayıt silmez.
/// </summary>
public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapPlatformBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        platform.MapGet("/billing", async (Guid? tenantId, BillingStatusDto? status, IBillingLedger ledger, CancellationToken ct) =>
            (await ledger.ListAsync(tenantId, status is { } s ? (BillingStatus)s : null, ct)).Select(ToDto).ToList());

        platform.MapPost("/tenants/{tenantId:guid}/billing", (Guid tenantId, CreateBillingRequest request, ClaimsPrincipal user, IBillingLedger ledger, CancellationToken ct) =>
            Run(async () => Results.Created(
                "/api/platform/billing",
                ToDto(await ledger.CreateAsync(
                    tenantId,
                    new BillingInput(request.PeriodStart, request.PeriodEnd, request.Amount, request.Currency, request.DueDate, request.Reference, request.Note),
                    PlatformEndpoints.UserId(user),
                    ct)))));

        platform.MapPost("/billing/{id:guid}/paid", (Guid id, MarkBillingPaidRequest request, ClaimsPrincipal user, IBillingLedger ledger, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await ledger.MarkPaidAsync(id, request.PaidAt, request.Reference, PlatformEndpoints.UserId(user), ct)))));

        platform.MapPost("/billing/{id:guid}/cancel", (Guid id, CancelBillingRequest request, ClaimsPrincipal user, IBillingLedger ledger, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await ledger.CancelAsync(id, request.Reason, PlatformEndpoints.UserId(user), ct)))));

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
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

    private static BillingRecordDto ToDto(BillingInfo b) => new(
        b.Id, b.TenantId, b.TenantName, b.PeriodStart, b.PeriodEnd, b.Amount, b.Currency, b.DueDate, b.Reference, b.Note,
        (BillingStatusDto)b.Status, b.PaidAt, b.CancelReason, b.CreatedAt, b.IsOverdue);
}
