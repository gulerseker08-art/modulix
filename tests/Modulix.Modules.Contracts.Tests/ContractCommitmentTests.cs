using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 7: tekrar kuralından son tarihler, ölçüm değerlendirmesi, ihlal/eskalasyon ve bağımlılık kararları (saf).</summary>
public class ContractCommitmentTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void One_time_commitment_has_exactly_one_occurrence_on_its_due_date()
    {
        var dues = CommitmentSchedule.DueDates(CommitmentKind.OneTime, D("2026-12-15"), null, null, D("2027-12-31"));

        Assert.Equal([(1, D("2026-12-15"))], dues);
    }

    [Fact]
    public void Monthly_periods_keep_the_anchor_day_and_clamp_to_month_end_without_drifting()
    {
        var dues = CommitmentSchedule.DueDates(CommitmentKind.Periodic, D("2026-01-31"), CommitmentRecurrence.Monthly, D("2026-05-31"), D("2030-01-01"));

        Assert.Equal([D("2026-01-31"), D("2026-02-28"), D("2026-03-31"), D("2026-04-30"), D("2026-05-31")], dues.Select(d => d.Due));
        Assert.Equal([1, 2, 3, 4, 5], dues.Select(d => d.Sequence));
    }

    [Theory]
    [InlineData("Weekly", "2026-11-02", "2026-11-23", 4)]
    [InlineData("Quarterly", "2026-11-30", "2027-11-30", 5)]
    [InlineData("Yearly", "2026-02-28", "2029-02-28", 4)]
    public void Recurrence_rules_generate_the_expected_number_of_periods_including_the_end_date(string recurrence, string first, string end, int expected)
    {
        Assert.Equal(expected, CommitmentSchedule.DueDates(CommitmentKind.Periodic, D(first), recurrence, D(end), D("2040-01-01")).Count);
    }

    [Fact]
    public void Leap_day_anchor_clamps_in_non_leap_years_and_returns_in_leap_years()
    {
        Assert.Equal(D("2027-02-28"), CommitmentSchedule.Step(D("2026-02-28"), 12, CommitmentRecurrence.Monthly));
        Assert.Equal(D("2028-02-29"), CommitmentSchedule.Step(D("2024-02-29"), 48, CommitmentRecurrence.Monthly));
        Assert.Equal(D("2025-02-28"), CommitmentSchedule.Step(D("2024-02-29"), 1, CommitmentRecurrence.Yearly));
    }

    [Fact]
    public void Open_ended_periodic_commitment_is_bounded_by_the_horizon_and_the_hard_cap()
    {
        var withinHorizon = CommitmentSchedule.DueDates(CommitmentKind.Periodic, D("2026-11-01"), CommitmentRecurrence.Monthly, null, D("2027-10-31"));
        Assert.Equal(12, withinHorizon.Count);
        Assert.Equal(D("2027-10-01"), withinHorizon[^1].Due);

        var weeklyForever = CommitmentSchedule.DueDates(CommitmentKind.Periodic, D("2026-11-01"), CommitmentRecurrence.Weekly, D("2099-01-01"), D("2027-01-01"));
        Assert.Equal(CommitmentSchedule.MaxOccurrences, weeklyForever.Count);
    }

    [Fact]
    public void Generation_is_deterministic_so_rerunning_never_changes_existing_due_dates()
    {
        var first = CommitmentSchedule.DueDates(CommitmentKind.Periodic, D("2026-11-30"), CommitmentRecurrence.Monthly, null, D("2027-03-01"));
        var later = CommitmentSchedule.DueDates(CommitmentKind.Periodic, D("2026-11-30"), CommitmentRecurrence.Monthly, null, D("2027-09-01"));

        Assert.Equal(first, later.Take(first.Count));
        Assert.True(later.Count > first.Count);
    }

    [Theory]
    [InlineData("AtLeast", 99.5, 0.0, 99.5, true)]
    [InlineData("AtLeast", 99.5, 0.0, 99.49, false)]
    [InlineData("AtLeast", 99.5, 0.2, 99.3, true)]   // tolerans: hedefin 0,2 altı hâlâ karşılanmış
    [InlineData("AtLeast", 99.5, 0.2, 99.29, false)]
    [InlineData("AtMost", 4, 0.5, 4.5, true)]
    [InlineData("AtMost", 4, 0.5, 4.51, false)]
    [InlineData("Exactly", 10, 1, 9, true)]
    [InlineData("Exactly", 10, 1, 11.01, false)]
    public void Target_is_met_according_to_direction_and_tolerance(string direction, double target, double tolerance, double actual, bool expected)
    {
        Assert.Equal(expected, CommitmentMeasure.Meets(direction, (decimal)target, (decimal)tolerance, (decimal)actual));
    }

    [Fact]
    public void Measurement_without_target_or_direction_is_not_evaluated()
    {
        Assert.Null(CommitmentMeasure.Meets(null, 10m, null, 5m));
        Assert.Null(CommitmentMeasure.Meets("AtLeast", null, null, 5m));
    }

    [Fact]
    public void Overdue_then_breach_only_after_the_grace_period_and_only_for_open_occurrences()
    {
        var due = D("2026-11-10");

        Assert.False(CommitmentTracker.IsOverdue(OccurrenceStatus.Open, null, due, D("2026-11-10")));
        Assert.True(CommitmentTracker.IsOverdue(OccurrenceStatus.Open, null, due, D("2026-11-11")));
        Assert.False(CommitmentTracker.IsOverdue(OccurrenceStatus.Fulfilled, D("2026-11-09"), due, D("2026-12-01")));

        Assert.False(CommitmentTracker.ShouldBreach(OccurrenceStatus.Open, due, graceDays: 3, today: D("2026-11-13")));
        Assert.True(CommitmentTracker.ShouldBreach(OccurrenceStatus.Open, due, graceDays: 3, today: D("2026-11-14")));
        Assert.True(CommitmentTracker.ShouldBreach(OccurrenceStatus.Open, due, graceDays: 0, today: D("2026-11-11")));
        Assert.False(CommitmentTracker.ShouldBreach(OccurrenceStatus.Waived, due, 0, D("2027-01-01")));
        Assert.False(CommitmentTracker.ShouldBreach(OccurrenceStatus.Breached, due, 0, D("2027-01-01"))); // zaten ihlal: ikinci kez ihlal olmaz
    }

    [Fact]
    public void Escalation_needs_a_breach_old_enough_open_and_not_escalated_before()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var breached = now.AddDays(-5);

        Assert.True(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Breached, null, breached, null, 5, now));
        Assert.False(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Breached, null, breached, null, 6, now));
        Assert.False(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Breached, null, breached, null, 0, now));
        Assert.False(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Breached, null, breached, now.AddDays(-1), 5, now));
        Assert.False(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Breached, D("2026-11-19"), breached, null, 5, now)); // geç de olsa tamamlandı
        Assert.False(CommitmentTracker.ShouldEscalate(OccurrenceStatus.Open, null, null, null, 5, now));
    }

    [Theory]
    [InlineData("Open", null, true)]
    [InlineData("Breached", null, true)]
    [InlineData("Breached", "2026-11-12", false)]   // geç tamamlanmış: üzerine yazılmaz
    [InlineData("Fulfilled", "2026-11-09", false)]
    [InlineData("Waived", null, false)]
    [InlineData("Cancelled", null, false)]
    public void Only_open_or_breached_uncompleted_occurrences_can_be_recorded(string status, string? completedOn, bool expected)
    {
        Assert.Equal(expected, CommitmentTracker.IsCompletable(status, completedOn is null ? null : D(completedOn)));
    }

    [Fact]
    public void Dependency_blocks_until_earlier_or_same_dated_occurrences_of_the_other_commitment_are_done()
    {
        var dependency = new (string Status, DateOnly? CompletedOn, DateOnly Due)[]
        {
            (OccurrenceStatus.Fulfilled, D("2026-11-01"), D("2026-11-01")),
            (OccurrenceStatus.Open, null, D("2026-12-01")),
            (OccurrenceStatus.Open, null, D("2027-01-01")),
        };

        Assert.Null(CommitmentTracker.DependencyBlock(D("2026-11-15"), dependency));
        Assert.Equal(D("2026-12-01"), CommitmentTracker.DependencyBlock(D("2026-12-01"), dependency));
        Assert.Equal(D("2026-12-01"), CommitmentTracker.DependencyBlock(D("2027-02-01"), dependency));
        Assert.Null(CommitmentTracker.DependencyBlock(D("2027-02-01"), dependency.Select(o => o.Status == OccurrenceStatus.Open ? (OccurrenceStatus.Waived, o.CompletedOn, o.Due) : o)));
    }
}
