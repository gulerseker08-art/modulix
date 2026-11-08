using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Satış metrikleri, dönem/mali takvim, kur ve hedef kuralları (ADR-0026; veritabanı gerektirmez). Kabul senaryoları 1, 2, 3, 8 ve 10.</summary>
public class SalesMetricsTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly ReportPeriod November = ReportPeriods.Containing(PeriodKind.Month, Today);

    private static OpportunityFact Open(decimal? amount, int? probability, DateOnly? close, string currency = "TRY") =>
        new(Guid.NewGuid(), OpportunityStatus.Open, amount, currency, probability, close, null, null, null);

    private static OpportunityFact Won(decimal? amount, DateOnly close, string currency = "TRY", decimal? wonAmount = null) =>
        new(Guid.NewGuid(), OpportunityStatus.Won, amount, currency, 100, close, close, wonAmount, wonAmount is null ? null : currency);

    private static OpportunityFact Lost(DateOnly close) =>
        new(Guid.NewGuid(), OpportunityStatus.Lost, 50_000m, "TRY", 0, close, close, null, null);

    [Fact]
    public void Scenario_one_period_outlook_counts_won_once_and_leaves_next_month_out()
    {
        var facts = new[]
        {
            Open(100_000m, 40, new DateOnly(2026, 11, 20)),
            Open(200_000m, 70, new DateOnly(2026, 11, 25)),
            Won(150_000m, new DateOnly(2026, 11, 1)),
            Open(300_000m, 50, new DateOnly(2026, 12, 5)),
        };

        var metrics = SalesMetrics.Compute(facts, November, "TRY", RateBook.Empty, Today);

        Assert.Equal(300_000m, metrics.ExpectedOpen.Amount);
        Assert.Equal(180_000m, metrics.WeightedOpen.Amount);
        Assert.Equal(150_000m, metrics.Realized.Amount);
        Assert.Equal(330_000m, metrics.Outlook);
        Assert.Equal(600_000m, metrics.OpenPipeline.Amount);
        Assert.False(metrics.IsPartial);
    }

    [Fact]
    public void Comparison_window_takes_the_same_number_of_days_from_the_previous_period()
    {
        var quarter = ReportPeriods.Containing(PeriodKind.Quarter, new DateOnly(2026, 11, 7));
        var previous = ReportPeriods.Shift(quarter, -1);

        var window = SalesMetrics.ComparisonWindow(quarter, previous, new DateOnly(2026, 11, 7))!;

        Assert.Equal(new DateOnly(2026, 7, 1), window.Start);
        Assert.Equal(new DateOnly(2026, 8, 7), window.LastDay);
    }

    [Fact]
    public void Comparison_window_is_the_whole_previous_period_once_the_period_is_over_and_absent_before_it_starts()
    {
        var previous = ReportPeriods.Shift(November, -1);

        Assert.Equal(previous, SalesMetrics.ComparisonWindow(November, previous, new DateOnly(2026, 12, 3)));
        Assert.Null(SalesMetrics.ComparisonWindow(November, previous, new DateOnly(2026, 10, 30)));
    }

    [Fact]
    public void Comparison_window_does_not_run_past_a_shorter_previous_period()
    {
        var march = ReportPeriods.Containing(PeriodKind.Month, new DateOnly(2026, 3, 31));
        var february = ReportPeriods.Shift(march, -1);

        var window = SalesMetrics.ComparisonWindow(march, february, new DateOnly(2026, 3, 31))!;

        Assert.Equal(new DateOnly(2026, 2, 28), window.LastDay);
    }

    [Fact]
    public void Average_days_in_stage_counts_from_the_entry_day_and_is_empty_without_entries()
    {
        Assert.Equal(7.5m, SalesMetrics.AverageDays([Today.AddDays(-5), Today.AddDays(-10)], Today));
        Assert.Equal(0m, SalesMetrics.AverageDays([Today.AddDays(2)], Today));
        Assert.Null(SalesMetrics.AverageDays([], Today));
    }

    [Fact]
    public void Scenario_two_target_progress_remaining_and_coverage()
    {
        var progress = SalesMetrics.Progress(500_000m, 150_000m, 300_000m);

        Assert.NotNull(progress);
        Assert.Equal(30m, progress.AttainmentPercent);
        Assert.Equal(350_000m, progress.Remaining);
        Assert.Equal(0.857m, progress.Coverage);
        Assert.False(progress.TargetMet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Missing_or_zero_target_has_no_ratios(int? target)
    {
        Assert.Null(SalesMetrics.Progress(target, 100m, 50m));
    }

    [Fact]
    public void Exceeded_target_is_met_and_has_no_infinite_coverage()
    {
        var progress = SalesMetrics.Progress(100_000m, 120_000m, 40_000m);

        Assert.NotNull(progress);
        Assert.True(progress.TargetMet);
        Assert.Null(progress.Coverage);
        Assert.Equal(120m, progress.AttainmentPercent);
        Assert.Equal(-20_000m, progress.Remaining);
    }

    [Fact]
    public void Scenario_three_win_rate_excludes_open_opportunities()
    {
        var day = new DateOnly(2026, 11, 3);
        var facts = new[]
        {
            Won(10m, day), Won(10m, day), Won(10m, day), Lost(day), Lost(day),
            Open(1m, 10, day), Open(1m, 10, day), Open(1m, 10, day), Open(1m, 10, day),
        };

        var metrics = SalesMetrics.Compute(facts, November, "TRY", RateBook.Empty, Today);

        Assert.Equal(60m, metrics.WinRatePercent);
        Assert.Equal(3, metrics.WonCount);
        Assert.Equal(2, metrics.LostCount);
    }

    [Fact]
    public void Win_rate_is_undefined_without_decided_opportunities()
    {
        var metrics = SalesMetrics.Compute([Open(1m, 10, Today)], November, "TRY", RateBook.Empty, Today);

        Assert.Null(metrics.WinRatePercent);
    }

    [Fact]
    public void Missing_probability_is_reported_not_counted_as_zero()
    {
        var facts = new[] { Open(100m, 50, Today), Open(900m, null, Today) };

        var metrics = SalesMetrics.Compute(facts, November, "TRY", RateBook.Empty, Today);

        Assert.Equal(50m, metrics.WeightedOpen.Amount);
        Assert.Equal(1, metrics.WeightedMissingProbabilityCount);
        Assert.Equal(1_000m, metrics.ExpectedOpen.Amount);
    }

    [Fact]
    public void Missing_amount_and_close_date_are_reported()
    {
        var facts = new[] { Open(null, 50, Today), Open(100m, 50, null), Open(100m, 50, new DateOnly(2026, 10, 1)) };

        var metrics = SalesMetrics.Compute(facts, November, "TRY", RateBook.Empty, Today);

        Assert.Equal(1, metrics.ExpectedOpen.MissingAmountCount);
        Assert.Equal(1, metrics.OpenWithoutCloseDateCount);
        Assert.Equal(1, metrics.OverdueOpenCount);
        Assert.True(metrics.IsPartial);
    }

    [Fact]
    public void Realized_uses_won_amount_when_present()
    {
        var metrics = SalesMetrics.Compute([Won(100m, Today, wonAmount: 80m)], November, "TRY", RateBook.Empty, Today);

        Assert.Equal(80m, metrics.Realized.Amount);
    }

    [Fact]
    public void Foreign_currency_converts_with_rate_valid_on_the_right_day_and_reports_missing_rate()
    {
        var rates = new RateBook(
        [
            new RateEntry("USD", "TRY", 30m, new DateOnly(2026, 10, 1)),
            new RateEntry("USD", "TRY", 35m, new DateOnly(2026, 11, 1)),
            new RateEntry("USD", "TRY", 99m, new DateOnly(2026, 12, 1)),
        ]);
        var facts = new[]
        {
            Won(100m, new DateOnly(2026, 11, 3), "USD"),
            Open(10m, 100, new DateOnly(2026, 11, 20), "USD"),
            Open(10m, 100, new DateOnly(2026, 11, 21), "EUR"),
        };

        var metrics = SalesMetrics.Compute(facts, November, "TRY", rates, Today);

        // Gerçekleşen kazanım gününün (3 Kasım) kuruyla, açık fırsat bugünün kuruyla; gelecekteki kur (1 Aralık) girmez.
        Assert.Equal(3_500m, metrics.Realized.Amount);
        Assert.Equal(350m, metrics.ExpectedOpen.Amount);
        Assert.Equal(1, metrics.ExpectedOpen.MissingRateCount);
        Assert.True(metrics.IsPartial);
    }

    [Fact]
    public void Rate_book_uses_inverse_pair_and_never_assumes_one()
    {
        var book = new RateBook([new RateEntry("TRY", "USD", 0.025m, new DateOnly(2026, 11, 1))]);

        Assert.Equal(40m, book.Find("USD", "TRY", Today)!.Rate);
        Assert.True(book.Find("USD", "TRY", Today)!.Inverse);
        Assert.Null(book.Find("EUR", "TRY", Today));
        Assert.Equal(1m, book.Find("TRY", "TRY", Today)!.Rate);
        Assert.Null(book.Find("USD", "TRY", new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void Forecast_variance_has_no_percent_when_actual_is_zero()
    {
        var variance = SalesMetrics.Variance(100m, 0m);
        Assert.Equal(100m, variance.Error);
        Assert.Null(variance.ErrorPercent);

        var over = SalesMetrics.Variance(120m, 100m);
        Assert.Equal(20m, over.ErrorPercent);
        Assert.Equal(20m, over.AbsoluteError);
        Assert.Equal(20m, SalesMetrics.Variance(80m, 100m).AbsoluteError);
        Assert.Equal(-20m, SalesMetrics.Variance(80m, 100m).ErrorPercent);
    }

    // ---- Dönemler

    [Fact]
    public void Month_period_includes_start_and_excludes_next_start()
    {
        var period = ReportPeriods.Containing(PeriodKind.Month, new DateOnly(2026, 12, 31));

        Assert.Equal(new DateOnly(2026, 12, 1), period.Start);
        Assert.Equal(new DateOnly(2027, 1, 1), period.EndExclusive);
        Assert.True(period.Contains(new DateOnly(2026, 12, 31)));
        Assert.False(period.Contains(new DateOnly(2027, 1, 1)));
        Assert.Equal("2026-12", period.Label);
    }

    [Fact]
    public void Leap_day_belongs_to_february_and_year_boundary_is_exclusive()
    {
        var february = ReportPeriods.Containing(PeriodKind.Month, new DateOnly(2028, 2, 29));

        Assert.Equal(new DateOnly(2028, 3, 1), february.EndExclusive);
        Assert.True(february.Contains(new DateOnly(2028, 2, 29)));
    }

    [Theory]
    [InlineData(1, "2026-02-10", "2026-01-01", "2026-04-01", "FY2026-Q1")]
    [InlineData(1, "2026-11-02", "2026-10-01", "2027-01-01", "FY2026-Q4")]
    [InlineData(7, "2026-11-02", "2026-10-01", "2027-01-01", "FY2026/2027-Q2")]
    [InlineData(7, "2026-03-15", "2026-01-01", "2026-04-01", "FY2025/2026-Q3")]
    public void Quarter_follows_the_fiscal_calendar(int startMonth, string day, string start, string end, string label)
    {
        var period = ReportPeriods.Containing(PeriodKind.Quarter, DateOnly.Parse(day), startMonth);

        Assert.Equal(DateOnly.Parse(start), period.Start);
        Assert.Equal(DateOnly.Parse(end), period.EndExclusive);
        Assert.Equal(label, period.Label);
    }

    [Fact]
    public void Fiscal_year_spans_calendar_years_when_it_starts_mid_year()
    {
        var period = ReportPeriods.Containing(PeriodKind.Year, new DateOnly(2026, 3, 1), 7);

        Assert.Equal(new DateOnly(2025, 7, 1), period.Start);
        Assert.Equal(new DateOnly(2026, 7, 1), period.EndExclusive);
        Assert.Equal("FY2025/2026", period.Label);
    }

    [Fact]
    public void Shift_moves_by_whole_periods()
    {
        var quarter = ReportPeriods.Containing(PeriodKind.Quarter, new DateOnly(2026, 11, 2));

        Assert.Equal(new DateOnly(2027, 1, 1), ReportPeriods.Shift(quarter, 1).Start);
        Assert.Equal(new DateOnly(2026, 7, 1), ReportPeriods.Shift(quarter, -1).Start);
    }

    [Fact]
    public void Today_and_utc_range_follow_the_tenant_time_zone()
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var lateUtc = new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero);

        // UTC'de 31 Ekim 22:30, İstanbul'da (UTC+3) 1 Kasım 01:30: bu ayın günü Kasım'dır.
        Assert.Equal(new DateOnly(2026, 11, 1), ReportPeriods.Today(lateUtc, istanbul));
        var range = ReportPeriods.ToUtcRange(November, istanbul);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 21, 0, 0, TimeSpan.Zero), range.To);
    }

    [Fact]
    public void Utc_range_handles_daylight_saving_shift()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var march = ReportPeriods.Containing(PeriodKind.Month, new DateOnly(2026, 3, 10));

        var range = ReportPeriods.ToUtcRange(march, berlin);

        // 1 Mart UTC+1, 1 Nisan UTC+2 (yaz saati 29 Mart'ta başlar).
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 23, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 22, 0, 0, TimeSpan.Zero), range.To);
    }

    // ---- Hedef ve tahmin kuralları

    [Fact]
    public void Target_resolves_from_contiguous_slices_and_rejects_gaps()
    {
        var quarter = ReportPeriods.Containing(PeriodKind.Quarter, new DateOnly(2026, 11, 2));
        var full = new[]
        {
            new TargetSlice(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), 100m),
            new TargetSlice(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1), 200m),
            new TargetSlice(new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1), 300m),
        };

        Assert.Equal(600m, TargetRules.Resolve(full, quarter).Value);
        Assert.NotNull(TargetRules.Resolve(full.Take(2), quarter).Problem);
        Assert.Null(TargetRules.Resolve(full.Take(2), quarter).Value);
        Assert.Null(TargetRules.Resolve([], quarter).Problem);
        Assert.Equal(200m, TargetRules.Resolve(full, November).Value);
    }

    [Fact]
    public void Target_overlap_and_child_excess()
    {
        Assert.True(TargetRules.Overlaps(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 5, 1)));
        Assert.False(TargetRules.Overlaps(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1)));
        Assert.Equal(50m, TargetRules.ChildExcess(100m, 150m));
        Assert.Null(TargetRules.ChildExcess(100m, 100m));
    }

    [Theory]
    [InlineData(null, ForecastCategory.Pipeline)]
    [InlineData(0, ForecastCategory.Omitted)]
    [InlineData(49, ForecastCategory.Pipeline)]
    [InlineData(50, ForecastCategory.BestCase)]
    [InlineData(79, ForecastCategory.BestCase)]
    [InlineData(80, ForecastCategory.Commit)]
    [InlineData(100, ForecastCategory.Commit)]
    public void Default_category_follows_probability_thresholds(int? probability, string expected)
    {
        Assert.Equal(expected, ForecastRules.DefaultCategory(probability, ForecastPolicy.Default));
    }

    [Fact]
    public void Scenarios_do_not_stack_categories_and_weighted_is_a_separate_method()
    {
        var lines = new[]
        {
            new ForecastLine(ForecastCategory.Commit, 100m, 90),
            new ForecastLine(ForecastCategory.BestCase, 200m, 60),
            new ForecastLine(ForecastCategory.Pipeline, 400m, 20),
            new ForecastLine(ForecastCategory.Omitted, 1_000m, 90),
            new ForecastLine(ForecastCategory.Pipeline, null, 50),
        };

        var scenarios = ForecastRules.Scenarios(50m, lines);

        Assert.Equal(150m, scenarios.CommitScenario);
        Assert.Equal(350m, scenarios.OptimisticScenario);
        Assert.Equal(100m, scenarios.Commit);
        Assert.Equal(200m, scenarios.BestCase);
        Assert.Equal(400m, scenarios.Pipeline);
        // Ağırlıklı: 100*0,9 + 200*0,6 + 400*0,2 = 290 (hariç satır girmez).
        Assert.Equal(290m, scenarios.WeightedOpen);
        Assert.Equal(340m, scenarios.WeightedScenario);
    }

    [Fact]
    public void Adjustment_changes_final_amount_but_not_the_base()
    {
        Assert.Equal(120m, ForecastRules.FinalAmount(100m, 20m));
        Assert.Equal(100m, ForecastRules.FinalAmount(100m, null));
        Assert.Null(ForecastRules.FinalAmount(null, 20m));
        Assert.Equal(70, ForecastRules.EffectiveProbability(40, 70));
        Assert.Equal(40, ForecastRules.EffectiveProbability(40, null));
    }

    [Theory]
    [InlineData(ForecastStatus.Draft, ForecastStatus.Submitted, true)]
    [InlineData(ForecastStatus.Submitted, ForecastStatus.Locked, true)]
    [InlineData(ForecastStatus.Submitted, ForecastStatus.Draft, true)]
    [InlineData(ForecastStatus.Draft, ForecastStatus.Locked, false)]
    [InlineData(ForecastStatus.Locked, ForecastStatus.Draft, false)]
    [InlineData(ForecastStatus.Locked, ForecastStatus.Submitted, false)]
    public void Forecast_status_transitions(string from, string to, bool allowed)
    {
        Assert.Equal(allowed, ForecastRules.CanTransition(from, to));
    }

    [Fact]
    public void Reason_is_required_and_bounded()
    {
        Assert.NotNull(ForecastRules.ValidateReason(null));
        Assert.NotNull(ForecastRules.ValidateReason("   "));
        Assert.NotNull(ForecastRules.ValidateReason(new string('x', ForecastRules.ReasonMaxLength + 1)));
        Assert.Null(ForecastRules.ValidateReason("Müşteri bütçeyi onayladı."));
    }
}
