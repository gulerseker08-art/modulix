using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Aktivite durum makinesi, gecikme türetmesi, zaman bloğu doğrulaması ve takvim penceresi (veritabanı gerektirmez).</summary>
public class ActivityRulesTests
{
    [Theory]
    [InlineData(ActivityFamily.Task, ActivityStatus.Open, ActivityStatus.InProgress, true)]
    [InlineData(ActivityFamily.Task, ActivityStatus.Open, ActivityStatus.Blocked, true)]
    [InlineData(ActivityFamily.Task, ActivityStatus.Blocked, ActivityStatus.Completed, false)]
    [InlineData(ActivityFamily.Task, ActivityStatus.Completed, ActivityStatus.Open, true)]
    [InlineData(ActivityFamily.Task, ActivityStatus.Completed, ActivityStatus.InProgress, false)]
    [InlineData(ActivityFamily.Call, ActivityStatus.Open, ActivityStatus.NoAnswer, true)]
    [InlineData(ActivityFamily.Call, ActivityStatus.Open, ActivityStatus.Blocked, false)]
    [InlineData(ActivityFamily.Call, ActivityStatus.NoAnswer, ActivityStatus.Completed, true)]
    [InlineData(ActivityFamily.Meeting, ActivityStatus.Open, ActivityStatus.NoShow, true)]
    [InlineData(ActivityFamily.Visit, ActivityStatus.Open, ActivityStatus.NoShow, true)]
    [InlineData(ActivityFamily.Meeting, ActivityStatus.Open, ActivityStatus.NoAnswer, false)]
    [InlineData(ActivityFamily.Email, ActivityStatus.Draft, ActivityStatus.Sent, true)]
    [InlineData(ActivityFamily.Email, ActivityStatus.Draft, ActivityStatus.Completed, false)]
    [InlineData(ActivityFamily.Email, ActivityStatus.Queued, ActivityStatus.Failed, true)]
    [InlineData(ActivityFamily.Email, ActivityStatus.Failed, ActivityStatus.Queued, true)]
    [InlineData(ActivityFamily.Email, ActivityStatus.Sent, ActivityStatus.Draft, false)]
    public void Transitions_follow_each_familys_own_machine(string family, string from, string to, bool allowed) =>
        Assert.Equal(allowed, ActivityRules.CanMove(family, from, to) is null);

    [Fact]
    public void Same_status_is_not_a_transition() =>
        Assert.NotNull(ActivityRules.CanMove(ActivityFamily.Task, ActivityStatus.Open, ActivityStatus.Open));

    [Fact]
    public void Unknown_or_foreign_status_is_treated_as_the_familys_initial_state()
    {
        // Eski kayıt ya da tür değişimi: arama için "Draft" geçersizdir, Open gibi davranır.
        Assert.Equal(ActivityStatus.Open, ActivityRules.Normalize(ActivityFamily.Call, ActivityStatus.Draft));
        Assert.Equal(ActivityStatus.Draft, ActivityRules.Normalize(ActivityFamily.Email, ActivityStatus.Open));
        Assert.Equal(ActivityStatus.Open, ActivityRules.Normalize(ActivityFamily.Task, null));
    }

    [Theory]
    [InlineData(ActivityFamily.Task, null, ActivityStatus.Completed)]
    [InlineData(ActivityFamily.Call, "Reached", ActivityStatus.Completed)]
    [InlineData(ActivityFamily.Call, "NoAnswer", ActivityStatus.NoAnswer)]
    [InlineData(ActivityFamily.Meeting, "NoAnswer", ActivityStatus.NoShow)]
    [InlineData(ActivityFamily.Visit, "NoAnswer", ActivityStatus.NoShow)]
    [InlineData(ActivityFamily.Visit, "Reached", ActivityStatus.Completed)]
    [InlineData(ActivityFamily.Email, "Sent", ActivityStatus.Sent)]
    public void Completion_target_depends_on_family_and_outcome(string family, string? outcome, string expected) =>
        Assert.Equal(expected, ActivityRules.CompletionTarget(family, outcome));

    [Theory]
    [InlineData(ActivityStatus.Completed, true)]
    [InlineData(ActivityStatus.NoAnswer, true)]
    [InlineData(ActivityStatus.NoShow, true)]
    [InlineData(ActivityStatus.Sent, true)]
    [InlineData(ActivityStatus.Cancelled, false)]
    [InlineData(ActivityStatus.Open, false)]
    public void Only_realised_states_record_a_completion_time(string status, bool records) =>
        Assert.Equal(records, ActivityRules.RecordsCompletion(status));

    [Theory]
    [InlineData(ActivityStatus.Open, true)]
    [InlineData(ActivityStatus.InProgress, true)]
    [InlineData(ActivityStatus.Blocked, true)]
    [InlineData(ActivityStatus.Failed, true)]
    [InlineData(ActivityStatus.Completed, false)]
    [InlineData(ActivityStatus.Cancelled, false)]
    [InlineData(ActivityStatus.NoShow, false)]
    [InlineData(ActivityStatus.Sent, false)]
    public void Overdue_is_derived_and_never_applies_to_closed_records(string status, bool canBeOverdue)
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var past = now.AddDays(-1);
        Assert.Equal(canBeOverdue, ActivityRules.IsOverdue(status, past, now));
        Assert.False(ActivityRules.IsOverdue(status, now.AddDays(1), now));
        Assert.False(ActivityRules.IsOverdue(status, null, now));
    }

    [Fact]
    public void Every_family_state_machine_only_targets_its_own_statuses()
    {
        foreach (var family in ActivityFamily.All)
        {
            foreach (var from in ActivityRules.StatusesOf(family))
            {
                foreach (var to in ActivityStatus.All.Where(s => ActivityRules.CanMove(family, from, s) is null))
                {
                    Assert.Contains(to, ActivityRules.StatusesOf(family));
                }
            }
        }
    }

    [Fact]
    public void Schedule_rules_separate_all_day_dates_from_timed_blocks()
    {
        var start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var day = new DateOnly(2026, 11, 2);

        Assert.Null(ActivityRules.ValidateSchedule(false, start, start.AddHours(1), null, null));
        Assert.Null(ActivityRules.ValidateSchedule(false, null, null, null, null));
        Assert.Null(ActivityRules.ValidateSchedule(true, null, null, day, day.AddDays(2)));
        Assert.NotNull(ActivityRules.ValidateSchedule(false, start, start, null, null));
        Assert.NotNull(ActivityRules.ValidateSchedule(false, null, start, null, null));
        Assert.NotNull(ActivityRules.ValidateSchedule(true, start, null, day, null));
        Assert.NotNull(ActivityRules.ValidateSchedule(true, null, null, null, null));
        Assert.NotNull(ActivityRules.ValidateSchedule(true, null, null, day, day.AddDays(-1)));
        Assert.NotNull(ActivityRules.ValidateSchedule(false, start, start.AddHours(1), day, null));
    }

    [Fact]
    public void Time_blocks_are_half_open_so_back_to_back_meetings_do_not_conflict()
    {
        var nine = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.False(ActivityRules.Overlaps(nine, nine.AddHours(1), nine.AddHours(1), nine.AddHours(2)));
        Assert.True(ActivityRules.Overlaps(nine, nine.AddHours(1), nine.AddMinutes(30), nine.AddHours(2)));
    }

    [Fact]
    public void All_day_span_counts_both_ends()
    {
        var day = new DateOnly(2026, 2, 27);
        Assert.Equal(1, ActivityRules.DaySpan(day, null));
        Assert.Equal(3, ActivityRules.DaySpan(day, day.AddDays(2)));
        // Artık yıl olmayan yılda şubat 28 gün: 27 Şubat–2 Mart 4 gündür.
        Assert.Equal(4, ActivityRules.DaySpan(day, new DateOnly(2026, 3, 2)));
    }

    [Fact]
    public void Calendar_range_is_bounded_and_validated()
    {
        const string valid = "2026-11-01T00:00:00Z|2026-12-01T00:00:00Z|2026-11-01|2026-11-30";
        Assert.NotNull(CalendarRange.Parse(valid));
        Assert.Null(CalendarRange.Parse("2026-11-01T00:00:00Z|2026-11-01T00:00:00Z|2026-11-01|2026-11-01"));
        Assert.Null(CalendarRange.Parse("2026-01-01T00:00:00Z|2027-01-01T00:00:00Z|2026-01-01|2026-12-31"));
        Assert.Null(CalendarRange.Parse("bozuk"));
        Assert.Null(CalendarRange.Parse("2026-11-01T00:00:00Z|2026-12-01T00:00:00Z|2026-11-30|2026-11-01"));
    }
}
