using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Reporting;

// Kohort, dönüşüm ve performans hesapları (ADR-0026, dilim 2). Saf işlevler: veritabanı ve izin bilmez; girdi, kayıt boru hattından kullanıcının
// kapsamıyla okunmuş kayıtlardır, bu yüzden hesap kapsamı aşamaz.

// ---- Teklif kohortu

/// <summary>Teklif revizyonunun hesap için gereken alanları.</summary>
public sealed record QuoteFact(
    Guid Id, Guid FamilyId, int RevisionNo, string Status, DateTimeOffset? PresentedAt, DateTimeOffset? DecidedAt, Guid? OwnerId, string Currency, decimal NetTotal);

public sealed record QuoteOwnerCohort(Guid? OwnerId, int Families, int Accepted, decimal? AcceptanceRatePercent);

/// <summary>
/// Teklif kohortu sonucu. Birim <b>teklif ailesidir</b> (revizyonlar tek aile); kohort, ailenin <b>ilk gönderilme</b> anı [Başlangıç, Bitiş) içinde olanlardır.
/// Sonuç <see cref="AsOf"/> (kesim) anına göredir: sonradan kabul edilen aile, kesimden önce Beklemede sayılır.
/// </summary>
public sealed record QuoteCohortResult(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    DateTimeOffset AsOf,
    bool Mature,
    int Families,
    int Accepted,
    int Pending,
    int Rejected,
    int Cancelled,
    int Expired,
    decimal? AcceptanceRatePercent,
    int PresentedRevisions,
    decimal? AverageRevisionsPerFamily,
    decimal? RevisionAcceptanceRatePercent,
    decimal? AverageDaysToAccept,
    decimal? MedianDaysToAccept,
    MoneyTotal AcceptedNet,
    IReadOnlyList<QuoteOwnerCohort> ByOwner);

public static class QuoteCohort
{
    public static QuoteCohortResult Compute(
        IReadOnlyList<QuoteFact> facts, DateTimeOffset from, DateTimeOffset toExclusive, DateTimeOffset asOf,
        string currency, RateBook rates, TimeZoneInfo zone)
    {
        var families = new List<Family>();
        foreach (var group in facts.GroupBy(f => f.FamilyId))
        {
            // Yalnızca kesime kadar sunulmuş revizyonlar vardır; taslak ve sonradan sunulan hiçbir şey saymaz.
            var presented = group.Where(f => f.PresentedAt is { } at && at <= asOf).OrderBy(f => f.RevisionNo).ToList();
            if (presented.Count == 0)
            {
                continue;
            }

            var first = presented.Min(f => f.PresentedAt!.Value);
            if (first < from || first >= toExclusive)
            {
                continue;
            }

            var accepted = presented.Where(f => f.Status == QuoteStatus.Accepted && f.DecidedAt is { } d && d <= asOf).OrderBy(f => f.DecidedAt).FirstOrDefault();
            families.Add(new Family(group.Key, first, presented, accepted, Outcome(presented, accepted, asOf)));
        }

        var accepts = families.Where(f => f.Accepted is not null).ToList();
        var days = accepts.Select(f => (decimal)(f.Accepted!.DecidedAt!.Value - f.FirstPresentedAt).TotalDays).Where(d => d >= 0).Order().ToList();
        var revisions = families.Sum(f => f.Presented.Count);
        var acceptedRevisions = families.Sum(f => f.Presented.Count(r => r.Status == QuoteStatus.Accepted && r.DecidedAt is { } d && d <= asOf));

        decimal netTotal = 0;
        var missingRate = 0;
        foreach (var family in accepts)
        {
            var accepted = family.Accepted!;
            var (converted, _) = rates.Convert(accepted.NetTotal, accepted.Currency, currency, ReportPeriods.Today(accepted.DecidedAt!.Value, zone));
            if (converted is { } value)
            {
                netTotal += value;
            }
            else
            {
                missingRate++;
            }
        }

        var byOwner = families.GroupBy(f => f.Presented[^1].OwnerId).Select(g => new QuoteOwnerCohort(
            g.Key, g.Count(), g.Count(f => f.Outcome == QuoteStatus.Accepted), Percent(g.Count(f => f.Outcome == QuoteStatus.Accepted), g.Count())))
            .OrderByDescending(o => o.Families).ThenBy(o => o.OwnerId).ToList();

        return new QuoteCohortResult(
            from, toExclusive, asOf, asOf >= toExclusive,
            families.Count,
            families.Count(f => f.Outcome == QuoteStatus.Accepted),
            families.Count(f => f.Outcome == QuoteStatus.Presented),
            families.Count(f => f.Outcome == QuoteStatus.Rejected),
            families.Count(f => f.Outcome == QuoteStatus.Cancelled),
            families.Count(f => f.Outcome == QuoteStatus.Expired),
            Percent(accepts.Count, families.Count),
            revisions,
            families.Count == 0 ? null : Math.Round((decimal)revisions / families.Count, 2),
            Percent(acceptedRevisions, revisions),
            days.Count == 0 ? null : Math.Round(days.Average(), 1),
            days.Count == 0 ? null : Math.Round(Stats.Median(days), 1),
            new MoneyTotal(netTotal, accepts.Count, 0, missingRate),
            byOwner);
    }

    /// <summary>Ailenin kesimdeki sonucu: kabul; aksi halde son sunulan revizyonun kesimde geçerli kararı (karar kesimden sonraysa Beklemede).</summary>
    private static string Outcome(List<QuoteFact> presented, QuoteFact? accepted, DateTimeOffset asOf)
    {
        if (accepted is not null)
        {
            return QuoteStatus.Accepted;
        }

        var latest = presented[^1];
        var decided = latest.DecidedAt is null || latest.DecidedAt <= asOf;
        return decided && latest.Status is QuoteStatus.Rejected or QuoteStatus.Cancelled or QuoteStatus.Expired
            ? latest.Status
            : QuoteStatus.Presented;
    }

    private static decimal? Percent(int part, int whole) => whole == 0 ? null : Math.Round(100m * part / whole, 2);

    private sealed record Family(Guid Id, DateTimeOffset FirstPresentedAt, List<QuoteFact> Presented, QuoteFact? Accepted, string Outcome);
}

// ---- Lead kohortu

public sealed record LeadFact(Guid Id, string Status, DateTimeOffset ReceivedAt, DateTimeOffset? ConvertedAt, Guid? OwnerId, string? Source);

public sealed record LeadSourceCohort(string Source, int Leads, int Converted, decimal? RatePercent);

/// <summary>
/// Lead dönüşüm kohortu: kohort, alınma anı [Başlangıç, Bitiş) içindeki lead'lerdir. Dönüşüm = kesime kadar fırsata dönüşmüş <b>tekil</b> lead (bir lead'den kaç fırsat çıkarsa çıksın
/// bir kez). İki oran verilir: tüm kohorta ve <b>uygun</b> lead'lere (elenenler hariç) göre. Kohort, bitişinden sonra <see cref="MaturityDays"/> gün geçmeden olgun sayılmaz.
/// </summary>
public sealed record LeadCohortResult(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    DateTimeOffset AsOf,
    int MaturityDays,
    bool Mature,
    int Leads,
    int Converted,
    int Disqualified,
    int Open,
    int Eligible,
    decimal? RateOfAllPercent,
    decimal? RateOfEligiblePercent,
    decimal? AverageDaysToConvert,
    decimal? MedianDaysToConvert,
    IReadOnlyList<LeadSourceCohort> BySource);

public static class LeadCohort
{
    public const int DefaultMaturityDays = 30;

    public static LeadCohortResult Compute(
        IReadOnlyList<LeadFact> facts, DateTimeOffset from, DateTimeOffset toExclusive, DateTimeOffset asOf, int maturityDays = DefaultMaturityDays)
    {
        var cohort = facts.DistinctBy(f => f.Id).Where(f => f.ReceivedAt >= from && f.ReceivedAt < toExclusive && f.ReceivedAt <= asOf).ToList();
        bool IsConverted(LeadFact f) => f.Status == LeadStatus.Converted && f.ConvertedAt is { } at && at <= asOf;
        var converted = cohort.Where(IsConverted).ToList();
        var disqualified = cohort.Count(f => f.Status == LeadStatus.Disqualified);
        var eligible = cohort.Count - disqualified;
        var days = converted.Select(f => (decimal)(f.ConvertedAt!.Value - f.ReceivedAt).TotalDays).Where(d => d >= 0).Order().ToList();

        var bySource = cohort.GroupBy(f => f.Source ?? string.Empty).Select(g =>
        {
            var done = g.Count(IsConverted);
            return new LeadSourceCohort(g.Key, g.Count(), done, Percent(done, g.Count()));
        }).OrderByDescending(s => s.Leads).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();

        return new LeadCohortResult(
            from, toExclusive, asOf, maturityDays, asOf >= toExclusive.AddDays(maturityDays),
            cohort.Count, converted.Count, disqualified, cohort.Count - converted.Count - disqualified, eligible,
            Percent(converted.Count, cohort.Count), Percent(converted.Count, eligible),
            days.Count == 0 ? null : Math.Round(days.Average(), 1),
            days.Count == 0 ? null : Math.Round(Stats.Median(days), 1),
            bySource);
    }

    private static decimal? Percent(int part, int whole) => whole == 0 ? null : Math.Round(100m * part / whole, 2);
}

// ---- Aşama akışı (tarihsel dönüşüm)

public sealed record StageDef(string Key, int Order, string Category);

/// <summary>Tarihçe kaydının hesap için gereken alanları (aşama giriş olayları: Created ve Stage türleri).</summary>
public sealed record StageEvent(Guid OpportunityId, DateTimeOffset At, string? ToStage);

public sealed record StageFlowRow(
    string Stage, int Order, int Entered, int Advanced, decimal? AdvanceRatePercent, int LostFrom, int WonFrom, int StillHere);

/// <summary>
/// Kohort bazlı aşama dönüşümü: kohort, oluşturulma anı [Başlangıç, Bitiş) içindeki fırsatlardır; sayılan, aşamaya <b>hiç girmiş tekil fırsat</b>tır
/// (geri dönülen/tekrarlanan girişler bir kez; atlanan aşamaya girilmiş sayılmaz). İlerleme = daha ileri sıradaki (kaybedilen kategorisi hariç) bir aşamaya girmiş olmak.
/// Bu, <b>güncel aşama dağılımı değildir</b>; o, açık fırsatların bugünkü dağılımıdır (<c>StillHere</c> yalnızca bilgi içindir).
/// </summary>
public sealed record StageFlowResult(
    DateTimeOffset From, DateTimeOffset ToExclusive, DateTimeOffset AsOf, int Cohort, DateTimeOffset? TrackingStart, IReadOnlyList<StageFlowRow> Rows);

public static class StageFlow
{
    public const string OpenCategory = "open";
    public const string WonCategory = "won";
    public const string LostCategory = "lost";

    public static StageFlowResult Compute(
        IReadOnlyList<StageDef> stages, IReadOnlyDictionary<Guid, List<StageEvent>> byOpportunity,
        DateTimeOffset from, DateTimeOffset toExclusive, DateTimeOffset asOf, DateTimeOffset? trackingStart)
    {
        var order = stages.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var paths = new List<Path>();
        foreach (var (id, events) in byOpportunity)
        {
            var sequence = events.Where(e => e.ToStage is not null && e.At <= asOf).OrderBy(e => e.At).Select(e => e.ToStage!).Where(order.ContainsKey).ToList();
            if (sequence.Count > 0)
            {
                paths.Add(new Path(id, sequence));
            }
        }

        var rows = new List<StageFlowRow>();
        foreach (var stage in stages.Where(s => s.Category == OpenCategory).OrderBy(s => s.Order))
        {
            var entered = paths.Where(p => p.Stages.Any(s => Same(s, stage.Key))).ToList();
            var advanced = entered.Count(p => p.Stages.Any(s => order[s] is { } d && d.Order > stage.Order && d.Category != LostCategory));
            var lostFrom = entered.Count(p => p.Stages.Any(s => order[s].Category == LostCategory) && Same(LastOpen(p, order), stage.Key));
            var wonFrom = entered.Count(p => p.Stages.Any(s => order[s].Category == WonCategory) && Same(LastOpen(p, order), stage.Key));
            var here = entered.Count(p => Same(p.Stages[^1], stage.Key));
            rows.Add(new StageFlowRow(
                stage.Key, stage.Order, entered.Count, advanced, entered.Count == 0 ? null : Math.Round(100m * advanced / entered.Count, 2), lostFrom, wonFrom, here));
        }

        return new StageFlowResult(from, toExclusive, asOf, paths.Count, trackingStart, rows);
    }

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fırsatın girdiği son açık kategorili aşama (kapanıştan hemen önceki aşama).</summary>
    private static string? LastOpen(Path path, Dictionary<string, StageDef> order) =>
        path.Stages.LastOrDefault(s => order[s].Category == OpenCategory);

    private sealed record Path(Guid OpportunityId, List<string> Stages);
}

// ---- Ekip performansı

public sealed record OwnerFact(Guid? OwnerId, OpportunityFact Fact, int? CycleDays);

public sealed record OwnerPerformance(
    Guid? OwnerId,
    MoneyTotal Realized,
    int WonCount,
    int LostCount,
    decimal? WinRatePercent,
    MoneyTotal OpenPipeline,
    MoneyTotal WeightedOpen,
    MoneyTotal ExpectedOpen,
    int OpenCount,
    int OverdueOpenCount,
    int CycleSample,
    decimal? AverageCycleDays,
    decimal? MedianCycleDays);

public static class TeamPerformance
{
    /// <summary>
    /// Kullanıcı (sorumlu) başına performans. Her sayı <see cref="SalesMetrics"/>'ten gelir. Döngü = kazanılmış fırsatta gerçek kapanış − oluşturulma günü; örneklem adedi her zaman
    /// verilir (az örneklemde ortalama yanıltıcıdır). <b>Bugünkü sahiplik</b> esastır: fırsat kimin üzerindeyse onun satırındadır.
    /// </summary>
    public static IReadOnlyList<OwnerPerformance> Compute(
        IReadOnlyList<OwnerFact> facts, ReportPeriod period, string currency, RateBook rates, DateOnly today)
    {
        var result = new List<OwnerPerformance>();
        foreach (var group in facts.GroupBy(f => f.OwnerId))
        {
            var opportunities = group.Select(f => f.Fact).ToList();
            var metrics = SalesMetrics.Compute(opportunities, period, currency, rates, today);
            var wonIds = SalesMetrics.Select(opportunities, SalesCard.Realized, period).Select(f => f.Id).ToHashSet();
            var cycles = group.Where(f => wonIds.Contains(f.Fact.Id) && f.CycleDays is >= 0).Select(f => (decimal)f.CycleDays!.Value).Order().ToList();
            result.Add(new OwnerPerformance(
                group.Key, metrics.Realized, metrics.WonCount, metrics.LostCount, metrics.WinRatePercent, metrics.OpenPipeline, metrics.WeightedOpen, metrics.ExpectedOpen,
                metrics.OpenPipeline.Count, metrics.OverdueOpenCount, cycles.Count,
                cycles.Count == 0 ? null : Math.Round(cycles.Average(), 1), cycles.Count == 0 ? null : Math.Round(Stats.Median(cycles), 1)));
        }

        return [.. result.OrderByDescending(r => r.Realized.Amount).ThenByDescending(r => r.WonCount).ThenBy(r => r.OwnerId)];
    }
}

public static class Stats
{
    /// <summary>Sıralı listenin medyanı (çift sayıda iki ortanın ortalaması).</summary>
    public static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}

// ---- Veri kalitesi

/// <summary>Veri kalitesi denetiminde bir açık fırsatın alanları. <c>HasHistory</c> false: fırsatın tarihçe kaydı yok (izleme başlamadan önce açılmış).</summary>
public sealed record QualityOpportunity(
    Guid Id, string Number, string Title, Guid? OwnerId, string Status, decimal? Amount, string Currency, int? Probability,
    DateOnly? ExpectedCloseDate, DateTimeOffset? LastInteractionAt, bool HasHistory);

public sealed record QualityItem(Guid Id, string Number, string Title, Guid? OwnerId, string? Detail);

/// <summary><see cref="Count"/> sorunlu fırsat adedidir; <see cref="Samples"/> en fazla örnek sınırı kadar kayıttır (kimlikle kayda gidilir).</summary>
public sealed record QualityIssue(string Code, int Count, IReadOnlyList<QualityItem> Samples);

public static class DataQuality
{
    public const string MissingAmount = "MissingAmount";
    public const string MissingRate = "MissingRate";
    public const string MissingProbability = "MissingProbability";
    public const string MissingCloseDate = "MissingCloseDate";
    public const string Overdue = "Overdue";
    public const string Stale = "Stale";
    public const string NoHistory = "NoHistory";

    public static IReadOnlyList<string> Codes { get; } =
        [MissingAmount, MissingRate, MissingProbability, MissingCloseDate, Overdue, Stale, NoHistory];

    /// <summary>
    /// Yalnızca durumu Açık fırsatlar denetlenir. Eksik tutar/olasılık/kapanış tarihi, kuru bulunamayan tutar (1 varsayılmaz), geciken kapanış, hareketsizlik
    /// (son müşteri etkileşimi yok ya da eşikten eski) ve tarihçesi olmayan fırsat (aşama süresi/dönüşümü hesaplanamaz) ayrı sayılır.
    /// </summary>
    public static IReadOnlyList<QualityIssue> Compute(
        IReadOnlyList<QualityOpportunity> opportunities, DateOnly today, DateTimeOffset now, int staleDays, string currency, RateBook rates, int sampleLimit = 20)
    {
        var open = opportunities.Where(o => o.Status == OpportunityStatus.Open).ToList();
        var staleBefore = now.AddDays(-staleDays);

        string? Detail(string code, QualityOpportunity o) => code switch
        {
            MissingRate => $"{o.Currency} → {currency} kuru yok",
            Overdue => o.ExpectedCloseDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Stale => o.LastInteractionAt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "etkileşim yok",
            _ => null,
        };

        QualityIssue Issue(string code, Func<QualityOpportunity, bool> predicate)
        {
            var hits = open.Where(predicate).OrderBy(o => o.Number, StringComparer.Ordinal).ToList();
            return new QualityIssue(code, hits.Count, [.. hits.Take(sampleLimit).Select(o => new QualityItem(o.Id, o.Number, o.Title, o.OwnerId, Detail(code, o)))]);
        }

        return
        [
            Issue(MissingAmount, o => o.Amount is null),
            Issue(MissingRate, o => o.Amount is not null && rates.Find(o.Currency, currency, today) is null),
            Issue(MissingProbability, o => o.Probability is null),
            Issue(MissingCloseDate, o => o.ExpectedCloseDate is null),
            Issue(Overdue, o => o.ExpectedCloseDate is { } d && d < today),
            Issue(Stale, o => o.LastInteractionAt is null || o.LastInteractionAt < staleBefore),
            Issue(NoHistory, o => !o.HasHistory),
        ];
    }
}
