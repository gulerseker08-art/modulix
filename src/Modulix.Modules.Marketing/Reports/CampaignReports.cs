using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Records;

namespace Modulix.Modules.Marketing.Reports;

/// <param name="Sent">Kabul edilen + sonucu belirsiz. Teslim edildi anlamına gelmez.</param>
/// <param name="FirstTouchLeads">Bu kampanya, yanıttan lead açılan hedefe giden ilk ileti olan lead sayısı.</param>
/// <param name="LastTouchLeads">Lead'in açıldığı yanıtın cevapladığı ileti bu kampanyadandı (son dokunuş).</param>
public sealed record CampaignReportRow(
    Guid CampaignId, string Code, string Name, string Channel, string Status, int Runs,
    int Sent, int Accepted, int Unknown, int Failed, int Skipped,
    int Responses, int Interested, int OptOuts, int Complaints, int Leads, int FirstTouchLeads, int LastTouchLeads,
    decimal? Budget, decimal? ActualCost, string Currency, decimal? CostPerResponse, decimal? CostPerLead);

public sealed record CampaignReport(IReadOnlyList<CampaignReportRow> Rows, DateTimeOffset GeneratedAt, bool Truncated);

public sealed record TouchHistoryItem(string Kind, Guid CampaignId, string CampaignName, DateTimeOffset At, string Status, string? Detail);

public sealed record TargetHistory(IReadOnlyList<TouchHistoryItem> Items);

public interface ICampaignReports
{
    /// <summary>Çağıranın görebildiği (kampanya izni ve veri kapsamı) kampanyaların raporu. Süzgeçler: kanal, durum, kampanya.</summary>
    Task<RecordResult<CampaignReport>> CampaignsAsync(string? channel, string? status, Guid? campaignId, CancellationToken cancellationToken);

    /// <summary>Bir firma/kişi/lead kimliğine giden iletiler ve yanıtlar; yalnızca çağıranın görebildiği kampanyalardan. Adres içermez.</summary>
    Task<RecordResult<TargetHistory>> HistoryAsync(Guid targetId, CancellationToken cancellationToken);
}

internal sealed class CampaignReports(IRecordPipeline pipeline, MarketingStoreContext context, ICurrentAccess permissions, TimeProvider time) : ICampaignReports
{
    private const int MaxCampaigns = 1000;
    private const int HistoryLimit = 100;
    private const int Chunk = 1000;

    public async Task<RecordResult<CampaignReport>> CampaignsAsync(string? channel, string? status, Guid? campaignId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.ReportsRead, cancellationToken))
        {
            return Fail<CampaignReport>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.ReportsRead}' izni gerekir.");
        }

        var filters = new Dictionary<string, string>();
        if (channel is not null) { filters["channel"] = channel; }
        if (status is not null) { filters["status"] = status; }
        var visible = await VisibleCampaignsAsync(filters, cancellationToken);
        if (!visible.IsSuccess)
        {
            return RecordResult<CampaignReport>.Fail(visible.Error!.Code, visible.Error.Message, visible.Error.FieldErrors);
        }

        var campaigns = visible.Value!.Items.Where(c => campaignId is null || c.Id == campaignId).ToList();
        var ids = campaigns.Select(c => c.Id).ToList();
        var data = context.Data;

        var runs = await data.Set<CampaignRun>().AsNoTracking().Where(r => ids.Contains(r.CampaignId)).GroupBy(r => r.CampaignId)
            .Select(g => new { CampaignId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.CampaignId, x => x.Count, cancellationToken);

        var deliveryCounts = (await (
            from d in data.Set<CampaignDelivery>().AsNoTracking()
            join r in data.Set<CampaignRun>().AsNoTracking() on d.RunId equals r.Id
            where ids.Contains(r.CampaignId)
            group d by new { r.CampaignId, d.Status } into g
            select new { g.Key.CampaignId, g.Key.Status, Count = g.Count() }).ToListAsync(cancellationToken))
            .GroupBy(x => x.CampaignId).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Status, x => x.Count));

        var responseCounts = (await data.Set<CampaignResponse>().AsNoTracking().Where(r => ids.Contains(r.CampaignId))
            .GroupBy(r => new { r.CampaignId, r.Kind }).Select(g => new { g.Key.CampaignId, g.Key.Kind, Count = g.Count() }).ToListAsync(cancellationToken))
            .GroupBy(x => x.CampaignId).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Kind, x => x.Count));

        var leadResponses = await data.Set<CampaignResponse>().AsNoTracking().Where(r => ids.Contains(r.CampaignId) && r.LeadId != null)
            .Select(r => new { r.CampaignId, r.TargetId }).ToListAsync(cancellationToken);
        var firstTouch = await FirstTouchAsync(leadResponses.Select(l => l.TargetId).Distinct().ToList(), cancellationToken);

        var rows = new List<CampaignReportRow>();
        foreach (var c in campaigns)
        {
            var d = deliveryCounts.GetValueOrDefault(c.Id) ?? [];
            var r = responseCounts.GetValueOrDefault(c.Id) ?? [];
            var accepted = d.GetValueOrDefault(DeliveryStatuses.Accepted);
            var unknown = d.GetValueOrDefault(DeliveryStatuses.Unknown);
            var leads = leadResponses.Count(l => l.CampaignId == c.Id);
            var first = leadResponses.Count(l => firstTouch.GetValueOrDefault(l.TargetId) == c.Id);
            var responses = r.Values.Sum();
            var cost = Money(c.Values["actualCost"]);
            rows.Add(new CampaignReportRow(
                c.Id, c.Values["code"]!.GetValue<string>(), c.Values["name"]!.GetValue<string>(), c.Values["channel"]!.GetValue<string>(), c.Values["status"]!.GetValue<string>(),
                runs.GetValueOrDefault(c.Id), accepted + unknown, accepted, unknown, d.GetValueOrDefault(DeliveryStatuses.Failed), d.GetValueOrDefault(DeliveryStatuses.Skipped),
                responses,
                r.GetValueOrDefault(ResponseKinds.Interested) + r.GetValueOrDefault(ResponseKinds.Question) + r.GetValueOrDefault(ResponseKinds.MeetingRequest),
                r.GetValueOrDefault(ResponseKinds.OptOut), r.GetValueOrDefault(ResponseKinds.Complaint), leads, first, leads,
                Money(c.Values["budget"]), cost, c.Values["costCurrency"]?.GetValue<string>() ?? "TRY",
                ReportRules.CostPer(cost, responses), ReportRules.CostPer(cost, leads)));
        }

        return RecordResult<CampaignReport>.Ok(new CampaignReport([.. rows.OrderByDescending(r => r.Sent).ThenBy(r => r.Code)], time.GetUtcNow(), visible.Value.Truncated));
    }

    public async Task<RecordResult<TargetHistory>> HistoryAsync(Guid targetId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.ReportsRead, cancellationToken)
            || !await permissions.HasPermissionAsync(MarketingModule.Permissions.RecipientsRead, cancellationToken))
        {
            return Fail<TargetHistory>(RecordErrorCode.PermissionDenied,
                $"Bu işlem için '{MarketingModule.Permissions.ReportsRead}' ve '{MarketingModule.Permissions.RecipientsRead}' izinleri gerekir.");
        }

        var visible = await VisibleCampaignsAsync(new Dictionary<string, string>(), cancellationToken);
        if (!visible.IsSuccess)
        {
            return RecordResult<TargetHistory>.Fail(visible.Error!.Code, visible.Error.Message, visible.Error.FieldErrors);
        }

        var names = visible.Value!.Items.ToDictionary(c => c.Id, c => c.Values["name"]!.GetValue<string>());
        var ids = names.Keys.ToList();
        var data = context.Data;
        var items = new List<TouchHistoryItem>();

        var deliveries = await (
            from d in data.Set<CampaignDelivery>().AsNoTracking()
            join r in data.Set<CampaignRun>().AsNoTracking() on d.RunId equals r.Id
            where (d.TargetId == targetId || d.PersonId == targetId) && ids.Contains(r.CampaignId)
            orderby d.CreatedAt descending
            select new { r.CampaignId, d.Status, d.Code, d.CreatedAt }).Take(HistoryLimit).ToListAsync(cancellationToken);
        items.AddRange(deliveries.Select(d => new TouchHistoryItem("Delivery", d.CampaignId, names[d.CampaignId], d.CreatedAt, d.Status, d.Code)));

        // Yanıtlar kayıt boru hattından okunur: yanıt izni ve yanıtın sorumlusuna bağlı veri kapsamı uygulanır; izin yoksa yanıtlar listelenmez.
        if (await permissions.HasPermissionAsync(MarketingModule.Permissions.ResponsesRead, cancellationToken))
        {
            var responses = await pipeline.QueryAsync(
                new RecordQuery(MarketingModule.Entities.CampaignResponse, 1, HistoryLimit, null, new Dictionary<string, string> { ["targetId"] = targetId.ToString() }), cancellationToken);
            if (responses.IsSuccess)
            {
                foreach (var r in responses.Value!.Items)
                {
                    var campaignId = Guid.Parse(r.Values["campaignId"]!.GetValue<string>());
                    if (names.TryGetValue(campaignId, out var name))
                    {
                        items.Add(new TouchHistoryItem(
                            "Response", campaignId, name, DateTimeOffset.Parse(r.Values["receivedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                            r.Values["status"]!.GetValue<string>(), r.Values["kind"]!.GetValue<string>()));
                    }
                }
            }
        }

        return RecordResult<TargetHistory>.Ok(new TargetHistory([.. items.OrderByDescending(i => i.At).Take(HistoryLimit)]));
    }

    private sealed record Visible(IReadOnlyList<RecordData> Items, bool Truncated);

    private async Task<RecordResult<Visible>> VisibleCampaignsAsync(IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var items = new List<RecordData>();
        for (var page = 1; items.Count < MaxCampaigns; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(MarketingModule.Entities.Campaign, page, RecordQuery.MaxPageSize, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<Visible>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }

            items.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || items.Count >= result.Value.TotalCount)
            {
                return RecordResult<Visible>.Ok(new Visible(items, false));
            }
        }

        return RecordResult<Visible>.Ok(new Visible(items, true));
    }

    private async Task<IReadOnlyDictionary<Guid, Guid>> FirstTouchAsync(IReadOnlyList<Guid> targets, CancellationToken cancellationToken)
    {
        var touches = new List<TouchPoint>();
        foreach (var part in targets.Chunk(Chunk))
        {
            var ids = part.ToList();
            touches.AddRange(await (
                from d in context.Data.Set<CampaignDelivery>().AsNoTracking()
                join r in context.Data.Set<CampaignRun>().AsNoTracking() on d.RunId equals r.Id
                where ids.Contains(d.TargetId) && (d.Status == DeliveryStatuses.Accepted || d.Status == DeliveryStatuses.Unknown)
                select new TouchPoint(d.TargetId, r.CampaignId, d.CreatedAt)).ToListAsync(cancellationToken));
        }

        return ReportRules.FirstTouch(touches);
    }

    private static decimal? Money(JsonNode? node) => node is null ? null : node.GetValue<decimal>();

    private static RecordResult<T> Fail<T>(RecordErrorCode code, string message) => RecordResult<T>.Fail(code, message, null);
}
