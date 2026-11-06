using System.Security.Claims;
using Modulix.Api.Access;
using Modulix.Api.Platform;
using Modulix.Api.Users;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;
using Modulix.Shared.Customs;
using Modulix.Shared.Warehouse;

namespace Modulix.Api.Customs;

/// <summary>
/// GTİP kataloğu (ADR-0032 K9). Yönetim platform yöneticisine açıktır (<c>/api/platform/gtip</c>, tenant gerekmez); okuma depo modülünü
/// kullanan tenant'a açıktır (<c>/api/gtip</c>): katalog ürün standardı verisidir, tenant verisi değildir. Katalogda olmayan kod yalnızca
/// uyarıdır, hiçbir kaydı engellemez.
/// </summary>
public static class GtipEndpoints
{
    public static IEndpointRouteBuilder MapGtipEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform/gtip")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");
        platform.MapGet("/versions", async (IGtipCatalogAdmin catalog, CancellationToken ct) =>
            (await catalog.ListVersionsAsync(ct)).Select(ToDto).ToList());
        platform.MapGet("/versions/{id:guid}/entries", (Guid id, string? search, int? page, int? pageSize, IGtipCatalogAdmin catalog, CancellationToken ct) =>
            Run(async () =>
            {
                var result = await catalog.SearchEntriesAsync(id, search, page ?? 1, pageSize ?? 50, ct);
                return Results.Ok(new GtipEntryPageDto([.. result.Items.Select(e => new GtipEntryDto(e.Code, e.Description, e.Unit))], result.TotalCount, result.Page, result.PageSize));
            }));
        platform.MapDelete("/versions/{id:guid}", (Guid id, ClaimsPrincipal user, IGtipCatalogAdmin catalog, CancellationToken ct) =>
            Run(async () =>
            {
                await catalog.DeleteVersionAsync(PlatformEndpoints.UserId(user), id, ct);
                return Results.NoContent();
            }));

        // Stok kartı formu yazarken kodu kontrol eder: depo modülü açık olmalı ve stok kartı okuma izni gerekir.
        var read = app.MapGroup("/api/gtip")
            .RequireAuthorization()
            .RequireModule(WarehouseAccess.ModuleKey)
            .RequirePermission(WarehouseAccess.StockItemsRead)
            .WithTags("Warehouse");
        read.MapGet("/check", async (string? code, DateOnly? date, IGtipCatalog catalog, TimeProvider time, CancellationToken ct) =>
        {
            var (value, error) = GtipRules.Normalize(code);
            if (value is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error ?? "GTİP kodu boş olamaz.");
            }

            var check = await catalog.CheckAsync(value, date ?? Today(time), ct);
            return Results.Ok(new GtipCheckDto(check.Status.ToString(), check.Description, check.VersionName));
        });
        read.MapGet("/search", async (string? q, DateOnly? date, int? limit, IGtipCatalog catalog, TimeProvider time, CancellationToken ct) =>
            (await catalog.SearchAsync(q ?? string.Empty, date ?? Today(time), limit ?? 10, ct))
                .Select(s => new GtipEntryDto(s.Code, s.Description, s.Unit)).ToList());

        return app;
    }

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (GtipCatalogNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (GtipCatalogConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return UserEndpoints.ToProblem(ex)!;
        }
    }

    private static GtipVersionDto ToDto(GtipCatalogVersionView v) => new(v.Id, v.Name, v.EffectiveFrom, v.SourceFileName, v.EntryCount, v.CreatedAt);
}
