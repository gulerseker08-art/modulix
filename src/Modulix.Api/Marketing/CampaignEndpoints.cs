using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Audiences;
using Modulix.Shared.Marketing;

namespace Modulix.Api.Marketing;

/// <summary>
/// Kampanya uç noktaları (/api/marketing/campaigns...; ADR-0025, dilim 4). Kampanya, hedef kitle girdisi ve dondurulmuş kitle kayıt boru hattından geçer.
/// Önizleme hiçbir şey yazmaz; dondurma değişmez bir kayıt yazar ve kampanyayı arşivlenmiş durumda reddeder. Alıcı listesi ayrıca alıcı görüntüleme
/// izni ister. Sayılar çağıranın veri kapsamı ve alan yetkisiyle hesaplanır; uygunluk her zaman çekirdeğin iletişim tercihinden gelir.
/// </summary>
internal static class CampaignEndpoints
{
    private const string Prefix = "/api/marketing";

    public static void MapCampaignEndpoints(RouteGroupBuilder group)
    {
        ResourceEndpoints.MapResource<CampaignDto, SaveCampaignRequest>(group, Prefix, "campaigns", MarketingModule.Entities.Campaign,
            MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsWrite);
        ResourceEndpoints.MapResource<CampaignAudienceEntryDto, SaveCampaignAudienceEntryRequest>(group, Prefix, "campaign-audience", MarketingModule.Entities.CampaignAudience,
            MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsWrite);
        ResourceEndpoints.MapResource<AudienceSnapshotDto, SaveAudienceSnapshotRequest>(group, Prefix, "audience-snapshots", MarketingModule.Entities.AudienceSnapshot,
            MarketingModule.Permissions.CampaignsRead, MarketingModule.Permissions.CampaignsAudience);

        RunEndpoints.MapRunEndpoints(group);
        ResponseEndpoints.MapResponseEndpoints(group);
        ReportEndpoints.MapReportEndpoints(group);

        var campaigns = group.MapGroup("/campaigns");
        campaigns.MapPost("/{id:guid}/audience/preview", async (Guid id, ICampaignAudience audience, CancellationToken ct) =>
        {
            var result = await audience.PreviewAsync(id, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsRead);

        campaigns.MapPost("/{id:guid}/audience/snapshot", async (Guid id, ICampaignAudience audience, CancellationToken ct) =>
        {
            var result = await audience.SnapshotAsync(id, ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/audience-snapshots/{result.Value!.Id}", ResourceEndpoints.ToDto<AudienceSnapshotDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsAudience);

        campaigns.MapPost("/{id:guid}/archive", async (Guid id, SegmentActionRequest r, ICampaignAudience audience, CancellationToken ct) =>
        {
            var result = await audience.ArchiveAsync(id, r.Version, ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<CampaignDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsAudience);

        campaigns.MapPost("/{id:guid}/restore", async (Guid id, SegmentActionRequest r, ICampaignAudience audience, CancellationToken ct) =>
        {
            var result = await audience.RestoreAsync(id, r.Version, ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<CampaignDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsAudience);

        group.MapGet("/audience-snapshots/{id:guid}/recipients", async (Guid id, string? status, string? code, int? page, int? pageSize, ICampaignAudience audience, CancellationToken ct) =>
        {
            var result = await audience.RecipientsAsync(id, status, code, page ?? 1, pageSize ?? 25, ct);
            return result.IsSuccess
                ? Results.Ok(new AudienceRecipientsPageDto(
                    [.. result.Value!.Items.Select(r => new AudienceRecipientDto(r.TargetId, r.Label, r.PersonId, r.Status, r.Code))],
                    result.Value.Page, result.Value.PageSize, result.Value.TotalCount))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.CampaignsRead);
    }

    private static AudiencePreviewDto ToDto(AudiencePreview preview) => new(
        new AudienceSummaryDto(
            preview.Summary.Targets, preview.Summary.ResolvedTargets, preview.Summary.Persons, preview.Summary.UniqueAddresses, preview.Summary.Eligible,
            preview.Summary.Excluded, preview.Summary.Exclusions, preview.Summary.Unresolved),
        [.. preview.Sources.Select(s => new AudienceSourceDto(s.SegmentId, s.Name, s.Mode, s.Count))]);
}
