using Modulix.Api.Access;
using Modulix.Modules.Warehouse.Dashboard;
using Modulix.Shared.Warehouse;

namespace Modulix.Api.Warehouse;

/// <summary>
/// Depo ve antrepo panosu (<c>GET /api/warehouse/dashboard</c>). Panoyu açmak tesis okuma iznine bağlıdır; diğer bölümler kendi okuma izinleriyle sunucuda
/// süzülür (izni olmayana bölüm boş gelir). Tesis başka tenant'ınsa ya da yoksa 404; dönem yalnızca 7, 14 ya da 30 gündür.
/// </summary>
public static class WarehouseDashboardEndpoints
{
    public static IEndpointRouteBuilder MapWarehouseDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/warehouse")
            .RequireAuthorization()
            .RequireModule(WarehouseAccess.ModuleKey)
            .WithTags("Warehouse");

        group.MapGet("/dashboard", async (Guid? facilityId, int? days, IWarehouseDashboard dashboard, CancellationToken ct) =>
            {
                var period = days ?? 14;
                if (!WarehouseDashboardRules.AllowedDays.Contains(period))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["days"] = ["Dönem 7, 14 ya da 30 gün olabilir."] },
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                var v = await dashboard.GetAsync(facilityId, period, ct);
                return v is null ? Results.NotFound() : Results.Ok(Map(v));
            })
            .RequirePermission(WarehouseAccess.FacilitiesRead);

        return app;
    }

    private static WarehouseDashboardDto Map(WarehouseDashboardView v) => new(
        v.Today,
        v.Days,
        v.FacilityId,
        v.Facilities is { } f ? new WarehouseFacilityCountsDto(f.Active, f.CustomsEnabled) : null,
        v.Occupancy is { } o
            ? new WarehouseOccupancyDto(o.Locations, o.Occupied, o.PassiveWithStock,
                [.. o.ByFacility.Select(x => new WarehouseFacilityOccupancyDto(x.FacilityId, x.Code, x.Name, x.CustomsEnabled, x.Locations, x.Occupied))])
            : null,
        v.StockItems is { } s ? new WarehouseStockItemCountsDto(s.Active, s.NewInPeriod) : null,
        v.Declarations is { } d
            ? new WarehouseDeclarationSummaryDto(
                [.. d.ByStatus.Select(x => new WarehouseDeclarationStatusCountDto(x.Key, x.Label, x.Category, x.Count))],
                d.StaleDrafts, WarehouseDashboardRules.StaleDraftDays,
                [.. d.StaleSample.Select(x => new WarehouseStaleDeclarationDto(x.Id, x.RegistrationNo, x.CreatedAt))])
            : null,
        v.Movements is { } m
            ? new WarehouseMovementSummaryDto(
                [.. m.Daily.Select(x => new WarehouseDailyMovementDto(x.Day, x.Inbound, x.Outbound, x.Transfer, x.Adjustment))],
                m.Inbound, m.Outbound, m.Transfer,
                [.. m.Recent.Select(x => new WarehouseRecentMovementDto(x.Id, x.TypeKey, x.TypeLabel, x.Category, x.Quantity, x.OccurredAt,
                    x.StockItemCode, x.StockItemName, x.LotNo, x.FromLocation, x.ToLocation))])
            : null,
        v.Attorneys is { } a ? new WarehouseAttorneyAlertDto(a.Expiring, WarehouseDashboardRules.ExpiryWindowDays, a.Nearest) : null);
}
