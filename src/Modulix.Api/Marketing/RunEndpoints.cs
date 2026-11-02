using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Runs;
using Modulix.Shared.Marketing;

namespace Modulix.Api.Marketing;

/// <summary>
/// Kampanya içeriği ve çalıştırma uç noktaları (ADR-0025, dilim 5). İçerik sürümleri değişmez. Çalıştırma durumu yalnızca eylem uç noktalarıyla değişir
/// (gönder, onayla, reddet, taslağa al, başlat, duraklat, sürdür, iptal); her eylem kendi iznini ister ve görev ayrılığı uygular. Başlatma ve işleme,
/// sağlayıcı yapılandırılmamışsa bloklanır; hiçbir yol izin, engel ve sıklık denetimini atlayamaz. Teslimat satırları adres içermez.
/// </summary>
internal static class RunEndpoints
{
    private const string Prefix = "/api/marketing";

    private static readonly string[] Actions = [RunActions.Submit, RunActions.Approve, RunActions.Reject, RunActions.Reopen, RunActions.Start, RunActions.Pause, RunActions.Resume, RunActions.Cancel];

    public static void MapRunEndpoints(RouteGroupBuilder group)
    {
        ResourceEndpoints.MapResource<CampaignContentDto, SaveCampaignContentRequest>(group, Prefix, "campaign-contents", MarketingModule.Entities.CampaignContent,
            MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsWrite);
        ResourceEndpoints.MapResource<CampaignRunDto, SaveCampaignRunRequest>(group, Prefix, "campaign-runs", MarketingModule.Entities.CampaignRun,
            MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsWrite);

        var runs = group.MapGroup("/campaign-runs");
        foreach (var action in Actions)
        {
            runs.MapPost($"/{{id:guid}}/{action}", async (Guid id, RunActionRequest? r, ICampaignRuns service, CancellationToken ct) =>
            {
                var result = await service.ActAsync(id, action, r?.Reason, r?.Version, ct);
                return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<CampaignRunDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(MarketingModule.Permissions.CampaignsRead);
        }

        runs.MapPost("/{id:guid}/process", async (Guid id, int? batchSize, ICampaignRuns service, CancellationToken ct) =>
        {
            var result = await service.ProcessAsync(id, batchSize ?? 50, ct);
            return result.IsSuccess
                ? Results.Ok(new ProcessResultDto(
                    result.Value!.Processed, result.Value.Accepted, result.Value.Failed, result.Value.Skipped, result.Value.Unknown, result.Value.Requeued,
                    result.Value.Remaining, result.Value.Completed))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsRead);

        runs.MapGet("/{id:guid}/summary", async (Guid id, ICampaignRuns service, CancellationToken ct) =>
        {
            var result = await service.SummaryAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(new RunSummaryDto(result.Value!.Status, result.Value.Total, result.Value.Statuses, result.Value.Codes, result.Value.ProviderConfigured, result.Value.Provider))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsRead);

        runs.MapGet("/{id:guid}/deliveries", async (Guid id, string? status, string? code, int? page, int? pageSize, ICampaignRuns service, CancellationToken ct) =>
        {
            var result = await service.DeliveriesAsync(id, status, code, page ?? 1, pageSize ?? 25, ct);
            return result.IsSuccess
                ? Results.Ok(new CampaignDeliveryPageDto(
                    [.. result.Value!.Items.Select(d => new CampaignDeliveryDto(d.Id, d.TargetId, d.PersonId, d.Status, d.Code, d.Attempts, d.ProviderMessageId, d.UpdatedAt))],
                    result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsRead);
    }
}
