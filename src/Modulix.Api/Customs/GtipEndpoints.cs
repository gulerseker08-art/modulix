using System.Security.Claims;
using Modulix.Api.Access;
using Modulix.Api.Imports;
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
        platform.MapGet("/versions/{id:guid}/entries", (Guid id, string? search, int? page, int? pageSize, string? sort, IGtipCatalogAdmin catalog, CancellationToken ct) =>
            Run(async () =>
            {
                // Başlığa göre sıralama (ADR-0036): yalnızca bilinen anahtarlar kabul edilir.
                if (!string.IsNullOrEmpty(sort) && !GtipEntrySort.Keys.Contains(sort))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]> { ["sort"] = ["Geçersiz sıralama."] }, statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                var result = await catalog.SearchEntriesAsync(id, search, page ?? 1, pageSize ?? 50, ct, string.IsNullOrEmpty(sort) ? null : sort);
                return Results.Ok(new GtipEntryPageDto([.. result.Items.Select(e => new GtipEntryDto(e.Code, e.Description, e.Unit))], result.TotalCount, result.Page, result.PageSize));
            }));

        // Dosya (Excel/CSV) gövdede ham bayttır; ad ve sürüm bilgisi sorgudadır. Önizleme hiçbir şey yazmaz (yolu /preview ile biter).
        platform.MapPost("/versions/preview", async (string? fileName, HttpRequest http, IGtipFileReader reader, CancellationToken ct) =>
        {
            var (content, problem) = await LimitedBody.ReadAsync(http, GtipImportRules.MaxFileBytes, ct);
            if (content is null)
            {
                return problem!;
            }

            var read = reader.Read(content, fileName);
            return Results.Ok(new GtipImportPreviewDto(
                read.FatalError, read.CanImport, read.RowsRead, read.Entries.Count, read.HeadingRows, read.PaddedCodes, read.ErrorCount,
                [.. read.Errors.Select(e => new GtipImportErrorDto(e.Row, e.Message))],
                [.. read.Entries.Take(10).Select(e => new GtipEntryDto(e.Code, e.Description, e.Unit))]));
        });
        platform.MapPost("/versions", async (string? fileName, string? name, DateOnly? effectiveFrom, ClaimsPrincipal user, HttpRequest http, IGtipFileReader reader, IGtipCatalogAdmin catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name) || effectiveFrom is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Sürüm adı ve yürürlük tarihi (yyyy-aa-gg) gerekli.");
            }

            var (content, problem) = await LimitedBody.ReadAsync(http, GtipImportRules.MaxFileBytes, ct);
            if (content is null)
            {
                return problem!;
            }

            // Hatalı dosya kısmen yüklenmez: eksik katalog sessizce yürürlüğe girmesin.
            var read = reader.Read(content, fileName);
            if (!read.CanImport)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: read.FatalError ?? (read.ErrorCount > 0 ? $"Dosyada {read.ErrorCount} hatalı satır var; önizlemeden düzeltip yeniden yükleyin." : "Dosyada içe aktarılacak GTİP satırı yok."));
            }

            return await Run(async () =>
            {
                var created = await catalog.CreateVersionAsync(PlatformEndpoints.UserId(user), new GtipCatalogVersionInput(name, effectiveFrom.Value, fileName), read.Entries, ct);
                return Results.Created($"/api/platform/gtip/versions/{created.Id}", ToDto(created));
            });
        });
        platform.MapDelete("/versions/{id:guid}",(Guid id, ClaimsPrincipal user, IGtipCatalogAdmin catalog, CancellationToken ct) =>
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
