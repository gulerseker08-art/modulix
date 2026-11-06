using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Core.Views;
using Modulix.Shared.Views;

namespace Modulix.Api.Views;

/// <summary>
/// Kişisel liste görünümleri (ADR-0036): <c>/api/grid-views</c>. Her üye yalnızca kendi görünümlerini görür ve değiştirir; ek izin istemez
/// (görünüm yetki vermez, liste uç noktası yetkiyi ve kapsamı her istekte uygular). Tenant yalnızca oturumdan gelir.
/// </summary>
public static class GridViewEndpoints
{
    public static IEndpointRouteBuilder MapGridViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/grid-views")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Views");

        group.MapGet("/", async (string screen, IGridViewService service, CancellationToken ct) =>
            (await service.ListAsync(screen, ct)).Select(ToDto).ToList());

        group.MapPost("/", async (SaveGridViewRequest request, IGridViewService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(ToInput(request), ct);
            return result.IsSuccess
                ? Results.Created($"/api/grid-views/{result.Value!.Id}", ToDto(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPut("/{id:guid}", async (Guid id, SaveGridViewRequest request, IGridViewService service, CancellationToken ct) =>
        {
            if (request.Version is not { } version)
            {
                return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
            }

            var result = await service.UpdateAsync(id, ToInput(request), version, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapDelete("/{id:guid}", async (Guid id, long version, IGridViewService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(id, version, ct);
            return result.IsSuccess ? Results.NoContent() : RecordEndpoints.ToProblem(result.Error!);
        });

        return app;
    }

    private static GridViewInput ToInput(SaveGridViewRequest request) =>
        new(request.ScreenKey ?? string.Empty, request.Name ?? string.Empty, request.Columns ?? [], request.Sort, request.Filters);

    private static GridViewDto ToDto(GridViewInfo info) =>
        new(info.Id, info.Version, info.ScreenKey, info.Name, info.Columns, info.Sort, info.Filters);
}
