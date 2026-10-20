using System.Text.Json.Nodes;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Records;
using Modulix.Shared.Records;

namespace Modulix.Api.Records;

/// <summary>
/// Genel kayıt uç noktaları. Her istek kayıt boru hattından geçer; uç nokta yalnızca HTTP'ye çevirir.
/// Metadata tabanlı form ve listeler (E6-E7) bu uç noktaları kullanır.
/// </summary>
public static class RecordEndpoints
{
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/records/{entityKey}")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Records");

        group.MapGet("", async (string entityKey, int? page, int? pageSize, string? search, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.QueryAsync(new RecordQuery(entityKey, page ?? 1, pageSize ?? 50, search), ct);
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

        group.MapPost("", async (string entityKey, RecordWriteRequest request, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.CreateAsync(new RecordCommand(entityKey, null, request.Values ?? new JsonObject()), ct);
            return result.IsSuccess
                ? Results.Created($"/api/records/{entityKey}/{result.Value!.Id}", ToResponse(result.Value))
                : ToProblem(result.Error!);
        });

        group.MapPut("/{id:guid}", async (string entityKey, Guid id, RecordWriteRequest request, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.UpdateAsync(
                new RecordCommand(entityKey, id, request.Values ?? new JsonObject(), request.Version), ct);
            return result.IsSuccess ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result.Error!);
        });

        group.MapDelete("/{id:guid}", async (string entityKey, Guid id, long? version, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.DeleteAsync(new RecordRef(entityKey, id), version, ct);
            return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);
        });

        return app;
    }

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
