using System.Security.Claims;
using Modulix.Core.Events;
using Modulix.Shared.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// Olaylar (ADR-0007): /api/platform/events. Yalnızca platform yöneticisine açıktır. Bekleyen, bekletilen ve başarısız
/// teslimleri listeler; yeniden dener veya atlar (teslimin firmasının denetim kaydına yazılır). Payload gösterilmez.
/// </summary>
public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/platform/events")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        events.MapGet("/summary", async (IEventAdministration admin, CancellationToken ct) =>
        {
            var summary = await admin.GetSummaryAsync(ct);
            return new EventSummaryResponse(
                summary.Pending, summary.Held, summary.Failed, summary.OldestUndispatched, summary.OldestPending, summary.Subscriptions, summary.EventTypes);
        });

        events.MapGet("/deliveries", async (
            Guid? tenantId, string? subscription, string? eventType, EventDeliveryStateDto? state, DateOnly? from, DateOnly? to,
            int? page, int? pageSize, string? sort, IEventAdministration admin, CancellationToken ct) =>
        {
            // Başlığa göre sıralama (ADR-0036): yalnızca bilinen anahtarlar kabul edilir.
            if (!string.IsNullOrEmpty(sort) && !EventDeliveryQuery.SortKeys.Contains(sort))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["sort"] = ["Geçersiz sıralama."] }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var query = Query(new EventDeliveryFilterDto(tenantId, subscription, eventType, state, from, to)) with
            {
                Page = page ?? 1,
                PageSize = pageSize ?? 50,
                Sort = string.IsNullOrEmpty(sort) ? null : sort,
            };
            var result = await admin.ListAsync(query, ct);
            return Results.Ok(new EventDeliveryPageResponse([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        });

        events.MapPost("/deliveries/retry", (EventDeliveryActionRequest request, ClaimsPrincipal user, IEventAdministration admin, CancellationToken ct) =>
            RunAsync(request, () => admin.RetryAsync(request.Ids, Filter(request), PlatformEndpoints.UserId(user), ct)));
        events.MapPost("/deliveries/skip", (EventDeliveryActionRequest request, ClaimsPrincipal user, IEventAdministration admin, CancellationToken ct) =>
            RunAsync(request, () => admin.SkipAsync(request.Ids, Filter(request), PlatformEndpoints.UserId(user), ct)));

        return app;
    }

    private static async Task<IResult> RunAsync(EventDeliveryActionRequest request, Func<Task<int>> action) =>
        request.Ids is not { Count: > 0 } && request.Filter is null
            ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Teslim seçin veya süzgeç verin.")
            : Results.Ok(new EventDeliveryActionResponse(await action()));

    private static EventDeliveryQuery? Filter(EventDeliveryActionRequest request) =>
        request.Ids is { Count: > 0 } || request.Filter is null ? null : Query(request.Filter);

    // Günler UTC'dir; bitiş günü dahil.
    private static EventDeliveryQuery Query(EventDeliveryFilterDto filter) => new(
        filter.TenantId,
        string.IsNullOrWhiteSpace(filter.Subscription) ? null : filter.Subscription.Trim(),
        string.IsNullOrWhiteSpace(filter.EventType) ? null : filter.EventType.Trim(),
        (EventDeliveryState?)filter.State,
        filter.From is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        filter.To is { } to ? new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null);

    private static EventDeliveryDto ToDto(EventDeliveryItem item) => new(
        item.Id,
        item.EventId,
        item.TenantId,
        item.TenantName,
        item.Subscription,
        item.EventType,
        item.EntityKey,
        item.RecordId,
        item.Sequence,
        (EventDeliveryStateDto)item.State,
        item.AttemptCount,
        item.CreatedAt,
        item.NextAttemptAt,
        item.CompletedAt,
        item.LastError,
        item.HoldingBack);
}
