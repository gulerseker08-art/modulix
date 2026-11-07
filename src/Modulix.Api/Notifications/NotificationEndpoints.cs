using Modulix.Api.Tenancy;
using Modulix.Core.Notifications;
using Modulix.Shared.Notifications;

namespace Modulix.Api.Notifications;

/// <summary>Kişinin bildirim kutusu (Ç5a). İzin gerekmez: herkes yalnızca kendi bildirimlerini görür; başkasının bildirimi "bulunamadı" döner.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/notifications").RequireAuthorization().AddEndpointFilter<RequireTenantFilter>().WithTags("Notifications");

        group.MapGet("/", async (int? take, INotificationCenter center, CancellationToken ct) =>
        {
            NotificationPage page = await center.ListAsync(take ?? 20, ct);
            return Results.Ok(new NotificationPageDto(
                [.. page.Items.Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Link, n.CreatedAt, n.ReadAt is not null))], page.UnreadCount));
        });

        group.MapGet("/unread-count", async (INotificationCenter center, CancellationToken ct) =>
            Results.Ok(new UnreadNotificationCountDto(await center.UnreadCountAsync(ct))));

        group.MapPost("/{id:guid}/read", async (Guid id, INotificationCenter center, CancellationToken ct) =>
            await center.MarkReadAsync(id, ct) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Bildirim bulunamadı."));

        group.MapPost("/read-all", async (INotificationCenter center, CancellationToken ct) =>
        {
            await center.MarkAllReadAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
