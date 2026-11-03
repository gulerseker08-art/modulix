using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Quotes;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Teklif ve lead kohortları, aşama akışı ve ekip performansı (ADR-0026 dilim 2; veritabanı gerektirmez). Kabul senaryoları 3, 4, 10.</summary>
public class CohortMetricsTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cut = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static QuoteFact Rev(Guid family, int no, string status, DateTimeOffset? presented, DateTimeOffset? decided = null, decimal net = 100m) =>
        new(Guid.NewGuid(), family, no, status, presented, decided, null, "TRY", net);

    // ---- Teklif kohortu

    [Fact]
    public void Scenario_four_ten_families_four_accepted_eighteen_revisions_is_forty_percent_and_drafts_are_excluded()
    {
        var facts = new List<QuoteFact>();
        var day = From.AddDays(2);
        // 4 kabul: 1+2+3+2 = 8 revizyon; 6 diğer aile: 1+2+3+2+1+1 = 10 revizyon → toplam 18.
        int[] acceptedRevisions = [1, 2, 3, 2];
        foreach (var count in acceptedRevisions)
        {
            facts.AddRange(Family(count, QuoteStatus.Accepted, day));
        }

        facts.AddRange(Family(1, QuoteStatus.Rejected, day));
        facts.AddRange(Family(2, QuoteStatus.Rejected, day));
        facts.AddRange(Family(3, QuoteStatus.Cancelled, day));
        facts.AddRange(Family(2, QuoteStatus.Expired, day));
        facts.AddRange(Family(1, QuoteStatus.Presented, day));
        facts.AddRange(Family(1, QuoteStatus.Presented, day));
        // Hiç gönderilmemiş taslaklar kohorta girmez.
        facts.Add(Rev(Guid.NewGuid(), 1, QuoteStatus.Draft, null));
        facts.Add(Rev(Guid.NewGuid(), 1, QuoteStatus.Draft, null));

        var result = QuoteCohort.Compute(facts, From, To, Cut, "TRY", RateBook.Empty, Istanbul);

        Assert.Equal(10, result.Families);
        Assert.Equal(4, result.Accepted);
        Assert.Equal(40m, result.AcceptanceRatePercent);
        Assert.Equal(18, result.PresentedRevisions);
        Assert.Equal(1.8m, result.AverageRevisionsPerFamily);
        Assert.Equal((2, 1, 2, 1), (result.Rejected, result.Cancelled, result.Pending, result.Expired));
        Assert.True(result.Mature);
    }

    private static IEnumerable<QuoteFact> Family(int revisions, string finalStatus, DateTimeOffset firstPresented)
    {
        var id = Guid.NewGuid();
        for (var no = 1; no <= revisions; no++)
        {
            var last = no == revisions;
            var status = last ? finalStatus : QuoteStatus.Superseded;
            DateTimeOffset? decided = last && finalStatus != QuoteStatus.Presented ? firstPresented.AddDays(5) : null;
            yield return Rev(id, no, status, firstPresented.AddDays(no - 1), decided);
        }
    }

    [Fact]
    public void Acceptance_after_the_cutoff_leaves_the_family_pending_as_of_the_cutoff()
    {
        var family = Guid.NewGuid();
        var facts = new[] { Rev(family, 1, QuoteStatus.Accepted, From.AddDays(3), From.AddDays(20)) };

        var early = QuoteCohort.Compute(facts, From, To, From.AddDays(10), "TRY", RateBook.Empty, Istanbul);
        var late = QuoteCohort.Compute(facts, From, To, Cut, "TRY", RateBook.Empty, Istanbul);

        Assert.Equal((1, 0, 1), (early.Families, early.Accepted, early.Pending));
        Assert.False(early.Mature);
        Assert.Equal((1, 1, 0), (late.Families, late.Accepted, late.Pending));
        Assert.Equal(17m, late.AverageDaysToAccept);
    }

    [Fact]
    public void Family_belongs_to_the_cohort_of_its_first_presentation_not_of_a_later_revision()
    {
        var family = Guid.NewGuid();
        var facts = new[]
        {
            Rev(family, 1, QuoteStatus.Superseded, From.AddDays(-5)),
            Rev(family, 2, QuoteStatus.Accepted, From.AddDays(5), From.AddDays(6)),
        };

        Assert.Equal(0, QuoteCohort.Compute(facts, From, To, Cut, "TRY", RateBook.Empty, Istanbul).Families);
        var earlier = QuoteCohort.Compute(facts, From.AddDays(-30), From, Cut, "TRY", RateBook.Empty, Istanbul);
        Assert.Equal((1, 1), (earlier.Families, earlier.Accepted));
    }

    [Fact]
    public void Revision_rate_is_lower_than_family_rate_when_revisions_pile_up_and_accepted_net_is_converted()
    {
        var family = Guid.NewGuid();
        var facts = new[]
        {
            Rev(family, 1, QuoteStatus.Superseded, From.AddDays(1)),
            Rev(family, 2, QuoteStatus.Superseded, From.AddDays(2)),
            Rev(family, 3, QuoteStatus.Accepted, From.AddDays(3), From.AddDays(4), net: 10m) with { Currency = "USD" },
        };
        var rates = new RateBook([new RateEntry("USD", "TRY", 30m, new DateOnly(2026, 10, 1))]);

        var result = QuoteCohort.Compute(facts, From, To, Cut, "TRY", rates, Istanbul);

        Assert.Equal(100m, result.AcceptanceRatePercent);
        Assert.Equal(33.33m, result.RevisionAcceptanceRatePercent);
        Assert.Equal(300m, result.AcceptedNet.Amount);
        var missing = QuoteCohort.Compute(facts, From, To, Cut, "TRY", RateBook.Empty, Istanbul);
        Assert.Equal((0m, 1), (missing.AcceptedNet.Amount, missing.AcceptedNet.MissingRateCount));
    }

    [Fact]
    public void Empty_cohort_has_no_rates()
    {
        var result = QuoteCohort.Compute([], From, To, Cut, "TRY", RateBook.Empty, Istanbul);

        Assert.Equal(0, result.Families);
        Assert.Null(result.AcceptanceRatePercent);
        Assert.Null(result.AverageRevisionsPerFamily);
        Assert.Null(result.AverageDaysToAccept);
    }

    // ---- Lead kohortu

    private static LeadFact Lead(string status, DateTimeOffset received, DateTimeOffset? converted = null, string? source = "web") =>
        new(Guid.NewGuid(), status, received, converted, null, source);

    [Fact]
    public void Lead_conversion_counts_unique_leads_and_reports_both_rates()
    {
        var facts = new[]
        {
            Lead(LeadStatus.Converted, From.AddDays(1), From.AddDays(4)),
            Lead(LeadStatus.Converted, From.AddDays(2), From.AddDays(12), "fuar"),
            Lead(LeadStatus.Disqualified, From.AddDays(3)),
            Lead(LeadStatus.New, From.AddDays(4)),
            Lead(LeadStatus.Converted, From.AddDays(-3), From.AddDays(1)),   // kohort dışı
        };
        var duplicate = facts[0];

        var result = LeadCohort.Compute([.. facts, duplicate], From, To, Cut);

        Assert.Equal((4, 2, 1, 1), (result.Leads, result.Converted, result.Disqualified, result.Open));
        Assert.Equal(50m, result.RateOfAllPercent);
        Assert.Equal(66.67m, result.RateOfEligiblePercent);
        Assert.Equal(6.5m, result.AverageDaysToConvert);
        Assert.Equal(6.5m, result.MedianDaysToConvert);
        Assert.Equal(2, result.BySource.Count);
    }

    [Fact]
    public void Lead_cohort_is_immature_until_the_maturity_window_passes_and_late_conversion_does_not_count()
    {
        var facts = new[] { Lead(LeadStatus.Converted, From.AddDays(1), To.AddDays(5)) };

        var soon = LeadCohort.Compute(facts, From, To, To.AddDays(2));
        var later = LeadCohort.Compute(facts, From, To, To.AddDays(40));

        Assert.False(soon.Mature);
        Assert.Equal(0, soon.Converted);
        Assert.True(later.Mature);
        Assert.Equal(1, later.Converted);
        Assert.Null(LeadCohort.Compute([], From, To, Cut).RateOfAllPercent);
    }

    // ---- Aşama akışı

    private static readonly StageDef[] Stages =
    [
        new("Lead", 0, "open"), new("Qualified", 1, "open"), new("Proposal", 2, "open"), new("Negotiation", 3, "open"),
        new("Won", 4, "won"), new("Lost", 5, "lost"),
    ];

    private static List<StageEvent> Path(Guid id, params string[] stages) =>
        [.. stages.Select((s, i) => new StageEvent(id, From.AddDays(i + 1), s))];

    [Fact]
    public void Stage_flow_counts_unique_opportunities_ignores_skips_and_does_not_count_loss_as_progress()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var paths = new Dictionary<Guid, List<StageEvent>>
        {
            [a] = Path(a, "Lead", "Qualified", "Proposal", "Won"),
            // Geri dönüş ve tekrar: Qualified iki kez girilir ama tek fırsat olarak sayılır; Proposal atlanır.
            [b] = Path(b, "Lead", "Qualified", "Lead", "Qualified", "Negotiation", "Lost"),
            [c] = Path(c, "Lead", "Qualified", "Lost"),
            [d] = Path(d, "Lead"),
        };

        var result = StageFlow.Compute(Stages, paths, From, To, Cut, From);

        Assert.Equal(4, result.Cohort);
        var lead = result.Rows.Single(r => r.Stage == "Lead");
        Assert.Equal((4, 3, 75m, 1), (lead.Entered, lead.Advanced, lead.AdvanceRatePercent, lead.StillHere));
        var qualified = result.Rows.Single(r => r.Stage == "Qualified");
        Assert.Equal((3, 2, 1), (qualified.Entered, qualified.Advanced, qualified.LostFrom));   // c kayıp ilerleme sayılmaz
        var proposal = result.Rows.Single(r => r.Stage == "Proposal");
        Assert.Equal((1, 1, 1), (proposal.Entered, proposal.Advanced, proposal.WonFrom));        // b Proposal'a hiç girmedi
        var negotiation = result.Rows.Single(r => r.Stage == "Negotiation");
        Assert.Equal((1, 0, 1), (negotiation.Entered, negotiation.Advanced, negotiation.LostFrom));
    }

    [Fact]
    public void Stage_flow_without_history_invents_nothing()
    {
        var result = StageFlow.Compute(Stages, new Dictionary<Guid, List<StageEvent>>(), From, To, Cut, null);

        Assert.Equal(0, result.Cohort);
        Assert.Null(result.TrackingStart);
        Assert.All(result.Rows, r => Assert.Equal((0, null), (r.Entered, r.AdvanceRatePercent)));
    }

    [Fact]
    public void Stage_flow_ignores_events_after_the_cutoff()
    {
        var a = Guid.NewGuid();
        var paths = new Dictionary<Guid, List<StageEvent>> { [a] = Path(a, "Lead", "Qualified") };

        var result = StageFlow.Compute(Stages, paths, From, To, From.AddDays(1.5), From);

        Assert.Equal(1, result.Rows.Single(r => r.Stage == "Lead").Entered);
        Assert.Equal(0, result.Rows.Single(r => r.Stage == "Qualified").Entered);
    }

    // ---- Ekip performansı

    private static OwnerFact Fact(Guid? owner, string status, decimal? amount, DateOnly? expected, DateOnly? actual, int? cycle = null) =>
        new(owner, new OpportunityFact(Guid.NewGuid(), status, amount, "TRY", status == OpportunityStatus.Open ? 50 : 100, expected, actual, null, null), cycle);

    [Fact]
    public void Team_performance_groups_by_owner_with_cycle_sample_and_median()
    {
        var today = new DateOnly(2026, 11, 2);
        var period = ReportPeriods.Containing(PeriodKind.Month, today);
        var ayse = Guid.NewGuid();
        var can = Guid.NewGuid();
        var facts = new[]
        {
            Fact(ayse, OpportunityStatus.Won, 100m, null, new DateOnly(2026, 11, 1), cycle: 10),
            Fact(ayse, OpportunityStatus.Won, 300m, null, new DateOnly(2026, 11, 2), cycle: 40),
            Fact(ayse, OpportunityStatus.Won, 50m, null, new DateOnly(2026, 11, 2), cycle: 20),
            Fact(ayse, OpportunityStatus.Lost, 10m, null, new DateOnly(2026, 11, 2)),
            Fact(ayse, OpportunityStatus.Open, 200m, new DateOnly(2026, 10, 20), null),
            Fact(can, OpportunityStatus.Open, 500m, new DateOnly(2026, 11, 20), null),
        };

        var result = TeamPerformance.Compute(facts, period, "TRY", RateBook.Empty, today);

        var a = result.Single(r => r.OwnerId == ayse);
        Assert.Equal((450m, 3, 1, 75m), (a.Realized.Amount, a.WonCount, a.LostCount, a.WinRatePercent));
        Assert.Equal((3, 23.3m, 20m), (a.CycleSample, a.AverageCycleDays, a.MedianCycleDays));
        Assert.Equal((1, 1), (a.OpenCount, a.OverdueOpenCount));
        var c = result.Single(r => r.OwnerId == can);
        Assert.Equal((0, null, null, null), (c.CycleSample, c.AverageCycleDays, c.MedianCycleDays, c.WinRatePercent));
        Assert.Equal(500m, c.OpenPipeline.Amount);
        Assert.Equal(ayse, result[0].OwnerId);   // gerçekleşene göre azalan
    }

    [Fact]
    public void Median_of_even_and_odd_samples()
    {
        Assert.Equal(2m, Stats.Median([1m, 2m, 9m]));
        Assert.Equal(2.5m, Stats.Median([1m, 2m, 3m, 9m]));
    }
}

/// <summary>Veri kalitesi denetimi (ADR-0026 dilim 2; kabul senaryosu 10).</summary>
public class DataQualityTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static QualityOpportunity Open(
        string number, decimal? amount = 100m, string currency = "TRY", int? probability = 50, DateOnly? close = null, DateTimeOffset? last = null, bool history = true,
        string status = OpportunityStatus.Open) =>
        new(Guid.NewGuid(), number, number, null, status, amount, currency, probability, close ?? Today.AddDays(5), last ?? Now.AddDays(-1), history);

    [Fact]
    public void Each_problem_is_counted_separately_and_closed_opportunities_are_ignored()
    {
        var rows = new[]
        {
            Open("A", amount: null),
            Open("B", currency: "USD"),
            Open("C", probability: null),
            Open("D", close: null) with { ExpectedCloseDate = null },
            Open("E", close: Today.AddDays(-3)),
            Open("F", last: Now.AddDays(-90)),
            Open("G", last: null) with { LastInteractionAt = null },
            Open("H", history: false),
            Open("X", amount: null, status: OpportunityStatus.Won),
        };

        var issues = DataQuality.Compute(rows, Today, Now, 30, "TRY", RateBook.Empty).ToDictionary(i => i.Code);

        Assert.Equal(1, issues[DataQuality.MissingAmount].Count);
        Assert.Equal(1, issues[DataQuality.MissingRate].Count);
        Assert.Equal("USD → TRY kuru yok", issues[DataQuality.MissingRate].Samples.Single().Detail);
        Assert.Equal(1, issues[DataQuality.MissingProbability].Count);
        Assert.Equal(1, issues[DataQuality.MissingCloseDate].Count);
        Assert.Equal(1, issues[DataQuality.Overdue].Count);
        Assert.Equal(2, issues[DataQuality.Stale].Count);
        Assert.Equal(1, issues[DataQuality.NoHistory].Count);
        Assert.DoesNotContain(issues.Values.SelectMany(i => i.Samples), s => s.Number == "X");
    }

    [Fact]
    public void A_defined_rate_clears_the_missing_rate_problem_and_samples_are_limited()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Open($"R{i:00}", currency: "USD")).ToList();
        var rates = new RateBook([new RateEntry("USD", "TRY", 30m, Today.AddDays(-1))]);

        Assert.Equal(0, DataQuality.Compute(rows, Today, Now, 30, "TRY", rates).Single(i => i.Code == DataQuality.MissingRate).Count);
        var without = DataQuality.Compute(rows, Today, Now, 30, "TRY", RateBook.Empty, sampleLimit: 5).Single(i => i.Code == DataQuality.MissingRate);
        Assert.Equal((30, 5), (without.Count, without.Samples.Count));
    }
}
