using System.Text.Json.Nodes;
using Modulix.Api.Audit;
using Modulix.Api.Tenancy;
using Modulix.Core.Audit;
using Modulix.Core.Contracts.Records;
using Modulix.Shared.Records;

namespace Modulix.Api.Records;

/// <summary>
/// Genel kayıt uç noktaları. Her istek kayıt boru hattından geçer; uç nokta yalnızca HTTP'ye çevirir.
/// Metadata tabanlı form ve listeler (E6-E7) bu uç noktaları kullanır.
/// </summary>
public static class RecordEndpoints
{
    public const int HistoryPageSize = 20;

    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/records/{entityKey}")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Records");

        group.MapGet("", async (string entityKey, int? page, int? pageSize, string? search, HttpRequest http, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.QueryAsync(new RecordQuery(entityKey, page ?? 1, pageSize ?? 50, search, FiltersFrom(http)), ct);
            return result.IsSuccess
                ? Results.Ok(new RecordPageResponse(
                    result.Value!.Items.Select(ToResponse).ToList(), result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
                : ToProblem(result.Error!);
        });

        group.MapGet("/{id:guid}", async (string entityKey, Guid id, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.GetAsync(new RecordRef(entityKey, id), ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result.Error!);
        });

        // Kayıt geçmişi (Y5b): kaydı görebilen herkes görür; core.audit.read gerekmez. Kayıt önce boru hattından
        // okunur, böylece izin, modül ve kaydın varlığı aynı kurallarla denetlenir.
        group.MapGet("/{id:guid}/history", async (string entityKey, Guid id, int? page, IRecordPipeline pipeline, IAuditLog log, CancellationToken ct) =>
        {
            var record = new RecordRef(entityKey, id);
            var readable = await pipeline.GetAsync(record, ct);
            if (!readable.IsSuccess)
            {
                return ToProblem(readable.Error!);
            }

            var history = await log.GetRecordHistoryAsync(record, page ?? 1, HistoryPageSize, ct);
            return Results.Ok(AuditEndpoints.ToResponse(history));
        });

        group.MapPost("", async (string entityKey, RecordWriteRequest request, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.CreateAsync(new RecordCommand(entityKey, null, request.Values ?? new JsonObject()), ct);
            return result.IsSuccess
                ? Results.Created($"/api/records/{entityKey}/{result.Value!.Id}", ToResponse(result.Value))
                : ToProblem(result.Error!);
        });

        group.MapPut("/{id:guid}", async (string entityKey, Guid id, RecordWriteRequest request, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            if (request.Version is null)
            {
                return VersionRequired();
            }

            var result = await pipeline.UpdateAsync(
                new RecordCommand(entityKey, id, request.Values ?? new JsonObject(), request.Version), ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result.Error!);
        });

        group.MapDelete("/{id:guid}", async (string entityKey, Guid id, long? version, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            if (version is null)
            {
                return VersionRequired();
            }

            var result = await pipeline.DeleteAsync(new RecordRef(entityKey, id), version, ct);
            return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);
        });

        return app;
    }

    /// <summary>Sayfa ve arama dışındaki sorgu parametreleri filtredir (ör. ?companyId=...); boş değerler yok sayılır.</summary>
    internal static IReadOnlyDictionary<string, string>? FiltersFrom(HttpRequest http)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in http.Query)
        {
            if (key is not ("page" or "pageSize" or "search") && values.LastOrDefault() is { Length: > 0 } value)
            {
                filters[key] = value;
            }
        }

        return filters.Count == 0 ? null : filters;
    }

    /// <summary>Güncelleme ve silme kaydın okunan sürümünü ister; sürümsüz istek son yazanın ezmesine yol açacağı için 428 alır.</summary>
    internal static IResult VersionRequired() =>
        Results.Problem(
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "Kaydın sürümü gerekli.",
            detail: "Güncelleme ve silme isteğinde okuduğunuz kaydın version değeri gönderilmelidir.",
            extensions: new Dictionary<string, object?> { ["code"] = "VersionRequired" });

    private static RecordResponse ToResponse(RecordData data) => new(data.EntityKey, data.Id, data.Version, data.Values);

    internal static IResult ToProblem(RecordError error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code.ToString() };

        return error.Code switch
        {
            RecordErrorCode.ValidationFailed => Results.ValidationProblem(
                (error.FieldErrors ?? Array.Empty<RecordFieldError>())
                    .GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()),
                title: error.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: extensions),
            RecordErrorCode.NotFound or RecordErrorCode.UnknownEntity =>
                Results.Problem(statusCode: StatusCodes.Status404NotFound, title: error.Message, extensions: extensions),
            RecordErrorCode.Conflict =>
                Results.Problem(statusCode: StatusCodes.Status409Conflict, title: error.Message, extensions: extensions),
            _ => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: error.Message, extensions: extensions),
        };
    }
}
