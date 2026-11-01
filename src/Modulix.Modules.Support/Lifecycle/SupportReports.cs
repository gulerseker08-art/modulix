using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record ReportQuery(
    DateOnly? From, DateOnly? To, string? Granularity, string? TypeKey, Guid? QueueId, Guid? CategoryId, string? Priority, Guid? PartyId);

public sealed record ReportMeta(DateOnly From, DateOnly To, string Granularity, int TicketCount, bool Truncated, string TimeZone, DateTimeOffset GeneratedAt);

public sealed record CountItem(string Key, int Count, string DrillDown);

public sealed record VolumePoint(DateOnly Bucket, int Created);

public sealed record VolumeSection(int Total, IReadOnlyList<VolumePoint> Series, IReadOnlyList<CountItem> ByStatus, IReadOnlyList<CountItem> ByType, IReadOnlyList<CountItem> ByChannel, IReadOnlyList<CountItem> ByPriority, string DrillDown);

public sealed record BacklogBucketItem(string Key, string Label, int Count, string DrillDown);

public sealed record BacklogSection(int Open, int Unassigned, double? OldestDays, IReadOnlyList<BacklogBucketItem> Buckets);

/// <summary>Süreler dakikadır. Takvim süresi: açılıştan olaya kadar geçen gerçek süre. İş süresi: SLA örneğinin harcanan süresi (takvime göre, duraklatmalar hariç); yalnızca SLA politikası uygulanmış talepler girer.</summary>
public sealed record TimesSection(
    DurationStats FirstResponseCalendar, DurationStats FirstResponseBusiness, DurationStats ResolutionCalendar, DurationStats ResolutionBusiness, int NotYetResponded, int NotYetResolved);

public sealed record SlaSection(string Metric, SlaComplianceCounts Counts, double? CompliancePercent, string DrillDown);

public sealed record ReopenSection(int Resolved, int Reopened, double? RatePercent, int TotalReopens, string DrillDown);

public sealed record CsatSection(int Sent, int NotSent, int Answered, double? ResponseRatePercent, double? AverageRating, IReadOnlyList<CountItem> Distribution, int Low, bool SmallSample, string DrillDown);

public sealed record ReportOverview(
    ReportMeta Meta, VolumeSection Volume, BacklogSection Backlog, TimesSection Times, IReadOnlyList<SlaSection> Sla, ReopenSection Reopen, CsatSection Csat);

public sealed record RecurringCause(string Cause, int Count, DateTimeOffset FirstAt, DateTimeOffset LastAt, IReadOnlyList<CountItem> Categories, IReadOnlyList<Guid> TicketIds, string DrillDown);

public sealed record OverdueAction(Guid Id, Guid TicketId, string TicketNumber, string Title, Guid OwnerId, DateOnly DueDate, int DaysOverdue);

public sealed record ActionSummary(int Open, int Overdue, int Done, int Cancelled, int DueInPeriod, int DoneOnTime, int DoneLate, int OverdueInPeriod, double? OnTimePercent);

public sealed record ComplaintReview(
    ReportMeta Meta, int Complaints, int WithRootCause, int MinOccurrences, IReadOnlyList<RecurringCause> Recurring, ActionSummary Actions, IReadOnlyList<OverdueAction> OverdueActions);

/// <summary>
/// Destek raporları (ADR-0024 dilim 6). Veri, çağıranın kendi yetkisi ve veri kapsamıyla <b>kayıt boru hattından</b> okunur (liste ekranıyla aynı izin ve kapsam; raporlar görünürlüğü aşmaz),
/// toplama bellekte yapılır; tek rapor en çok <see cref="MaxRows"/> talep okur ve aşılırsa <see cref="ReportMeta.Truncated"/> işaretlenir (sessizce eksik veri verilmez).
/// Kohort: dönemde (Türkiye günü) <b>açılan</b> talepler; süre istatistikleri yalnızca ilgili aşamaya ulaşanları kapsar, ulaşmayanlar ayrıca sayılır. Açık talep yaşı (birikim) dönemden bağımsız, şu anki durumdur.
/// Oranlarda pay ve payda açıktır; payda sıfırsa oran boştur. Örneklem büyüklüğü hep döner.
/// </summary>
public interface ISupportReports
{
    public const int MaxRows = 10000;

    Task<RecordResult<ReportOverview>> OverviewAsync(ReportQuery query, CancellationToken cancellationToken);

    Task<RecordResult<ComplaintReview>> ComplaintsAsync(ReportQuery query, int? minOccurrences, CancellationToken cancellationToken);
}

internal sealed class SupportReports(IRecordPipeline pipeline, SupportStoreContext context) : ISupportReports
{
    private const string Key = SupportModule.Entities.Ticket;

    /// <summary>Birikim: çalışılan durumlar. Çözülmüş talep müşteri teyidini beklese de iş kalmadığı için birikimde sayılmaz (listedeki "açık" görünümünden farkı budur).</summary>
    private const string BacklogStatuses = "New,Triage,Assigned,InProgress,WaitingCustomer,WaitingInternal,Reopened";

    private sealed record Row(
        Guid Id, string Number, string Status, string Priority, string TypeKey, string Channel, Guid? CategoryId, Guid? OwnerId, int ReopenCount, string? RootCause,
        DateTimeOffset CreatedAt, DateTimeOffset? FirstResponseAt, DateTimeOffset? ResolvedAt);

    public async Task<RecordResult<ReportOverview>> OverviewAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var validated = Validate(query);
        if (validated.Error is { } error)
        {
            return RecordResult<ReportOverview>.Fail(RecordErrorCode.ValidationFailed, error.Message, [error]);
        }

        var (from, to, granularity) = validated.Value;
        var now = context.Time.GetUtcNow();
        var passthrough = Passthrough(query);
        var baseFilter = BaseDrill(from, to, passthrough);

        var cohort = await FetchAsync(Merge(passthrough, ("createdFrom", Day(from)), ("createdTo", Day(to))), cancellationToken);
        if (cohort.Error is not null)
        {
            return RecordResult<ReportOverview>.Fail(cohort.Error.Code, cohort.Error.Message, cohort.Error.FieldErrors);
        }

        var open = await FetchAsync(Merge(passthrough, ("status", BacklogStatuses)), cancellationToken);
        if (open.Error is not null)
        {
            return RecordResult<ReportOverview>.Fail(open.Error.Code, open.Error.Message, open.Error.FieldErrors);
        }

        var rows = cohort.Rows;
        var ids = rows.Select(r => r.Id).ToList();
        var instances = await InstancesAsync(ids, cancellationToken);
        var surveys = await SurveysAsync(ids, cancellationToken);

        var meta = new ReportMeta(from, to, granularity, rows.Count, cohort.Truncated || open.Truncated, "Europe/Istanbul", now);
        var overview = new ReportOverview(
            meta,
            Volume(rows, from, to, granularity, baseFilter),
            Backlog(open.Rows, now, passthrough),
            Times(rows, instances),
            [Sla(SlaMetric.FirstResponse, instances, baseFilter), Sla(SlaMetric.Resolution, instances, baseFilter)],
            Reopen(rows, baseFilter),
            Csat(surveys, baseFilter));
        return RecordResult<ReportOverview>.Ok(overview);
    }

    public async Task<RecordResult<ComplaintReview>> ComplaintsAsync(ReportQuery query, int? minOccurrences, CancellationToken cancellationToken)
    {
        var validated = Validate(query);
        if (validated.Error is { } error)
        {
            return RecordResult<ComplaintReview>.Fail(RecordErrorCode.ValidationFailed, error.Message, [error]);
        }

        var min = minOccurrences ?? 2;
        if (min is < 2 or > 100)
        {
            return RecordResult<ComplaintReview>.Fail(RecordErrorCode.ValidationFailed, "Tekrar eşiği 2-100 arasında olmalıdır.", [new RecordFieldError("minOccurrences", "Tekrar eşiği 2-100 arasında olmalıdır.")]);
        }

        var (from, to, granularity) = validated.Value;
        var now = context.Time.GetUtcNow();
        var today = ReportPeriod.LocalDate(now);
        var types = await context.ChoiceLists.GetAsync(SupportModule.Lists.TicketType, cancellationToken);
        var complaintKeys = types.Values.Where(v => v.Category == SupportModule.TypeCategories.Complaint).Select(v => v.Key).ToList();
        var passthrough = Passthrough(query with { TypeKey = null });

        var created = new List<Row>();
        var all = new List<Row>();
        var truncated = false;
        foreach (var key in complaintKeys.Where(k => query.TypeKey is null || string.Equals(k, query.TypeKey, StringComparison.OrdinalIgnoreCase)))
        {
            var inPeriod = await FetchAsync(Merge(passthrough, ("typeKey", key), ("createdFrom", Day(from)), ("createdTo", Day(to))), cancellationToken);
            var everything = await FetchAsync(Merge(passthrough, ("typeKey", key)), cancellationToken);
            if ((inPeriod.Error ?? everything.Error) is { } failure)
            {
                return RecordResult<ComplaintReview>.Fail(failure.Code, failure.Message, failure.FieldErrors);
            }

            created.AddRange(inPeriod.Rows);
            all.AddRange(everything.Rows);
            truncated |= inPeriod.Truncated || everything.Truncated;
        }

        // Tekrarlayan neden: kök nedeni yazılmış şikâyetler normalleştirilmiş metne göre gruplanır (anlamsal eşleştirme yoktur).
        var categoryNames = await context.Data.Set<TicketCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var withCause = created.Where(r => RootCauseGrouping.Normalize(r.RootCause).Length > 0).ToList();
        var recurring = withCause.GroupBy(r => RootCauseGrouping.Normalize(r.RootCause)).Where(g => g.Count() >= min)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RecurringCause(
                g.GroupBy(r => r.RootCause!.Trim()).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key,
                g.Count(), g.Min(r => r.CreatedAt), g.Max(r => r.CreatedAt),
                [.. g.GroupBy(r => r.CategoryId).OrderByDescending(x => x.Count()).Select(x => new CountItem(x.Key is { } id && categoryNames.TryGetValue(id, out var name) ? name : "Kategorisiz", x.Count(), string.Empty))],
                [.. g.OrderBy(r => r.CreatedAt).Select(r => r.Id).Take(50)],
                "ids=" + string.Join(',', g.OrderBy(r => r.CreatedAt).Select(r => r.Id).Take(50))))
            .ToList();

        // Düzeltici aksiyonlar: çağıranın görebildiği tüm şikâyetlerin aksiyonları (dönemden bağımsız birikim) ve vadesi dönemde olanların zamanında tamamlanma oranı.
        var numbers = all.ToDictionary(r => r.Id, r => r.Number);
        var ticketIds = numbers.Keys.ToList();
        var actions = new List<CorrectiveAction>();
        foreach (var chunk in ticketIds.Chunk(1000))
        {
            actions.AddRange(await context.Data.Set<CorrectiveAction>().AsNoTracking().Where(a => chunk.Contains(a.TicketId)).ToListAsync(cancellationToken));
        }

        var inRange = actions.Where(a => a.DueDate >= from && a.DueDate <= to).ToList();
        var doneOnTime = inRange.Count(a => a.Status == CorrectiveActionStatus.Done && a.ClosedAt is { } c && ReportPeriod.LocalDate(c) <= a.DueDate);
        var doneLate = inRange.Count(a => a.Status == CorrectiveActionStatus.Done) - doneOnTime;
        var overdueInRange = inRange.Count(a => CorrectiveActionStatus.IsOverdue(a.Status, a.DueDate, today));
        var summary = new ActionSummary(
            actions.Count(a => a.Status == CorrectiveActionStatus.Open),
            actions.Count(a => CorrectiveActionStatus.IsOverdue(a.Status, a.DueDate, today)),
            actions.Count(a => a.Status == CorrectiveActionStatus.Done),
            actions.Count(a => a.Status == CorrectiveActionStatus.Cancelled),
            inRange.Count(a => a.Status != CorrectiveActionStatus.Cancelled),
            doneOnTime, doneLate, overdueInRange,
            ReportStats.Percent(doneOnTime, doneOnTime + doneLate + overdueInRange));
        var overdue = actions.Where(a => CorrectiveActionStatus.IsOverdue(a.Status, a.DueDate, today)).OrderBy(a => a.DueDate).ThenBy(a => a.Id).Take(50)
            .Select(a => new OverdueAction(a.Id, a.TicketId, numbers[a.TicketId], a.Title, a.OwnerId, a.DueDate, CorrectiveActionStatus.DaysOverdue(a.DueDate, today))).ToList();

        var meta = new ReportMeta(from, to, granularity, created.Count, truncated, "Europe/Istanbul", now);
        return RecordResult<ComplaintReview>.Ok(new ComplaintReview(meta, created.Count, withCause.Count, min, recurring, summary, overdue));
    }

    // ---- Bölümler

    private static VolumeSection Volume(List<Row> rows, DateOnly from, DateOnly to, string granularity, string baseFilter)
    {
        var counts = rows.GroupBy(r => ReportPeriod.Bucket(ReportPeriod.LocalDate(r.CreatedAt), granularity)).ToDictionary(g => g.Key, g => g.Count());
        var series = new List<VolumePoint>();
        for (var bucket = ReportPeriod.Bucket(from, granularity); bucket <= to; bucket = Next(bucket, granularity))
        {
            series.Add(new VolumePoint(bucket, counts.GetValueOrDefault(bucket)));
        }

        IReadOnlyList<CountItem> By(Func<Row, string> key, string filterName) =>
            [.. rows.GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new CountItem(g.Key, g.Count(), $"{baseFilter}&{filterName}={Uri.EscapeDataString(g.Key)}"))];
        return new VolumeSection(rows.Count, series, By(r => r.Status, "status"), By(r => r.TypeKey, "typeKey"), By(r => r.Channel, "channel"), By(r => r.Priority, "priority"), baseFilter);
    }

    private static DateOnly Next(DateOnly bucket, string granularity) => granularity switch
    {
        "month" => bucket.AddMonths(1),
        "week" => bucket.AddDays(7),
        _ => bucket.AddDays(1),
    };

    private static BacklogSection Backlog(List<Row> open, DateTimeOffset now, IReadOnlyDictionary<string, string> passthrough)
    {
        var filter = $"status={BacklogStatuses}" + string.Concat(passthrough.Select(p => $"&{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var ages = open.Select(r => (now - r.CreatedAt).TotalDays).ToList();
        var buckets = BacklogAge.Buckets.Select(b => new BacklogBucketItem(
            b.Key, b.Label, ages.Count(a => BacklogAge.KeyOf(a) == b.Key),
            $"{filter}&minAgeDays={b.MinDays}" + (b.MaxDays is { } max ? $"&maxAgeDays={max}" : string.Empty))).ToList();
        var unassigned = open.Count(r => r.OwnerId is null && r.Status is not (TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.Cancelled or TicketStatus.Merged));
        return new BacklogSection(open.Count, unassigned, ages.Count == 0 ? null : Math.Round(ages.Max(), 1), buckets);
    }

    private static TimesSection Times(List<Row> rows, List<InstanceRow> instances)
    {
        var responded = rows.Where(r => r.FirstResponseAt is not null).ToList();
        var resolved = rows.Where(r => r.ResolvedAt is not null && r.Status is TicketStatus.Resolved or TicketStatus.Closed).ToList();
        var byTicket = instances.Where(i => i.Status == SlaStatus.Completed).GroupBy(i => i.TicketId).ToDictionary(g => g.Key, g => g.ToList());

        double? Business(Guid ticketId, string metric) =>
            byTicket.TryGetValue(ticketId, out var list) && list.Where(i => i.Metric == metric).OrderByDescending(i => i.Cycle).FirstOrDefault() is { } i ? i.WorkedSeconds / 60d : null;

        return new TimesSection(
            ReportStats.Of([.. responded.Select(r => (r.FirstResponseAt!.Value - r.CreatedAt).TotalMinutes)]),
            ReportStats.Of([.. responded.Select(r => Business(r.Id, SlaMetric.FirstResponse)).Where(v => v is not null).Select(v => v!.Value)]),
            ReportStats.Of([.. resolved.Select(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalMinutes)]),
            ReportStats.Of([.. resolved.Select(r => Business(r.Id, SlaMetric.Resolution)).Where(v => v is not null).Select(v => v!.Value)]),
            rows.Count - responded.Count,
            rows.Count(r => r.Status is not (TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.Cancelled or TicketStatus.Merged)));
    }

    private static SlaSection Sla(string metric, List<InstanceRow> instances, string baseFilter)
    {
        var outcomes = instances.Where(i => i.Metric == metric).Select(i => SlaOutcome.Of(i.Status, i.BreachedAt)).ToList();
        var counts = new SlaComplianceCounts(
            outcomes.Count(o => o == SlaOutcome.Met), outcomes.Count(o => o == SlaOutcome.Missed), outcomes.Count(o => o == SlaOutcome.Breached),
            outcomes.Count(o => o == SlaOutcome.InProgress), outcomes.Count(o => o == SlaOutcome.Cancelled));
        return new SlaSection(metric, counts, counts.CompliancePercent, $"{baseFilter}&slaBreached=true&open=true");
    }

    private static ReopenSection Reopen(List<Row> rows, string baseFilter)
    {
        var resolved = rows.Where(r => r.ResolvedAt is not null).ToList();
        var reopened = resolved.Count(r => r.ReopenCount > 0);
        return new ReopenSection(resolved.Count, reopened, ReportStats.Percent(reopened, resolved.Count), resolved.Sum(r => r.ReopenCount), $"{baseFilter}&reopened=true");
    }

    private static CsatSection Csat(List<SurveyRow> surveys, string baseFilter)
    {
        var sent = surveys.Where(s => s.QueuedAt is not null).ToList();
        var answered = surveys.Where(s => s.Rating is not null).ToList();
        var distribution = Enumerable.Range(SurveyRules.MinRating, SurveyRules.MaxRating).Select(r => new CountItem(r.ToString(CultureInfo.InvariantCulture), answered.Count(s => s.Rating == r), string.Empty)).ToList();
        return new CsatSection(
            sent.Count, surveys.Count - sent.Count, answered.Count, ReportStats.Percent(answered.Count(s => s.QueuedAt is not null), sent.Count),
            answered.Count == 0 ? null : Math.Round(answered.Average(s => s.Rating!.Value), 2, MidpointRounding.AwayFromZero), distribution,
            answered.Count(s => SurveyRules.IsLow(s.Rating!.Value)), CsatRules.IsSmallSample(answered.Count), $"{baseFilter}&lowSatisfaction=true");
    }

    // ---- Veri

    private sealed record InstanceRow(Guid TicketId, int Cycle, string Metric, string Status, long WorkedSeconds, DateTimeOffset? BreachedAt);

    private sealed record SurveyRow(DateTimeOffset? QueuedAt, int? Rating);

    private async Task<List<InstanceRow>> InstancesAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new List<InstanceRow>();
        foreach (var chunk in ids.Chunk(1000))
        {
            result.AddRange(await context.Data.Set<SlaInstance>().AsNoTracking().Where(i => chunk.Contains(i.TicketId))
                .Select(i => new InstanceRow(i.TicketId, i.Cycle, i.Metric, i.Status, i.WorkedSeconds, i.BreachedAt)).ToListAsync(cancellationToken));
        }

        return result;
    }

    private async Task<List<SurveyRow>> SurveysAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new List<SurveyRow>();
        foreach (var chunk in ids.Chunk(1000))
        {
            result.AddRange(await context.Data.Set<SatisfactionSurvey>().AsNoTracking().Where(s => chunk.Contains(s.TicketId)).Select(s => new SurveyRow(s.QueuedAt, s.Rating)).ToListAsync(cancellationToken));
        }

        return result;
    }

    private sealed record Fetched(List<Row> Rows, bool Truncated, RecordError? Error);

    /// <summary>Talepleri kayıt boru hattıyla (izin ve veri kapsamı liste ekranıyla aynı) sayfa sayfa okur; üst sınırda keser ve işaretler.</summary>
    private async Task<Fetched> FetchAsync(IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        var truncated = false;
        for (var page = 1; ; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(Key, page, RecordQuery.MaxPageSize, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return new Fetched([], false, result.Error);
            }

            rows.AddRange(result.Value!.Items.Select(ToRow));
            if (result.Value.Items.Count == 0 || rows.Count >= result.Value.TotalCount)
            {
                break;
            }

            if (rows.Count >= ISupportReports.MaxRows)
            {
                truncated = true;
                break;
            }
        }

        return new Fetched(rows, truncated, null);
    }

    private static Row ToRow(RecordData record)
    {
        var v = record.Values;
        return new Row(
            record.Id, Text(v, "number") ?? string.Empty, Text(v, "status") ?? TicketStatus.New, Text(v, "priority") ?? "Normal", Text(v, "typeKey") ?? string.Empty, Text(v, "channel") ?? "User",
            Id(v, "categoryId"), Id(v, "ownerId"), v["reopenCount"] is JsonValue n && n.TryGetValue<int>(out var count) ? count : 0, Text(v, "rootCause"),
            Time(v, "createdAt") ?? DateTimeOffset.MinValue, Time(v, "firstResponseAt"), Time(v, "resolvedAt"));
    }

    private static string? Text(JsonObject v, string field) => v[field] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    private static Guid? Id(JsonObject v, string field) => Text(v, field) is { } s && Guid.TryParse(s, out var id) ? id : null;

    private static DateTimeOffset? Time(JsonObject v, string field) =>
        Text(v, field) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    // ---- Girdi

    private static (string? Granularity, RecordFieldError? Error) CheckGranularity(string? granularity) =>
        granularity is null ? ("day", null)
        : ReportPeriod.Granularities.Contains(granularity) ? (granularity, null)
        : (null, new RecordFieldError("granularity", $"Şunlardan biri olmalıdır: {string.Join(", ", ReportPeriod.Granularities)}."));

    private ((DateOnly From, DateOnly To, string Granularity) Value, RecordFieldError? Error) Validate(ReportQuery query)
    {
        var (granularity, granularityError) = CheckGranularity(query.Granularity);
        var (from, to, periodError) = ReportPeriod.Resolve(query.From, query.To, context.Time.GetUtcNow());
        var error = granularityError ?? (periodError is null ? null : new RecordFieldError("from", periodError));
        if (error is null && query.Priority is not null && !TicketPriority.All.Contains(query.Priority))
        {
            error = new RecordFieldError("priority", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketPriority.All)}.");
        }

        return ((from, to, granularity ?? "day"), error);
    }

    /// <summary>Rapor süzgeçleri liste süzgeçleriyle aynı adları kullanır; böylece rapordaki her sayı aynı süzgeçle listeye açılabilir (kırılım).</summary>
    private static IReadOnlyDictionary<string, string> Passthrough(ReportQuery q)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q.TypeKey is { Length: > 0 } type) { filters["typeKey"] = type; }
        if (q.QueueId is { } queue) { filters["queueId"] = queue.ToString(); }
        if (q.CategoryId is { } category) { filters["categoryId"] = category.ToString(); }
        if (q.Priority is { Length: > 0 } priority) { filters["priority"] = priority; }
        if (q.PartyId is { } party) { filters["partyId"] = party.ToString(); }
        return filters;
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> filters, params (string Name, string Value)[] extra)
    {
        var result = new Dictionary<string, string>(filters, StringComparer.Ordinal);
        foreach (var (name, value) in extra)
        {
            result[name] = value;
        }

        return result;
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string BaseDrill(DateOnly from, DateOnly to, IReadOnlyDictionary<string, string> passthrough) =>
        $"createdFrom={Day(from)}&createdTo={Day(to)}" + string.Concat(passthrough.Select(p => $"&{p.Key}={Uri.EscapeDataString(p.Value)}"));
}
