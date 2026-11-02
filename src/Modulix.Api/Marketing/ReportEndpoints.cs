using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Reports;
using Modulix.Shared.Marketing;

namespace Modulix.Api.Marketing;

/// <summary>
/// Kampanya raporu ve müşteri kartı geçmişi (ADR-0025, dilim 8). Salt okunur: kampanyalar kayıt boru hattıyla (izin ve veri kapsamı) okunur, sayımlar yalnızca bu kampanyalar için yapılır;
/// geçmişte adres yoktur ve firma/kişi için alıcı görüntüleme izni de gerekir.
/// </summary>
internal static class ReportEndpoints
{
    public static void MapReportEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/reports/campaigns", async (string? channel, string? status, Guid? campaignId, ICampaignReports reports, CancellationToken ct) =>
        {
            var result = await reports.CampaignsAsync(channel, status, campaignId, ct);
            return result.IsSuccess
                ? Results.Ok(new CampaignReportDto([.. result.Value!.Rows.Select(r => new CampaignReportRowDto(
                    r.CampaignId, r.Code, r.Name, r.Channel, r.Status, r.Runs, r.Sent, r.Accepted, r.Unknown, r.Failed, r.Skipped, r.Responses, r.Interested, r.OptOuts, r.Complaints,
                    r.Leads, r.FirstTouchLeads, r.LastTouchLeads, r.Budget, r.ActualCost, r.Currency, r.CostPerResponse, r.CostPerLead))], result.Value.GeneratedAt, result.Value.Truncated))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.ReportsRead);

        group.MapGet("/targets/{targetId:guid}/history", async (Guid targetId, ICampaignReports reports, CancellationToken ct) =>
        {
            var result = await reports.HistoryAsync(targetId, ct);
            return result.IsSuccess
                ? Results.Ok(new TargetHistoryDto([.. result.Value!.Items.Select(i => new TouchHistoryItemDto(i.Kind, i.CampaignId, i.CampaignName, i.At, i.Status, i.Detail))]))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(MarketingModule.Permissions.ReportsRead);
    }
}
