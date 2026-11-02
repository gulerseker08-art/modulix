using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Responses;
using Modulix.Shared.Marketing;
using Modulix.Shared.Records;

namespace Modulix.Api.Marketing;

/// <summary>
/// Kampanya yanıtı uç noktaları (ADR-0025, dilim 7). Yanıt kaydı ve durum değişikliği yalnızca bu uçlardan yapılır (genel PUT/DELETE yoktur): her eylem durum makinesinden geçer,
/// lead ve aktivite çağıranın CRM yetkisiyle açılır, "iletişim istemiyorum" yanıtının çıkışı çekirdeğin tercih kaydına yazılır. Okuma yanıtın sorumlusuna bağlı veri kapsamından geçer.
/// </summary>
internal static class ResponseEndpoints
{
    public static void MapResponseEndpoints(RouteGroupBuilder group)
    {
        var responses = group.MapGroup("/campaign-responses");

        responses.MapGet("", async (int? page, int? pageSize, string? search, HttpRequest http, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.QueryAsync(new RecordQuery(MarketingModule.Entities.CampaignResponse, page ?? 1, pageSize ?? 50, search, RecordEndpoints.FiltersFrom(http)), ct);
            return result.IsSuccess
                ? Results.Ok(new PagedResult<CampaignResponseDto>([.. result.Value!.Items.Select(ResourceEndpoints.ToDto<CampaignResponseDto>)], result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.ResponsesRead);

        responses.MapGet("/{id:guid}", async (Guid id, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignResponse, id), ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<CampaignResponseDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.ResponsesRead);

        responses.MapPost("", async (RegisterCampaignResponseRequest request, ICampaignResponses service, CancellationToken ct) =>
        {
            var result = await service.RegisterAsync(new RegisterResponseRequest(request.DeliveryId, request.Kind, request.Note, request.ReceivedAt), ct);
            return result.IsSuccess
                ? Results.Created($"/api/marketing/campaign-responses/{result.Value!.Id}", ResourceEndpoints.ToDto<CampaignResponseDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.ResponsesWrite);

        foreach (var action in ResponseActions.All)
        {
            responses.MapPost($"/{{id:guid}}/{action}", async (Guid id, ResponseActionRequest? r, ICampaignResponses service, CancellationToken ct) =>
            {
                var result = await service.ActAsync(id, action, r?.Reason, r?.Version, ct);
                return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<CampaignResponseDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(MarketingModule.Permissions.ResponsesWrite);
        }
    }
}
