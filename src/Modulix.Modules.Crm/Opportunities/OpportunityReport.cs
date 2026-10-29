using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Opportunities;

public sealed record OpportunityReportQuery(DateOnly From, DateOnly To, Guid? OwnerId);

// Rapor sonucu. Alan adları Shared'daki OpportunityReportDto ile birebir aynıdır (uç nokta JSON üzerinden eşler).
public sealed record ReportCurrencyTotal(string Currency, int Count, decimal? Amount, decimal? Weighted, int UnknownAmountCount, int NoProbabilityCount);

public sealed record ReportStage(string Stage, int Count, IReadOnlyList<ReportCurrencyTotal> Totals, double? AverageDaysInStage);

public sealed record ReportOwner(Guid? OwnerId, int OpenCount, int WonCount, int LostCount, IReadOnlyList<ReportCurrencyTotal> OpenTotals);

public sealed record ReportMonth(string Month, IReadOnlyList<ReportCurrencyTotal> Totals);

public sealed record ReportReason(string Key, int Count);

public sealed record ReportCompetitor(Guid CompetitorId, string? Name, int Count);

public sealed record ReportPostponed(Guid Id, string Title, int ChangeCount, DateOnly? ExpectedCloseDate);

public sealed record ReportAttention(Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, DateOnly? Date);

public sealed record OpportunityReportResult(
    DateOnly From,
    DateOnly To,
    bool Truncated,
    bool AmountsVisible,
    int OpenCount,
    int OnHoldCount,
    IReadOnlyList<ReportCurrencyTotal> OpenTotals,
    IReadOnlyList<ReportStage> ByStage,
    IReadOnlyList<ReportOwner> ByOwner,
    IReadOnlyList<ReportMonth> Forecast,
    int WonCount,
    int LostCount,
    int CancelledCount,
    double? WinRate,
    IReadOnlyList<ReportCurrencyTotal> WonTotals,
    double? AverageCycleDays,
    IReadOnlyList<ReportReason> LossReasons,
    IReadOnlyList<ReportCompetitor> CompetitorLosses,
    IReadOnlyList<ReportAttention> Overdue,
    IReadOnlyList<ReportAttention> NoNextAction,
    IReadOnlyList<ReportAttention> Inactive,
    IReadOnlyList<ReportPostponed> Postponed,
    IReadOnlyList<string> Definitions);

public interface IOpportunityReport
{
    Task<RecordResult<OpportunityReportResult>> BuildAsync(OpportunityReportQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Fırsat raporu (ADR-0017). Veriyi kayıt boru hattından okur: izin, veri kapsamı ve hassas alan kuralları liste ekranıyla aynıdır;
/// kapsam dışı fırsat hiçbir toplamda yer almaz. Para birimleri ayrı gösterilir (kur altyapısı yok, dönüştürme yapılmaz). Rapor
/// bugünkü kayıt değerlerinden hesaplanır; geçmiş tarihli tahmin anlık görüntüsü (snapshot) yoktur.
/// </summary>
internal sealed class OpportunityReport(
    IRecordPipeline pipeline, OpportunityHistoryReader historyReader, ICurrentAccess access, TimeProvider time, IOpportunityPolicyProvider? policyProvider = null)
    : IOpportunityReport
{
    /// <summary>Bir istekte okunan en fazla fırsat (iki sorgu için ayrı ayrı).</summary>
    public const int MaxRows = 5000;

    private const int ListLimit = 50;

    public static IReadOnlyList<string> Definitions { get; } =
    [
        "Açık fırsat: durumu Açık olan fırsat; Beklemede olanlar ayrı sayılır ve açık toplamlara girmez.",
        "Ağırlıklı tutar = tutar × olasılık / 100. Tutarı ya da olasılığı bilinmeyen fırsat ağırlıklı toplama girmez (sıfır sayılmaz); adedi ayrıca bildirilir.",
        "Para birimleri toplanmaz: her toplam para birimi bazındadır. Kur dönüşümü yapılmaz.",
        "Tahmin (aylık): açık fırsatların tahmini kapanış tarihinin ayına göre; tahmini kapanışı olmayanlar \"tarihsiz\" kovasındadır. Tahmin bugünkü kayıt değerlerindendir, geçmiş dönem tahmini değildir.",
        "Kapanış metrikleri: gerçek kapanış tarihi [Başlangıç, Bitiş] içinde olan fırsatlar; her fırsat yalnızca güncel kapanışıyla sayılır (yeniden açılıp kapanan fırsat bir kez).",
        "Kazanma oranı = Kazanılan / (Kazanılan + Kaybedilen). İptal edilen, beklemedeki ve açık fırsatlar paydaya girmez. Payda sıfırsa oran tanımsızdır.",
        "Ortalama satış döngüsü = kazanılan fırsatlarda (gerçek kapanış tarihi − fırsatın ilk oluşturulma günü), gün olarak.",
        "Aşamada kalma süresi: tarihçedeki aşama girişlerinden; yalnızca fırsat Açık durumdayken geçen süre sayılır (bekleme ve kapanış hariç).",
        "Gecikmiş: durumu Açık ve tahmini kapanış tarihi bugünden önce. Sonraki aksiyonu olmayan: Açık ve \"sonraki aksiyon tarihi\" boş. Hareketsiz: Açık ve son müşteri etkileşimi (yoksa oluşturulma) eşikten eski.",
        "Son müşteri etkileşimi gerçekleşen temastan gelir (tamamlanmış toplantı, ulaşılan arama, gönderilen e-posta); \"son güncelleme\" değildir.",
        "Kaybetme nedenleri ve rakibe kayıplar yalnızca dönemde kaybedilen fırsatlardandır; \"vazgeçti\" ve \"mevcut çözümle devam\" rakibe kayıp sayılmaz.",
        "Ertelenen: tahmini kapanış tarihi ilk girişten sonra en az bir kez değişen açık fırsatlar.",
    ];

    public async Task<RecordResult<OpportunityReportResult>> BuildAsync(OpportunityReportQuery query, CancellationToken cancellationToken)
    {
        if (query.To < query.From)
        {
            return RecordResult<OpportunityReportResult>.Fail(RecordErrorCode.ValidationFailed, "Bitiş tarihi başlangıçtan önce olamaz.",
                [new RecordFieldError("to", "Bitiş tarihi başlangıçtan önce olamaz.")]);
        }

        var staleDays = (policyProvider is null ? OpportunityPolicy.Default : await policyProvider.GetAsync(cancellationToken)).StaleDays;
        var amountsVisible = await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken);
        var owner = query.OwnerId is { } id ? new Dictionary<string, string> { ["ownerId"] = id.ToString() } : [];

        var live = await ReadAsync(Merge(owner, "live", "true"), cancellationToken);
        var closed = await ReadAsync(Merge(owner, "actualCloseFrom", Iso(query.From), "actualCloseTo", Iso(query.To)), cancellationToken);
        if (live.Error is not null || closed.Error is not null)
        {
            var error = (live.Error ?? closed.Error)!;
            return RecordResult<OpportunityReportResult>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var now = time.GetUtcNow();
        var open = live.Rows.Where(r => r.Status == OpportunityStatus.Open).ToList();
        var onHold = live.Rows.Count(r => r.Status == OpportunityStatus.OnHold);
        var won = closed.Rows.Where(r => r.Status == OpportunityStatus.Won).ToList();
        var lost = closed.Rows.Where(r => r.Status == OpportunityStatus.Lost).ToList();

        // Tarihçe: oluşturulma günü (döngü süresi, hareketsizlik) ve aşama girişleri (kalma süresi).
        var history = await historyReader.ForManyAsync([.. live.Rows.Select(r => r.Id), .. won.Select(r => r.Id)], cancellationToken);
        DateTimeOffset? CreatedAt(Row r) => history.TryGetValue(r.Id, out var rows) ? rows.FirstOrDefault()?.At : null;

        var dwell = Dwell(open, history, now);
        var stages = open.GroupBy(r => r.Stage).Select(g => new ReportStage(
            g.Key, g.Count(), Totals(g, amountsVisible), dwell.TryGetValue(g.Key, out var days) ? Math.Round(days, 1) : null)).ToList();

        var owners = open.Select(r => r.OwnerId).Concat(won.Select(r => r.OwnerId)).Concat(lost.Select(r => r.OwnerId)).Distinct()
            .Select(o => new ReportOwner(o, open.Count(r => r.OwnerId == o), won.Count(r => r.OwnerId == o), lost.Count(r => r.OwnerId == o),
                Totals(open.Where(r => r.OwnerId == o), amountsVisible))).ToList();

        var forecast = open.GroupBy(r => r.ExpectedCloseDate is { } d ? d.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "tarihsiz")
            .OrderBy(g => g.Key == "tarihsiz" ? "9999" : g.Key, StringComparer.Ordinal)
            .Select(g => new ReportMonth(g.Key, Totals(g, amountsVisible))).ToList();

        var cycleDays = won
            .Select(r => (Close: r.ActualCloseDate, Start: CreatedAt(r)))
            .Where(x => x.Close is not null && x.Start is not null)
            .Select(x => (double)(x.Close!.Value.DayNumber - DateOnly.FromDateTime(x.Start!.Value.UtcDateTime).DayNumber))
            .ToList();

        var decided = won.Count + lost.Count;
        var lossReasons = lost.Where(r => r.LossReason is not null).GroupBy(r => r.LossReason!)
            .Select(g => new ReportReason(g.Key, g.Count())).OrderByDescending(r => r.Count).ToList();

        var competitorLosses = new List<ReportCompetitor>();
        foreach (var group in lost.Where(r => r.WinningCompetitorId is not null).GroupBy(r => r.WinningCompetitorId!.Value).OrderByDescending(g => g.Count()))
        {
            var competitor = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.OpportunityCompetitor, group.Key), cancellationToken);
            competitorLosses.Add(new ReportCompetitor(group.Key, competitor.IsSuccess ? competitor.Value!.Values["name"]?.GetValue<string>() : null, group.Count()));
        }

        var staleBefore = now.AddDays(-staleDays);
        ReportAttention Item(Row r, DateOnly? date) => new(r.Id, r.Number, r.Title, r.OwnerId, date);

        var result = new OpportunityReportResult(
            query.From, query.To, live.Truncated || closed.Truncated, amountsVisible,
            open.Count, onHold, Totals(open, amountsVisible), stages, owners, forecast,
            won.Count, lost.Count, closed.Rows.Count(r => r.Status == OpportunityStatus.Cancelled),
            decided == 0 ? null : Math.Round((double)won.Count / decided, 4),
            Totals(won.Select(r => r with { Amount = r.WonAmount, Currency = r.WonCurrency ?? r.Currency, Probability = 100 }), amountsVisible),
            cycleDays.Count == 0 ? null : Math.Round(cycleDays.Average(), 1),
            lossReasons, competitorLosses,
            [.. open.Where(r => r.ExpectedCloseDate is { } d && d < today).OrderBy(r => r.ExpectedCloseDate).Take(ListLimit).Select(r => Item(r, r.ExpectedCloseDate))],
            [.. open.Where(r => r.NextActionDate is null).OrderBy(r => r.Number).Take(ListLimit).Select(r => Item(r, null))],
            [.. open.Where(r => (r.LastInteractionAt ?? CreatedAt(r) ?? now) < staleBefore)
                .OrderBy(r => r.LastInteractionAt ?? CreatedAt(r)).Take(ListLimit)
                .Select(r => Item(r, (r.LastInteractionAt ?? CreatedAt(r)) is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null))],
            [.. open.Where(r => r.CloseDateChangeCount > 0).OrderByDescending(r => r.CloseDateChangeCount).Take(ListLimit)
                .Select(r => new ReportPostponed(r.Id, r.Title, r.CloseDateChangeCount, r.ExpectedCloseDate))],
            Definitions);
        return RecordResult<OpportunityReportResult>.Ok(result);
    }

    // ---- Okuma

    private sealed record Row(
        Guid Id, string Number, string Title, Guid? OwnerId, string Status, string Stage, decimal? Amount, string Currency, int? Probability,
        DateOnly? ExpectedCloseDate, DateOnly? ActualCloseDate, decimal? WonAmount, string? WonCurrency, string? LossReason,
        Guid? WinningCompetitorId, DateOnly? NextActionDate, DateTimeOffset? LastInteractionAt, int CloseDateChangeCount);

    private sealed record Page(List<Row> Rows, bool Truncated, RecordError? Error);

    private async Task<Page> ReadAsync(Dictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        var pageNumber = 1;
        while (rows.Count < MaxRows)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Opportunity, pageNumber, RecordQuery.MaxPageSize, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return new Page([], false, result.Error);
            }

            rows.AddRange(result.Value!.Items.Select(ToRow));
            if (result.Value.Items.Count == 0 || rows.Count >= result.Value.TotalCount)
            {
                return new Page(rows, false, null);
            }

            pageNumber++;
        }

        return new Page(rows, true, null);
    }

    private static Row ToRow(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        decimal? Money(string name) => v[name] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : null;
        int? Int(string name) => v[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var guid) ? guid : null;
        DateOnly? Date(string name) => DateOnly.TryParseExact(Text(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        DateTimeOffset? Time(string name) => DateTimeOffset.TryParse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

        return new Row(
            record.Id, Text("opportunityNumber") ?? string.Empty, Text("title") ?? string.Empty, Id("ownerId"), Text("status") ?? OpportunityStatus.Open,
            Text("stage") ?? string.Empty, Money("amount"), Text("currency") ?? Opportunity.DefaultCurrency, Int("probability"),
            Date("expectedCloseDate"), Date("actualCloseDate"), Money("wonAmount"), Text("wonCurrency"), Text("lossReason"),
            Id("winningCompetitorId"), Date("nextActionDate"), Time("lastInteractionAt"), Int("closeDateChangeCount") ?? 0);
    }

    // ---- Hesap

    /// <summary>Aşama başına ortalama kalma süresi (gün): yalnızca durumun Açık olduğu aralıklar; son aralık şimdiye kadar.</summary>
    private static Dictionary<string, double> Dwell(IReadOnlyList<Row> open, Dictionary<Guid, List<OpportunityHistory>> history, DateTimeOffset now)
    {
        var spans = new List<(string Stage, double Days)>();
        foreach (var row in open)
        {
            if (!history.TryGetValue(row.Id, out var events))
            {
                continue;
            }

            var points = events.Where(e => e.Kind != OpportunityHistoryKinds.CloseDate && e.ToStage is not null).ToList();
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].ToStatus != OpportunityStatus.Open)
                {
                    continue;
                }

                var until = i + 1 < points.Count ? points[i + 1].At : now;
                spans.Add((points[i].ToStage!, Math.Max(0, (until - points[i].At).TotalDays)));
            }
        }

        return spans.GroupBy(s => s.Stage).ToDictionary(g => g.Key, g => g.Average(s => s.Days));
    }

    private static IReadOnlyList<ReportCurrencyTotal> Totals(IEnumerable<Row> rows, bool amountsVisible) =>
        [.. rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var known = g.Where(r => r.Amount is not null).ToList();
            var weightable = known.Where(r => r.Probability is not null).ToList();
            return new ReportCurrencyTotal(
                g.Key, g.Count(),
                amountsVisible ? known.Sum(r => r.Amount!.Value) : null,
                amountsVisible ? weightable.Sum(r => OpportunityRules.Weighted(r.Amount, r.Probability) ?? 0m) : null,
                amountsVisible ? g.Count() - known.Count : 0,
                amountsVisible ? known.Count - weightable.Count : 0);
        })];

    private static Dictionary<string, string> Merge(Dictionary<string, string> owner, params string[] pairs)
    {
        var result = new Dictionary<string, string>(owner);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            result[pairs[i]] = pairs[i + 1];
        }

        return result;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
