using Modulix.Modules.Support.Lifecycle;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024 dilim 6: rapor istatistikleri, dönem, kovalar, SLA uyumu, tekrarlayan neden gruplaması ve düzeltici aksiyon gecikmesi (saf kurallar).</summary>
public class ReportRulesTests
{
    [Fact]
    public void An_empty_sample_has_no_statistics_and_never_a_made_up_zero()
    {
        var stats = ReportStats.Of([]);
        Assert.Equal(0, stats.Count);
        Assert.Null(stats.Mean);
        Assert.Null(stats.Median);
        Assert.Null(stats.P95);
        Assert.Null(ReportStats.Percent(0, 0));
    }

    [Fact]
    public void Median_and_nearest_rank_percentiles_are_computed_from_the_real_values()
    {
        // 1..10: medyan iki ortanın ortalaması (5,5); p90 = 9. değer; p95 = 10. değer (en yakın sıra, ara değer yok).
        var stats = ReportStats.Of([.. Enumerable.Range(1, 10).Select(i => (double)i).Reverse()]);
        Assert.Equal((10, 5.5, 5.5, 9d, 10d, 1d, 10d), (stats.Count, stats.Mean, stats.Median, stats.P90, stats.P95, stats.Min, stats.Max));

        var odd = ReportStats.Of([30, 10, 20]);
        Assert.Equal((20d, 20d, 30d, 30d), (odd.Mean, odd.Median, odd.P90, odd.P95));

        var single = ReportStats.Of([42.04]);
        Assert.Equal((1, 42d, 42d, 42d), (single.Count, single.Mean, single.Median, single.P95));
    }

    [Fact]
    public void Percentages_state_their_numerator_and_denominator_and_round_to_one_decimal()
    {
        Assert.Equal(66.7, ReportStats.Percent(2, 3));
        Assert.Equal(100d, ReportStats.Percent(5, 5));
        Assert.Equal(0d, ReportStats.Percent(0, 4));
        Assert.Null(ReportStats.Percent(3, 0));
    }

    [Fact]
    public void Sla_compliance_counts_only_decided_instances_in_the_denominator()
    {
        var counts = new SlaComplianceCounts(Met: 6, Missed: 2, Breached: 2, InProgress: 5, Cancelled: 3);
        Assert.Equal(10, counts.Decided);
        Assert.Equal(60d, counts.CompliancePercent);
        Assert.Null(new SlaComplianceCounts(0, 0, 0, 4, 1).CompliancePercent);
    }

    [Theory]
    [InlineData("Completed", false, "Met")]
    [InlineData("Completed", true, "Missed")]
    [InlineData("Running", true, "Breached")]
    [InlineData("Paused", true, "Breached")]
    [InlineData("Running", false, "InProgress")]
    [InlineData("Cancelled", false, "Cancelled")]
    [InlineData("Cancelled", true, "Cancelled")]
    public void An_sla_instance_outcome_follows_its_status_and_breach(string status, bool breached, string expected) =>
        Assert.Equal(expected, SlaOutcome.Of(status, breached ? DateTimeOffset.UtcNow : null));

    [Fact]
    public void The_period_defaults_to_the_last_thirty_days_and_rejects_reversed_future_or_too_long_ranges()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var (from, to, error) = ReportPeriod.Resolve(null, null, now);
        Assert.Null(error);
        Assert.Equal((new DateOnly(2026, 10, 3), new DateOnly(2026, 11, 1)), (from, to));

        Assert.NotNull(ReportPeriod.Resolve(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), now).Error);
        Assert.NotNull(ReportPeriod.Resolve(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 2), now).Error);
        Assert.Null(ReportPeriod.Resolve(new DateOnly(2025, 11, 1), new DateOnly(2026, 10, 31), now).Error); // 365 gün
        Assert.NotNull(ReportPeriod.Resolve(new DateOnly(2025, 10, 1), new DateOnly(2026, 11, 1), now).Error); // 397 gün
        Assert.Null(ReportPeriod.Resolve(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), now).Error); // tek gün
    }

    [Fact]
    public void Days_follow_turkey_time_so_a_late_evening_ticket_belongs_to_the_local_day()
    {
        // 31 Ekim 22:30 UTC = 1 Kasım 01:30 Türkiye.
        var moment = new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 1), ReportPeriod.LocalDate(moment));
        var start = ReportPeriod.StartOf(new DateOnly(2026, 11, 1));
        Assert.True(moment >= start);
        Assert.True(moment < ReportPeriod.EndExclusive(new DateOnly(2026, 11, 1)));
        Assert.Equal(TimeSpan.FromHours(24), ReportPeriod.EndExclusive(new DateOnly(2026, 11, 1)) - start);
    }

    [Theory]
    [InlineData("2026-11-04", "day", "2026-11-04")]
    [InlineData("2026-11-04", "week", "2026-11-02")]
    [InlineData("2026-11-01", "week", "2026-10-26")]
    [InlineData("2026-11-02", "week", "2026-11-02")]
    [InlineData("2026-11-19", "month", "2026-11-01")]
    public void Series_buckets_start_on_monday_or_the_first_of_the_month(string day, string granularity, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), ReportPeriod.Bucket(DateOnly.Parse(day), granularity));

    [Theory]
    [InlineData(0.2, "lt1")]
    [InlineData(1.0, "d1-3")]
    [InlineData(2.99, "d1-3")]
    [InlineData(3.0, "d3-7")]
    [InlineData(13.9, "d7-14")]
    [InlineData(14.0, "d14-30")]
    [InlineData(30.0, "gt30")]
    [InlineData(400, "gt30")]
    public void Backlog_ages_fall_into_half_open_buckets(double ageDays, string expected) => Assert.Equal(expected, BacklogAge.KeyOf(ageDays));

    [Fact]
    public void Small_csat_samples_are_flagged_but_never_hidden()
    {
        Assert.True(CsatRules.IsSmallSample(0));
        Assert.True(CsatRules.IsSmallSample(CsatRules.MinReliableSample - 1));
        Assert.False(CsatRules.IsSmallSample(CsatRules.MinReliableSample));
    }

    [Theory]
    [InlineData("  Kargo   Firması\tGeç Teslim. ", "kargo firması geç teslim")]
    [InlineData("KARGO FİRMASI GEÇ TESLİM", "kargo firması geç teslim")]
    [InlineData("- Eksik   paket;", "eksik paket")]
    [InlineData("   ", "")]
    public void Root_causes_that_differ_only_in_case_spacing_or_edge_punctuation_group_together(string cause, string expected) =>
        Assert.Equal(expected, RootCauseGrouping.Normalize(cause));

    [Fact]
    public void Different_root_causes_stay_apart_and_long_ones_are_cut_to_a_stable_key()
    {
        Assert.NotEqual(RootCauseGrouping.Normalize("Kargo gecikti"), RootCauseGrouping.Normalize("Depo gecikti"));
        Assert.Equal(RootCauseGrouping.MaxKeyLength, RootCauseGrouping.Normalize(new string('a', 500)).Length);
    }

    [Fact]
    public void An_open_action_is_overdue_only_after_its_due_day_and_closed_ones_never_are()
    {
        var today = new DateOnly(2026, 11, 10);
        Assert.False(CorrectiveActionStatus.IsOverdue("Open", today, today));
        Assert.True(CorrectiveActionStatus.IsOverdue("Open", today.AddDays(-1), today));
        Assert.False(CorrectiveActionStatus.IsOverdue("Done", today.AddDays(-30), today));
        Assert.False(CorrectiveActionStatus.IsOverdue("Cancelled", today.AddDays(-30), today));
        Assert.Equal(4, CorrectiveActionStatus.DaysOverdue(today.AddDays(-4), today));
        Assert.Equal(0, CorrectiveActionStatus.DaysOverdue(today.AddDays(5), today));
    }
}
