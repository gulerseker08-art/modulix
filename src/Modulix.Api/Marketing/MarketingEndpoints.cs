using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Segments;
using Modulix.Shared.Marketing;

namespace Modulix.Api.Marketing;

/// <summary>
/// Pazarlama uç noktaları (/api/marketing; ADR-0025). Liste, detay, oluşturma ve düzenleme kayıt boru hattından geçer. Durum, yayın sürümü ve hesaplama özeti
/// yalnızca eylem uç noktalarıyla (yayımla, yenile, arşivle, geri al) değişir; genel PUT bunları yok sayar. Sürüm yalnızca yayımla ile oluşur.
/// Alanı görme yetkisi olmayan kişi o alanla önizleme yapamaz, sayı alamaz. Tenant her istekte oturumdan çözülür.
/// </summary>
public static class MarketingEndpoints
{
    private const string Prefix = "/api/marketing";

    public static IEndpointRouteBuilder MapMarketingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .RequireAuthorization()
            .RequireModule(MarketingModule.ModuleKey)
            .WithTags("Pazarlama");

        ResourceEndpoints.MapResource<SegmentDto, SaveSegmentRequest>(group, Prefix, "segments", MarketingModule.Entities.Segment,
            MarketingModule.Permissions.SegmentsRead, MarketingModule.Permissions.SegmentsWrite);
        ResourceEndpoints.MapResource<SegmentMemberDto, SaveSegmentMemberRequest>(group, Prefix, "segment-members", MarketingModule.Entities.SegmentMember,
            MarketingModule.Permissions.SegmentsRead, MarketingModule.Permissions.SegmentsWrite);
        ResourceEndpoints.MapResource<SegmentVersionDto, SaveSegmentVersionRequest>(group, Prefix, "segment-versions", MarketingModule.Entities.SegmentVersion,
            MarketingModule.Permissions.SegmentsRead, MarketingModule.Permissions.SegmentsPublish);

        // Segmentte kullanılabilir alanlar: çağıranın okuyabildikleri ve kullanamadıkları (nedeniyle).
        group.MapGet("/segment-fields", async (string target, ISegmentEngine engine, CancellationToken ct) =>
        {
            if (!TryTarget(target, out var parsed))
            {
                return InvalidTarget();
            }

            var catalog = await engine.CatalogAsync(parsed, ct);
            return Results.Ok(new SegmentFieldsResponse(
                [.. catalog.Fields.Select(f => new SegmentFieldDto(
                    f.Key, f.Label, f.Group, f.Target.ToString(), f.Type.ToString(),
                    [.. SegmentOperators.For(f.Type).Select(o => o.ToString().ToLowerInvariant())], f.Options, f.Hint))],
                [.. catalog.Unavailable.Select(u => new UnavailableSegmentFieldDto(u.Key, u.Label, u.Reason))]));
        }).RequirePermission(MarketingModule.Permissions.SegmentsRead);

        var segments = group.MapGroup("/segments");

        // Önizleme hiçbir şey yazmaz; sayı alan yetkisi ve veri kapsamıyla hesaplanır, satırlar alıcı görüntüleme yetkisine bağlıdır.
        segments.MapPost("/preview", async (PreviewSegmentRequest r, ISegmentService service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(r.Target ?? string.Empty, r.Condition?.ToJsonString(), r.Page, r.PageSize, ct);
            return Evaluation(result);
        }).RequirePermission(MarketingModule.Permissions.SegmentsRead);

        segments.MapGet("/{id:guid}/members", async (Guid id, int? page, int? pageSize, ISegmentService service, CancellationToken ct) =>
            Evaluation(await service.MembersAsync(id, page ?? 1, pageSize ?? 25, ct))).RequirePermission(MarketingModule.Permissions.SegmentsRead);

        segments.MapPost("/{id:guid}/publish", async (Guid id, SegmentActionRequest r, ISegmentService service, CancellationToken ct) =>
            Record(await service.PublishAsync(id, r.Version, ct))).RequirePermission(MarketingModule.Permissions.SegmentsPublish);
        segments.MapPost("/{id:guid}/refresh", async (Guid id, ISegmentService service, CancellationToken ct) =>
            Record(await service.RefreshAsync(id, ct))).RequirePermission(MarketingModule.Permissions.SegmentsPublish);
        segments.MapPost("/{id:guid}/archive", async (Guid id, SegmentActionRequest r, ISegmentService service, CancellationToken ct) =>
            Record(await service.ArchiveAsync(id, r.Version, ct))).RequirePermission(MarketingModule.Permissions.SegmentsPublish);
        segments.MapPost("/{id:guid}/restore", async (Guid id, SegmentActionRequest r, ISegmentService service, CancellationToken ct) =>
            Record(await service.RestoreAsync(id, r.Version, ct))).RequirePermission(MarketingModule.Permissions.SegmentsPublish);
        segments.MapPost("/{id:guid}/copy", async (Guid id, CopySegmentRequest r, ISegmentService service, CancellationToken ct) =>
        {
            var result = await service.CopyAsync(id, r.Code ?? string.Empty, r.Name ?? string.Empty, ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/segments/{result.Value!.Id}", ResourceEndpoints.ToDto<SegmentDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.SegmentsWrite);

        return app;
    }

    private static bool TryTarget(string? value, out SegmentTarget target)
    {
        target = default;
        return !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value.Trim()[0])
            && Enum.TryParse(value.Trim(), ignoreCase: true, out target) && Enum.IsDefined(target);
    }

    private static IResult InvalidTarget() =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { ["target"] = ["Geçersiz hedef türü (Company, Person, Lead)."] },
            title: "Doğrulama hatası.",
            statusCode: StatusCodes.Status422UnprocessableEntity);

    private static IResult Record(RecordResult<RecordData> result) =>
        result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<SegmentDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static IResult Evaluation(RecordResult<SegmentEvaluation> result) =>
        result.IsSuccess
            ? Results.Ok(new SegmentEvaluationDto(
                result.Value!.Count, [.. result.Value.Rows.Select(r => new SegmentRowDto(r.Id, r.Label))], result.Value.RowsHidden, result.Value.DurationMs))
            : RecordEndpoints.ToProblem(result.Error!);
}
