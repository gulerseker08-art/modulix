using System.Security.Claims;
using Modulix.Core.Platform;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Plan, abonelik ve limit yönetimi (ADR-0028 Dilim 4): /api/platform/plans ve /api/platform/tenants/{id}/plan. Yalnızca platform
/// yöneticisine açıktır. Hiçbir uç veri silmez: plan etkinlikle kapatılır, abonelik kapatılır, istisna geri alınır.
/// </summary>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlatformPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        platform.MapGet("/plans", async (IPlanAdministration plans, CancellationToken ct) =>
            new PlansResponse(
                [.. (await plans.ListPlansAsync(ct)).Select(ToDto)],
                [.. LimitKeys.All.Select(l => new LimitDefinitionDto(l.Key, l.Name))]));

        platform.MapPost("/plans", (SavePlanRequest request, ClaimsPrincipal user, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () => Results.Created("/api/platform/plans", ToDto(await plans.CreatePlanAsync(ToInput(request), PlatformEndpoints.UserId(user), ct)))));

        platform.MapPut("/plans/{planId:guid}", (Guid planId, SavePlanRequest request, ClaimsPrincipal user, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await plans.UpdatePlanAsync(planId, ToInput(request), PlatformEndpoints.UserId(user), ct)))));

        platform.MapGet("/tenants/{tenantId:guid}/plan", (Guid tenantId, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await plans.GetTenantPlanAsync(tenantId, ct)))));

        platform.MapPut("/tenants/{tenantId:guid}/plan", (Guid tenantId, AssignPlanRequest request, ClaimsPrincipal user, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () =>
            {
                await plans.AssignPlanAsync(tenantId, request.PlanKey, request.ValidUntil, request.Reason, PlatformEndpoints.UserId(user), ct);
                return Results.NoContent();
            }));

        platform.MapPut("/tenants/{tenantId:guid}/limits/{limitKey}", (Guid tenantId, string limitKey, SetLimitOverrideRequest request, ClaimsPrincipal user, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () =>
            {
                await plans.SetLimitOverrideAsync(tenantId, limitKey, request.Value, request.ValidUntil, request.Reason, PlatformEndpoints.UserId(user), ct);
                return Results.NoContent();
            }));

        platform.MapPost("/tenants/{tenantId:guid}/limits/{limitKey}/revoke", (Guid tenantId, string limitKey, RevokeLimitOverrideRequest request, ClaimsPrincipal user, IPlanAdministration plans, CancellationToken ct) =>
            Run(async () =>
            {
                await plans.RevokeLimitOverrideAsync(tenantId, limitKey, request.Reason, PlatformEndpoints.UserId(user), ct);
                return Results.NoContent();
            }));

        return app;
    }

    // PlatformEndpoints.Run gövdesiz eylem içindir; burada sonuç döndüren eylemler de gerekir.
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

    private static PlanInput ToInput(SavePlanRequest request) => new(request.Key, request.Name, request.Modules ?? [], request.Limits, request.IsActive);

    private static PlanDto ToDto(PlanInfo plan) => new(plan.Id, plan.Key, plan.Name, plan.IsActive, plan.Modules, plan.Limits, plan.SubscriberCount);

    private static TenantPlanResponse ToDto(TenantPlanOverview overview) => new(
        overview.Subscription is { } s ? new SubscriptionDto(s.PlanId, s.PlanKey, s.PlanName, s.StartsAt, s.ValidUntil) : null,
        [.. overview.Limits.Select(l => new TenantLimitDto(l.Key, l.Name, l.Limit, (LimitSourceDto)l.Source, l.OverrideUntil, l.Usage))]);
}
