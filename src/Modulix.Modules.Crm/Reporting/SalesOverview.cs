using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Reporting;

public sealed record SalesOverviewQuery(PeriodKind Kind, DateOnly? Anchor, Guid? OwnerId, string? Currency);

/// <summary>Hedef bloğu. <see cref="Source"/>: Tenant (tenant hedefi), User (seçilen kullanıcının hedefi), UserSum (kapsamdaki kullanıcı hedeflerinin toplamı), None.</summary>
public sealed record TargetBlock(
    string Metric, string Source, decimal? Value, TargetProgress? Progress, string? Problem, decimal? ChildExcess, string? Currency);

public sealed record StageSlice(string Stage, int Count, decimal Amount, decimal Weighted, int MissingAmountCount, int MissingRateCount);

public sealed record TrendPoint(string Label, DateOnly Start, DateOnly End, decimal Realized, int WonCount, bool Partial);

public sealed record OverviewRisk(Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, DateOnly? ExpectedCloseDate, decimal? Amount, string Currency);

/// <summary>
/// Satış genel görünümü sonucu. Alan adları Shared'daki <c>SalesOverviewDto</c> ile birebir aynıdır (uç nokta JSON üzerinden eşler).
/// <see cref="AmountsVisible"/> false ise tutar ve olasılık hassas olduğundan hiçbir tutar toplanmaz (adetler yine gösterilir).
/// </summary>
public sealed record SalesOverviewResult(
    string PeriodKind,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Today,
    string Currency,
    bool AmountsVisible,
    bool ScopeLimited,
    bool Truncated,
    MoneyTotal OpenPipeline,
    MoneyTotal ExpectedOpen,
    MoneyTotal WeightedOpen,
    int WeightedMissingProbabilityCount,
    MoneyTotal Realized,
    decimal Outlook,
    int WonCount,
    int LostCount,
    decimal? WinRatePercent,
    int OpenWithoutCloseDateCount,
    int OverdueOpenCount,
    TargetBlock? AmountTarget,
    TargetBlock? CountTarget,
    IReadOnlyList<StageSlice> ByStage,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<OverviewRisk> Overdue,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions);

public sealed record SalesDetailRow(
    Guid Id, string OpportunityNumber, string Title, Guid? OwnerId, string Status, string Stage, decimal? SourceAmount, string SourceCurrency,
    decimal? Amount, string? Issue, int? Probability, DateOnly? ExpectedCloseDate, DateOnly? ActualCloseDate, decimal? RateUsed);

/// <summary>Kartın detay listesi: <see cref="Total"/> kart toplamıyla aynı hesaptan gelir (aynı kesit, aynı kur); satırlar tutara göre azalan, en fazla <see cref="SalesOverview.DetailLimit"/> adet.</summary>
public sealed record SalesCardDetail(
    string Card, string PeriodLabel, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, bool AmountsVisible, bool Truncated,
    MoneyTotal Total, int RowCount, IReadOnlyList<SalesDetailRow> Rows);

public interface ISalesOverview
{
    Task<RecordResult<SalesOverviewResult>> BuildAsync(SalesOverviewQuery query, CancellationToken cancellationToken);

    Task<RecordResult<SalesCardDetail>> DetailAsync(SalesCard card, SalesOverviewQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Satış genel görünümü (ADR-0026). Veriyi kayıt boru hattından okur: izin, veri kapsamı ve hassas alan kuralları fırsat listesiyle aynıdır; kapsam dışı fırsat hiçbir toplamda
/// yer almaz. Metrikler <see cref="SalesMetrics"/>'ten gelir (kart, detay ve tahmin aynı hesabı kullanır). Hesap bugünkü kayıt değerlerindendir; geçmiş dönem tahmini için
/// kilitli tahmin sürümleri vardır.
/// </summary>
internal sealed class SalesOverview(
    SalesOpportunityReader reader, SalesPlanningData planning, ICurrentAccess access, IRecordAccess recordAccess, TimeProvider time,
    ISalesReportingPolicyProvider? policyProvider = null) : ISalesOverview
{
    public const int DetailLimit = 500;
    private const int TrendPeriods = 6;
    private const int RiskLimit = 20;

    public static IReadOnlyList<string> Definitions { get; } =
    [
        "Tutarlar vergi hariç, iskonto sonrası net fırsat tutarıdır; muhasebe geliri, fatura ya da tahsilat değildir. Sipariş ve fatura tutarı bu toplamlara eklenmez.",
        "Açık pipeline: durumu Açık olan tüm fırsatların tutarı (kapanış tarihinden bağımsız). Beklemede olanlar girmez.",
        "Beklenen açık satış: beklenen kapanış tarihi seçili dönemde olan açık fırsatlar. Kesin satış değildir.",
        "Ağırlıklı açık tahmin: aynı fırsatlar için tutar × olasılık / 100. Olasılığı bilinmeyen fırsat sıfır sayılmaz; ayrı bildirilir ve toplama girmez.",
        "Gerçekleşen satış: gerçek kapanışı dönemde olan kazanılmış fırsatların kazanılan tutarı (yoksa fırsat tutarı). Kazanıldığı günün kuruyla çevrilir.",
        "Dönem öngörüsü: gerçekleşen satış + aynı dönemdeki ağırlıklı açık tahmin. Kazanılmış fırsat ikinci kez eklenmez.",
        "Kazanma oranı: dönemde kazanılan / (kazanılan + kaybedilen). Açık, beklemedeki ve iptal edilen fırsatlar paydaya girmez; payda sıfırsa oran yoktur.",
        "Hedef gerçekleşme: gerçekleşen / hedef. Kalan hedef: hedef − gerçekleşen. Pipeline kapsama: dönemde kapanması beklenen açık tutar / kalan hedef; kalan hedef sıfır ya da altındaysa \"hedef karşılandı\".",
        "Hedef, dönemi tam kaplayan hedef dilimlerinin toplamıdır. Tenant hedefi varken kullanıcı hedefleri ona eklenmez; yalnızca üst hedefi aşıp aşmadıkları kontrol edilir.",
        "Dönemler firmanın saat diliminde ve mali takvimde, başlangıç dahil sonraki dönem başlangıcı hariç hesaplanır.",
        "Para birimi dönüşümü kayıtlı kurlarla yapılır: açık fırsatlar hesap gününün, kazanılmışlar kazanım gününün kuruyla. Kuru bulunamayan tutar toplama girmez ve ayrıca bildirilir (1 varsayılmaz).",
    ];

    public async Task<RecordResult<SalesOverviewResult>> BuildAsync(SalesOverviewQuery query, CancellationToken cancellationToken)
    {
        var load = await LoadAsync(query, withTrend: true, cancellationToken);
        if (load.Error is not null)
        {
            return RecordResult<SalesOverviewResult>.Fail(load.Error.Code, load.Error.Message, load.Error.FieldErrors);
        }

        var facts = load.Rows.Select(r => r.ToFact()).ToList();
        var period = load.Period;
        var metrics = SalesMetrics.Compute(facts, period, load.Currency, load.Rates, load.Today);
        var visible = load.AmountsVisible;

        var stageOf = load.Rows.ToDictionary(r => r.Id, r => r.Stage);
        var stages = SalesMetrics.Select(facts, SalesCard.OpenPipeline, period)
            .GroupBy(f => stageOf[f.Id])
            .Select(g => StageOf(g.Key, g.ToList(), period, load))
            .OrderBy(s => s.Stage, StringComparer.Ordinal).ToList();

        var trend = TrendOf(facts, period, load);
        var overdue = load.Rows.Where(r => r.Status == OpportunityStatus.Open && r.ExpectedCloseDate is { } d && d < load.Today)
            .OrderBy(r => r.ExpectedCloseDate).Take(RiskLimit)
            .Select(r => new OverviewRisk(r.Id, r.Number, r.Title, r.OwnerId, r.ExpectedCloseDate, visible ? r.Amount : null, r.Currency)).ToList();

        var (amountTarget, countTarget, targetWarnings) = await TargetsAsync(query, load, metrics, cancellationToken);

        var warnings = Warnings(load, metrics, targetWarnings);
        return RecordResult<SalesOverviewResult>.Ok(new SalesOverviewResult(
            period.Kind.ToString(), period.Label, period.Start, period.LastDay, load.Today, load.Currency, visible, load.ScopeLimited, load.Truncated,
            Shown(metrics.OpenPipeline, visible), Shown(metrics.ExpectedOpen, visible), Shown(metrics.WeightedOpen, visible), visible ? metrics.WeightedMissingProbabilityCount : 0,
            Shown(metrics.Realized, visible), visible ? metrics.Outlook : 0m, metrics.WonCount, metrics.LostCount, metrics.WinRatePercent,
            metrics.OpenWithoutCloseDateCount, metrics.OverdueOpenCount,
            visible ? amountTarget : null, countTarget, visible ? stages : [.. stages.Select(s => new StageSlice(s.Stage, s.Count, 0m, 0m, 0, 0))],
            visible ? trend : [.. trend.Select(t => t with { Realized = 0m, Partial = false })], overdue, warnings, Definitions));
    }

    public async Task<RecordResult<SalesCardDetail>> DetailAsync(SalesCard card, SalesOverviewQuery query, CancellationToken cancellationToken)
    {
        var load = await LoadAsync(query, withTrend: false, cancellationToken);
        if (load.Error is not null)
        {
            return RecordResult<SalesCardDetail>.Fail(load.Error.Code, load.Error.Message, load.Error.FieldErrors);
        }

        var facts = load.Rows.Select(r => r.ToFact()).ToList();
        var byId = load.Rows.ToDictionary(r => r.Id);
        var selected = SalesMetrics.Select(facts, card, load.Period);
        var total = SalesMetrics.Total(facts, card, load.Period, load.Currency, load.Rates, load.Today);
        var visible = load.AmountsVisible;

        var rows = selected.Select(f =>
        {
            var row = byId[f.Id];
            var converted = SalesMetrics.Amount(f, card, load.Currency, load.Rates, load.Today);
            return new SalesDetailRow(
                row.Id, row.Number, row.Title, row.OwnerId, row.Status, row.Stage, visible ? row.Amount : null, row.Currency,
                visible ? converted.Value : null, visible ? converted.Issue : null, visible ? row.Probability : null,
                row.ExpectedCloseDate, row.ActualCloseDate, visible ? converted.Rate?.Rate : null);
        }).OrderByDescending(r => r.Amount ?? -1m).ThenBy(r => r.OpportunityNumber, StringComparer.Ordinal).ToList();

        return RecordResult<SalesCardDetail>.Ok(new SalesCardDetail(
            card.ToString(), load.Period.Label, load.Period.Start, load.Period.LastDay, load.Currency, visible, load.Truncated,
            Shown(total, visible), rows.Count, [.. rows.Take(DetailLimit)]));
    }

    // ---- Yükleme

    private sealed record Loaded(
        ReportPeriod Period, string Currency, DateOnly Today, RateBook Rates, bool AmountsVisible, bool ScopeLimited, bool Truncated,
        List<OpportunityRow> Rows, SalesReportingPolicy Policy, IReadOnlySet<Guid>? Owners, RecordError? Error);

    private async Task<Loaded> LoadAsync(SalesOverviewQuery query, bool withTrend, CancellationToken cancellationToken)
    {
        var policy = policyProvider is null ? SalesReportingPolicy.Default : await policyProvider.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var today = ReportPeriods.Today(now, policy.TimeZone);
        var period = ReportPeriods.Containing(query.Kind, query.Anchor ?? today, policy.FiscalStartMonth);
        var currency = (query.Currency ?? policy.DefaultCurrency).Trim().ToUpperInvariant();
        var owners = await recordAccess.GetOwnersAsync(cancellationToken);
        var amountsVisible = await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken);

        var windowStart = withTrend ? ReportPeriods.Shift(period, -(TrendPeriods - 1), policy.FiscalStartMonth).Start : period.Start;
        var open = await reader.ReadAsync(SalesOpportunityReader.Filters(query.OwnerId, "status", OpportunityStatus.Open), cancellationToken);
        var closed = await reader.ReadAsync(SalesOpportunityReader.Filters(
            query.OwnerId, "actualCloseFrom", SalesOpportunityReader.Iso(windowStart), "actualCloseTo", SalesOpportunityReader.Iso(period.LastDay)), cancellationToken);
        var error = open.Error ?? closed.Error;

        var rates = RateBook.Empty;
        if (error is null)
        {
            rates = new RateBook(await planning.RatesAsync(cancellationToken));
        }

        return new Loaded(
            period, currency, today, rates, amountsVisible, owners is not null, open.Truncated || closed.Truncated,
            [.. open.Rows, .. closed.Rows.Where(r => r.Status != OpportunityStatus.Open)], policy, owners, error);
    }

    // ---- Hesap parçaları

    private static MoneyTotal Shown(MoneyTotal total, bool visible) => visible ? total : new MoneyTotal(0m, total.Count, 0, 0);

    private static StageSlice StageOf(string stage, List<OpportunityFact> facts, ReportPeriod period, Loaded load)
    {
        var all = SalesMetrics.Total(facts, SalesCard.OpenPipeline, period, load.Currency, load.Rates, load.Today);
        // Aşamadaki açık fırsatların ağırlıklı tutarı kapanış tarihinden bağımsızdır; olasılığı bilinmeyen girmez.
        var weighted = facts.Where(f => f.Probability is not null)
            .Sum(f => SalesMetrics.Amount(f, SalesCard.WeightedOpen, load.Currency, load.Rates, load.Today).Value ?? 0m);
        return new StageSlice(stage, all.Count, all.Amount, weighted, all.MissingAmountCount, all.MissingRateCount);
    }

    private static List<TrendPoint> TrendOf(List<OpportunityFact> facts, ReportPeriod period, Loaded load)
    {
        var points = new List<TrendPoint>();
        for (var back = TrendPeriods - 1; back >= 0; back--)
        {
            var p = ReportPeriods.Shift(period, -back, load.Policy.FiscalStartMonth);
            var realized = SalesMetrics.Total(facts, SalesCard.Realized, p, load.Currency, load.Rates, load.Today);
            points.Add(new TrendPoint(p.Label, p.Start, p.LastDay, realized.Amount, realized.Count, realized.IsPartial));
        }

        return points;
    }

    private async Task<(TargetBlock? Amount, TargetBlock? Count, List<string> Warnings)> TargetsAsync(
        SalesOverviewQuery query, Loaded load, SalesPeriodMetrics metrics, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (!await access.HasPermissionAsync(CrmModule.Permissions.SalesTargetsRead, cancellationToken))
        {
            return (null, null, warnings);
        }

        var period = load.Period;
        var targets = await planning.TargetsOverlappingAsync(period.Start, period.LastDay, cancellationToken);

        TargetBlock Block(string metric, string? currency, decimal realized, decimal expectedOpen)
        {
            var sameMetric = targets.Where(t => t.Metric == metric && t.Currency == currency).ToList();
            var foreign = metric == TargetMetric.WonAmount && targets.Any(t => t.Metric == metric && t.Currency != currency);
            var (source, resolved, excess) = ResolveScope(sameMetric, query, load);
            var problem = resolved.Problem ?? (resolved.Value is null && foreign
                ? $"Hedef başka para biriminde tanımlı; raporlama para birimi {currency} ile eşleşmiyor."
                : null);
            if (excess is { } over)
            {
                warnings.Add($"Kullanıcı hedeflerinin toplamı tenant hedefini {over:N0} aşıyor; hedef karşılaştırması tenant hedefine göredir (alt hedefler eklenmez).");
            }

            return new TargetBlock(metric, source, resolved.Value, SalesMetrics.Progress(resolved.Value, realized, expectedOpen), problem, excess, currency);
        }

        var amount = Block(TargetMetric.WonAmount, load.Currency, metrics.Realized.Amount, metrics.ExpectedOpen.Amount);
        var count = Block(TargetMetric.WonCount, null, metrics.WonCount, metrics.ExpectedOpen.Count);
        if (amount.Problem is not null)
        {
            warnings.Add(amount.Problem);
        }

        if (count.Problem is not null)
        {
            warnings.Add(count.Problem);
        }

        return (amount, count, warnings);
    }

    /// <summary>
    /// Kapsama göre hedef: kullanıcı seçiliyse onun hedefi; veri kapsamı kısıtlıysa kapsamdaki kullanıcıların hedef toplamı (tenant hedefiyle karşılaştırılmaz, çünkü
    /// gerçekleşen yalnızca kapsamdaki fırsatlardır); aksi halde tenant hedefi, yoksa kullanıcı hedeflerinin toplamı. Üst ve alt hedefler birlikte toplanmaz.
    /// </summary>
    private static (string Source, ResolvedTarget Resolved, decimal? Excess) ResolveScope(List<SalesTarget> targets, SalesOverviewQuery query, Loaded load)
    {
        static TargetSlice Slice(SalesTarget t) => new(t.PeriodStart, t.PeriodEnd.AddDays(1), t.Value);

        ResolvedTarget ForUser(Guid user) =>
            TargetRules.Resolve(targets.Where(t => t.ScopeType == TargetScope.User && t.ScopeUserId == user).Select(Slice), load.Period);

        ResolvedTarget Sum(IEnumerable<Guid> users)
        {
            var parts = users.Select(ForUser).ToList();
            return parts.Any(p => p.Problem is not null)
                ? new ResolvedTarget(null, parts.First(p => p.Problem is not null).Problem)
                : parts.Any(p => p.Value is not null)
                    ? new ResolvedTarget(parts.Sum(p => p.Value ?? 0m), null)
                    : new ResolvedTarget(null, null);
        }

        if (query.OwnerId is { } owner)
        {
            return ("User", ForUser(owner), null);
        }

        if (load.Owners is { } scoped)
        {
            return ("UserSum", Sum(scoped), null);
        }

        var tenant = TargetRules.Resolve(targets.Where(t => t.ScopeType == TargetScope.Tenant).Select(Slice), load.Period);
        var users = targets.Where(t => t.ScopeType == TargetScope.User).Select(t => t.ScopeUserId!.Value).Distinct().ToList();
        var children = Sum(users);
        if (tenant.Value is not null)
        {
            return ("Tenant", tenant, children.Value is { } sum ? TargetRules.ChildExcess(tenant.Value.Value, sum) : null);
        }

        return tenant.Problem is not null ? ("Tenant", tenant, null) : children.Value is not null || children.Problem is not null ? ("UserSum", children, null) : ("None", tenant, null);
    }

    private static List<string> Warnings(Loaded load, SalesPeriodMetrics metrics, List<string> targetWarnings)
    {
        var warnings = new List<string>();
        if (load.Truncated)
        {
            warnings.Add($"Okuma sınırına ({SalesOpportunityReader.MaxRows}) ulaşıldı; toplamlar eksik olabilir. Filtreyi daraltın.");
        }

        if (load.ScopeLimited)
        {
            warnings.Add("Veri kapsamınız kısıtlı: yalnızca yetkili olduğunuz fırsatlar toplanır.");
        }

        if (!load.AmountsVisible)
        {
            warnings.Add("Fırsat tutarı ve olasılığını görme izniniz yok; tutar toplamları gösterilmez, yalnızca adetler.");
            return warnings;
        }

        var missingRate = new[] { metrics.OpenPipeline, metrics.ExpectedOpen, metrics.WeightedOpen, metrics.Realized }.Max(m => m.MissingRateCount);
        if (missingRate > 0)
        {
            warnings.Add($"{missingRate} fırsatın kuru bulunamadı; bu tutarlar {load.Currency} toplamına girmedi. Kur tanımlayın.");
        }

        var missingAmount = new[] { metrics.OpenPipeline, metrics.ExpectedOpen, metrics.Realized }.Max(m => m.MissingAmountCount);
        if (missingAmount > 0)
        {
            warnings.Add($"{missingAmount} fırsatın tutarı yok; toplamlara girmedi.");
        }

        if (metrics.WeightedMissingProbabilityCount > 0)
        {
            warnings.Add($"{metrics.WeightedMissingProbabilityCount} açık fırsatın olasılığı yok; ağırlıklı tahmine girmedi (sıfır sayılmadı).");
        }

        if (metrics.OpenWithoutCloseDateCount > 0)
        {
            warnings.Add($"{metrics.OpenWithoutCloseDateCount} açık fırsatın beklenen kapanış tarihi yok; hiçbir dönemin beklenen satışına girmedi.");
        }

        if (metrics.OverdueOpenCount > 0)
        {
            warnings.Add($"{metrics.OverdueOpenCount} açık fırsatın beklenen kapanış tarihi geçmiş.");
        }

        warnings.AddRange(targetWarnings);
        return warnings;
    }
}
