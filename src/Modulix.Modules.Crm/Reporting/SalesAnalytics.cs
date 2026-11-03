using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Quotes;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Reporting;

/// <param name="AsOf">Kesim günü (dahil, firma saat diliminde); boşsa şimdi. Gelecek tarih şimdiye çekilir.</param>
public sealed record AnalyticsQuery(PeriodKind Kind, DateOnly? Anchor, DateOnly? AsOf, Guid? OwnerId, string? Currency);

/// <summary>Analiz zarfı: dönem, kapsam ve kısmi veri uyarılarıyla birlikte sonuç. Alan adları Shared'daki <c>AnalyticsDto&lt;T&gt;</c> ile birebir aynıdır.</summary>
public sealed record AnalyticsResult<T>(
    string PeriodKind,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Today,
    string Currency,
    bool AmountsVisible,
    bool ScopeLimited,
    bool Truncated,
    T Data,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Definitions);

public sealed record TeamPerformanceRow(OwnerPerformance Performance, decimal? Target, TargetProgress? Progress);

public sealed record DataQualityData(int OpenCount, IReadOnlyList<QualityIssue> Issues, int StaleDays);

public interface ISalesAnalytics
{
    Task<RecordResult<AnalyticsResult<QuoteCohortResult>>> QuoteCohortAsync(AnalyticsQuery query, CancellationToken cancellationToken);

    Task<RecordResult<AnalyticsResult<LeadCohortResult>>> LeadCohortAsync(AnalyticsQuery query, CancellationToken cancellationToken);

    Task<RecordResult<AnalyticsResult<StageFlowResult>>> StageFlowAsync(AnalyticsQuery query, CancellationToken cancellationToken);

    Task<RecordResult<AnalyticsResult<IReadOnlyList<TeamPerformanceRow>>>> TeamPerformanceAsync(AnalyticsQuery query, CancellationToken cancellationToken);

    Task<RecordResult<AnalyticsResult<DataQualityData>>> DataQualityAsync(AnalyticsQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Satış analizleri (ADR-0026, dilim 2): teklif ve lead kohortları, tarihsel aşama dönüşümü, kullanıcı bazında performans ve veri kalitesi. Veri kayıt boru hattından okunur
/// (izin, veri kapsamı, tutar/olasılık hassas alanı); tarihçe tabanlı aşama akışı yalnızca fırsatın <b>bugünkü</b> sorumlusu çağıranın kapsamındaki fırsatları sayar.
/// Hesap saf işlevlerdedir (<see cref="QuoteCohort"/>, <see cref="LeadCohort"/>, <see cref="StageFlow"/>, <see cref="TeamPerformance"/>, <see cref="DataQuality"/>).
/// </summary>
internal sealed class SalesAnalytics(
    SalesOpportunityReader reader, SalesPlanningData planning, IChoiceLists choiceLists, ICurrentAccess access, IRecordAccess recordAccess, TimeProvider time,
    ISalesReportingPolicyProvider? policyProvider = null, IOpportunityPolicyProvider? opportunityPolicy = null) : ISalesAnalytics
{
    public static IReadOnlyList<string> QuoteDefinitions { get; } =
    [
        "Birim teklif ailesidir: revizyonlar tek aile sayılır. Kohort, ailenin ilk gönderilme anı seçili dönemde olanlardır; hiç gönderilmemiş taslaklar girmez.",
        "Sonuç kesim anına göredir: kesimden sonra kabul/ret edilen aile kesimde Beklemede sayılır. Kabul oranı = kesime kadar kabul edilen aile / kohorttaki aile.",
        "Aile oranı ile revizyon oranı birlikte okunmalıdır: revizyon oranı = kabul edilen revizyon / gönderilen revizyon; çok revizyonlu aileler aile oranını şişirmez ama revizyon oranını düşürür.",
        "Kabul süresi = ilk gönderilmeden kabule gün. Kabul edilen net tutar (vergi hariç) kabul gününün kuruyla raporlama para birimine çevrilir; kuru olmayan toplama girmez.",
        "Kohort, dönem bitmeden olgun değildir; sonuçlar yeni kabul/retle değişebilir.",
    ];

    public static IReadOnlyList<string> LeadDefinitions { get; } =
    [
        "Kohort, alınma anı seçili dönemde olan lead'lerdir. Dönüşüm = kesime kadar fırsata dönüşen tekil müşteri adayı (bir müşteri adayından çok fırsat çıksa da bir kez).",
        "İki oran: tüm kohorta göre ve uygun müşteri adaylarına göre (elenenler hariç). Elenen sayısı güncel durumdan gelir (eleme tarihi kesime göre ayrıştırılmaz).",
        "Kohort, bitişinden sonra 30 gün geçmeden olgun sayılmaz; olgunlaşmamış kohortun oranı yanıltıcı olabilir.",
    ];

    public static IReadOnlyList<string> StageDefinitions { get; } =
    [
        "Aşama dönüşümü fırsat tarihçesinden hesaplanır; güncel aşama dağılımı değildir. Kohort, oluşturulma anı seçili dönemde olan fırsatlardır.",
        "Bir aşamaya hiç girmiş tekil fırsat sayılır; geri dönülen ya da tekrar girilen aşama bir kez, atlanan aşama hiç sayılmaz.",
        "İlerleme = daha ileri sıradaki (kaybedilen hariç) bir aşamaya girmiş olmak. Kaybedilen fırsat kaybedilmeden önce girdiği son açık aşamaya yazılır.",
        "Tarihçe izlemesi başlamadan önce açılmış fırsatlarda geçmiş yoktur ve üretilmez; izleme başlangıcı yanıtta verilir.",
    ];

    public static IReadOnlyList<string> TeamDefinitions { get; } =
    [
        "Her satır fırsatın bugünkü sorumlusunadır (tarihsel sahiplik görünümü yoktur). Sayılar satış genel görünümüyle aynı hesaptandır; yalnızca yetkili olduğunuz fırsatlar katılır.",
        "Döngü = kazanılmış fırsatta gerçek kapanış − oluşturulma günü. Ortalama yanında medyan ve örneklem adedi verilir; tarihçesi olmayan fırsat örnekleme girmez.",
        "Hedef, kullanıcı hedefidir (dönemi boşluksuz kaplayan hedef dilimleri); tanımlı değilse oran yoktur. Açık fırsat sayısı iş yükünü gösterir; aktivite sayısı başarı ölçütü değildir ve bu ekranda yoktur.",
        "Küçük örneklemde oran ve ortalama yanıltıcıdır; kazanılan/kaybedilen adetlerine birlikte bakın.",
    ];

    public static IReadOnlyList<string> QualityDefinitions { get; } =
    [
        "Yalnızca durumu Açık fırsatlar denetlenir. Her sorun ayrı sayılır; bir fırsat birden çok sorun taşıyabilir.",
        "Kuru bulunamayan tutar raporlama toplamlarına girmez (1 varsayılmaz). Hareketsiz = son müşteri etkileşimi yok ya da eşikten eski (eşik fırsat ayarlarından).",
        "Tarihçesi olmayan fırsat izleme başlamadan önce açılmıştır; aşama süresi ve dönüşümü hesaplanamaz.",
    ];

    // ---- Teklif kohortu

    public async Task<RecordResult<AnalyticsResult<QuoteCohortResult>>> QuoteCohortAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(query, cancellationToken);
        var filters = SalesOpportunityReader.Filters(query.OwnerId, "presentedTo", SalesOpportunityReader.Iso(DateOnly.FromDateTime(ctx.AsOf.UtcDateTime)));
        var page = await reader.ReadRawAsync(CrmModule.Entities.Quote, filters, cancellationToken);
        if (page.Error is not null)
        {
            return Forward<QuoteCohortResult>(page.Error);
        }

        var facts = page.Rows.Select(ToQuoteFact).ToList();
        var rates = new RateBook(await planning.RatesAsync(cancellationToken));
        var data = QuoteCohort.Compute(facts, ctx.From, ctx.ToExclusive, ctx.AsOf, ctx.Currency, rates, ctx.Policy.TimeZone);
        var warnings = Common(ctx, page.Truncated, ctx.AmountsVisible);
        if (!data.Mature)
        {
            warnings.Add("Dönem henüz bitmedi ya da kesim dönem sonundan önce: kohort olgun değil, oranlar değişebilir.");
        }

        if (data.AcceptedNet.MissingRateCount > 0)
        {
            warnings.Add($"{data.AcceptedNet.MissingRateCount} kabul edilen teklifin kuru bulunamadı; net tutar toplamına girmedi.");
        }

        return Ok(ctx, data, warnings, QuoteDefinitions);
    }

    // ---- Lead kohortu

    public async Task<RecordResult<AnalyticsResult<LeadCohortResult>>> LeadCohortAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(query, cancellationToken);
        var filters = SalesOpportunityReader.Filters(
            query.OwnerId, "receivedFrom", ctx.From.ToString("O", CultureInfo.InvariantCulture), "receivedBefore", ctx.ToExclusive.ToString("O", CultureInfo.InvariantCulture));
        var page = await reader.ReadRawAsync(CrmModule.Entities.Lead, filters, cancellationToken);
        if (page.Error is not null)
        {
            return Forward<LeadCohortResult>(page.Error);
        }

        var data = LeadCohort.Compute([.. page.Rows.Select(ToLeadFact)], ctx.From, ctx.ToExclusive, ctx.AsOf);
        var warnings = Common(ctx, page.Truncated, amounts: true);
        if (!data.Mature)
        {
            warnings.Add($"Kohort olgun değil (bitişinden sonra {data.MaturityDays} gün geçmedi): dönüşüm oranı sonradan artabilir.");
        }

        return Ok(ctx, data, warnings, LeadDefinitions);
    }

    // ---- Aşama akışı

    public async Task<RecordResult<AnalyticsResult<StageFlowResult>>> StageFlowAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(query, cancellationToken);
        if (!await access.HasPermissionAsync(CrmModule.Permissions.OpportunitiesRead, cancellationToken))
        {
            return RecordResult<AnalyticsResult<StageFlowResult>>.Fail(RecordErrorCode.PermissionDenied, $"Bu analiz için '{CrmModule.Permissions.OpportunitiesRead}' izni gerekir.");
        }

        var list = await choiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
        var stages = list.Values.Select((v, i) => new StageDef(v.Key, i, v.Category ?? StageFlow.OpenCategory)).ToList();

        var created = await planning.CreatedEventsAsync(ctx.From, ctx.ToExclusive, cancellationToken);
        var ids = created.Select(e => e.OpportunityId).Distinct().ToList();
        var owners = await planning.CurrentOwnersAsync(ids, cancellationToken);
        var scope = ctx.Owners;
        var visible = ids.Where(id => owners.TryGetValue(id, out var owner)
            && (scope is null || (owner is { } o && scope.Contains(o)))
            && (query.OwnerId is not { } wanted || owner == wanted)).ToList();

        var history = await planning.HistoryAsync(visible, cancellationToken);
        var byOpportunity = history.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(h => h.Kind != OpportunityHistoryKinds.CloseDate && h.ToStage is not null).Select(h => new StageEvent(h.OpportunityId, h.At, h.ToStage)).ToList());

        var tracking = await planning.TrackingStartAsync(cancellationToken);
        var data = StageFlow.Compute(stages, byOpportunity, ctx.From, ctx.ToExclusive, ctx.AsOf, tracking);
        var warnings = Common(ctx, truncated: false, amounts: true);
        if (tracking is null || tracking > ctx.From)
        {
            warnings.Add(tracking is null
                ? "Fırsat tarihçesi kaydı yok; aşama dönüşümü üretilemez."
                : $"Tarihçe izlemesi {ReportPeriods.Today(tracking.Value, ctx.Policy.TimeZone):yyyy-MM-dd} tarihinde başladı; öncesinde açılan fırsatlar bu analize girmez (geriye dönük üretilmez).");
        }

        if (!ctx.IsPeriodEnded)
        {
            warnings.Add("Dönem henüz bitmedi: kohortun fırsatları hâlâ ilerliyor olabilir.");
        }

        return Ok(ctx, data, warnings, StageDefinitions);
    }

    // ---- Ekip performansı

    public async Task<RecordResult<AnalyticsResult<IReadOnlyList<TeamPerformanceRow>>>> TeamPerformanceAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(query, cancellationToken);
        var open = await reader.ReadAsync(SalesOpportunityReader.Filters(query.OwnerId, "status", OpportunityStatus.Open), cancellationToken);
        var closed = await reader.ReadAsync(SalesOpportunityReader.Filters(
            query.OwnerId, "actualCloseFrom", SalesOpportunityReader.Iso(ctx.Period.Start), "actualCloseTo", SalesOpportunityReader.Iso(ctx.Period.LastDay)), cancellationToken);
        var error = open.Error ?? closed.Error;
        if (error is not null)
        {
            return Forward<IReadOnlyList<TeamPerformanceRow>>(error);
        }

        var rows = open.Rows.Concat(closed.Rows.Where(r => r.Status != OpportunityStatus.Open)).ToList();
        var wonIds = SalesMetrics.Select(rows.Select(r => r.ToFact()).ToList(), SalesCard.Realized, ctx.Period).Select(f => f.Id).ToList();
        var history = await planning.HistoryAsync(wonIds, cancellationToken);
        int? Cycle(OpportunityRow row) =>
            wonIds.Contains(row.Id) && row.ActualCloseDate is { } close && history.TryGetValue(row.Id, out var events) && events.FirstOrDefault() is { } first
                ? close.DayNumber - ReportPeriods.Today(first.At, ctx.Policy.TimeZone).DayNumber
                : null;

        var rates = new RateBook(await planning.RatesAsync(cancellationToken));
        var performance = TeamPerformance.Compute([.. rows.Select(r => new OwnerFact(r.OwnerId, r.ToFact(), Cycle(r)))], ctx.Period, ctx.Currency, rates, ctx.Today);

        var showTargets = ctx.AmountsVisible && await access.HasPermissionAsync(CrmModule.Permissions.SalesTargetsRead, cancellationToken);
        var targets = showTargets ? await planning.TargetsOverlappingAsync(ctx.Period.Start, ctx.Period.LastDay, cancellationToken) : [];
        var result = new List<TeamPerformanceRow>();
        foreach (var p in performance)
        {
            decimal? target = null;
            TargetProgress? progress = null;
            if (p.OwnerId is { } owner && showTargets)
            {
                var slices = targets.Where(t => t.Metric == TargetMetric.WonAmount && t.ScopeType == TargetScope.User && t.ScopeUserId == owner && t.Currency == ctx.Currency)
                    .Select(t => new TargetSlice(t.PeriodStart, t.PeriodEnd.AddDays(1), t.Value));
                target = TargetRules.Resolve(slices, ctx.Period).Value;
                progress = SalesMetrics.Progress(target, p.Realized.Amount, p.ExpectedOpen.Amount);
            }

            result.Add(new TeamPerformanceRow(ctx.AmountsVisible ? p : Hide(p), target, progress));
        }

        var warnings = Common(ctx, open.Truncated || closed.Truncated, ctx.AmountsVisible);
        if (result.Any(r => r.Performance.WonCount > 0 && r.Performance.CycleSample < r.Performance.WonCount))
        {
            warnings.Add("Bazı kazanılmış fırsatların tarihçesi olmadığından döngü süresi örnekleminde yer almıyor (örneklem adedi kazanılan adetten küçük olabilir).");
        }

        return Ok<IReadOnlyList<TeamPerformanceRow>>(ctx, result, warnings, TeamDefinitions);
    }

    // ---- Veri kalitesi

    public async Task<RecordResult<AnalyticsResult<DataQualityData>>> DataQualityAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var ctx = await ContextAsync(query, cancellationToken);
        var open = await reader.ReadAsync(SalesOpportunityReader.Filters(query.OwnerId, "status", OpportunityStatus.Open), cancellationToken);
        if (open.Error is not null)
        {
            return Forward<DataQualityData>(open.Error);
        }

        var withHistory = await planning.IdsWithHistoryAsync([.. open.Rows.Select(r => r.Id)], cancellationToken);
        var staleDays = (opportunityPolicy is null ? OpportunityPolicy.Default : await opportunityPolicy.GetAsync(cancellationToken)).StaleDays;
        var rates = new RateBook(await planning.RatesAsync(cancellationToken));
        var opportunities = open.Rows.Select(r => new QualityOpportunity(
            r.Id, r.Number, r.Title, r.OwnerId, r.Status, ctx.AmountsVisible ? r.Amount : null, r.Currency, ctx.AmountsVisible ? r.Probability : null,
            r.ExpectedCloseDate, r.LastInteractionAt, withHistory.Contains(r.Id))).ToList();

        // Tutar ve olasılık hassastır: izin yoksa bu üç sorun hesaplanamaz ve rapora girmez (eksik sanılmaz).
        var issues = DataQuality.Compute(opportunities, ctx.Today, ctx.Now, staleDays, ctx.Currency, rates)
            .Where(i => ctx.AmountsVisible || i.Code is not (DataQuality.MissingAmount or DataQuality.MissingRate or DataQuality.MissingProbability)).ToList();
        return Ok(ctx, new DataQualityData(open.Rows.Count, issues, staleDays), Common(ctx, open.Truncated, ctx.AmountsVisible), QualityDefinitions);
    }

    // ---- Ortak

    private sealed record Ctx(
        SalesReportingPolicy Policy, ReportPeriod Period, DateTimeOffset From, DateTimeOffset ToExclusive, DateTimeOffset AsOf, DateTimeOffset Now, DateOnly Today,
        string Currency, bool AmountsVisible, IReadOnlySet<Guid>? Owners)
    {
        public bool IsPeriodEnded => AsOf >= ToExclusive;
    }

    private async Task<Ctx> ContextAsync(AnalyticsQuery query, CancellationToken cancellationToken)
    {
        var policy = policyProvider is null ? SalesReportingPolicy.Default : await policyProvider.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var today = ReportPeriods.Today(now, policy.TimeZone);
        var period = ReportPeriods.Containing(query.Kind, query.Anchor ?? today, policy.FiscalStartMonth);
        var (from, toExclusive) = ReportPeriods.ToUtcRange(period, policy.TimeZone);

        // Kesim: verilen günün sonu (firma saat diliminde, dahil); gelecek ya da boşsa şimdi.
        var asOf = query.AsOf is { } day ? ReportPeriods.StartOfDayUtc(day.AddDays(1), policy.TimeZone) : now;
        if (asOf > now)
        {
            asOf = now;
        }

        return new Ctx(
            policy, period, from, toExclusive, asOf, now, today, (query.Currency ?? policy.DefaultCurrency).Trim().ToUpperInvariant(),
            await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken), await recordAccess.GetOwnersAsync(cancellationToken));
    }

    private static List<string> Common(Ctx ctx, bool truncated, bool amounts)
    {
        var warnings = new List<string>();
        if (truncated)
        {
            warnings.Add($"Okuma sınırına ({SalesOpportunityReader.MaxRows}) ulaşıldı; sonuçlar eksik olabilir. Filtreyi daraltın.");
        }

        if (ctx.Owners is not null)
        {
            warnings.Add("Veri kapsamınız kısıtlı: yalnızca yetkili olduğunuz kayıtlar hesaba girer.");
        }

        if (!amounts)
        {
            warnings.Add("Fırsat tutarı ve olasılığını görme izniniz yok; tutarlar gösterilmez, yalnızca adetler.");
        }

        return warnings;
    }

    private static OwnerPerformance Hide(OwnerPerformance p)
    {
        static MoneyTotal NoAmount(MoneyTotal t) => new(0m, t.Count, 0, 0);
        return p with { Realized = NoAmount(p.Realized), OpenPipeline = NoAmount(p.OpenPipeline), WeightedOpen = NoAmount(p.WeightedOpen), ExpectedOpen = NoAmount(p.ExpectedOpen) };
    }

    private static RecordResult<AnalyticsResult<T>> Ok<T>(Ctx ctx, T data, IReadOnlyList<string> warnings, IReadOnlyList<string> definitions) =>
        RecordResult<AnalyticsResult<T>>.Ok(new AnalyticsResult<T>(
            ctx.Period.Kind.ToString(), ctx.Period.Label, ctx.Period.Start, ctx.Period.LastDay, ctx.Today, ctx.Currency, ctx.AmountsVisible, ctx.Owners is not null,
            warnings.Any(w => w.StartsWith("Okuma sınırına", StringComparison.Ordinal)), data, warnings, definitions));

    private static RecordResult<AnalyticsResult<T>> Forward<T>(RecordError error) =>
        RecordResult<AnalyticsResult<T>>.Fail(error.Code, error.Message, error.FieldErrors);

    private static QuoteFact ToQuoteFact(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        DateTimeOffset? Time(string name) => DateTimeOffset.TryParse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        return new QuoteFact(
            record.Id, Guid.TryParse(Text("familyId"), out var family) ? family : record.Id, v["revisionNo"] is JsonValue n && n.TryGetValue<int>(out var no) ? no : 1,
            Text("status") ?? QuoteStatus.Draft, Time("presentedAt"), Time("decidedAt"), Guid.TryParse(Text("ownerId"), out var owner) ? owner : null,
            Text("currency") ?? Opportunity.DefaultCurrency, v["netTotal"] is JsonValue net && net.TryGetValue<decimal>(out var total) ? total : 0m);
    }

    private static LeadFact ToLeadFact(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        DateTimeOffset? Time(string name) => DateTimeOffset.TryParse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        return new LeadFact(
            record.Id, Text("status") ?? LeadStatus.New, Time("firstReceivedAt") ?? DateTimeOffset.MinValue, Time("convertedAt"),
            Guid.TryParse(Text("ownerId"), out var owner) ? owner : null, Text("source"));
    }
}
