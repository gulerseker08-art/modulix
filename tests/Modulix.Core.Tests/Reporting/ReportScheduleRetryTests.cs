using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Reporting;

namespace Modulix.Core.Tests.Reporting;

public sealed class ReportScheduleRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextPeriod = new(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);

    private static ReportSchedule NewSchedule() =>
        new(Guid.NewGuid(), Guid.NewGuid(), ScheduleFrequency.Weekly, 9, DayOfWeek.Tuesday, null, Now, Now);

    [Fact]
    public void A_failed_run_is_retried_after_fifteen_then_sixty_minutes_and_then_waits_for_the_next_period()
    {
        var schedule = NewSchedule();

        schedule.RecordFailure("hata", NextPeriod, Now);
        Assert.Equal((1, "Failed", Now.AddMinutes(15)), (schedule.Attempts, schedule.LastStatus, schedule.NextRunAt));

        var second = Now.AddMinutes(15);
        schedule.RecordFailure("hata", NextPeriod, second);
        Assert.Equal((2, second.AddMinutes(60)), (schedule.Attempts, schedule.NextRunAt));

        var third = second.AddMinutes(60);
        schedule.RecordFailure("hata", NextPeriod, third);
        Assert.Equal((0, NextPeriod, "Failed"), (schedule.Attempts, schedule.NextRunAt, schedule.LastStatus));
        Assert.True(schedule.Enabled);   // başarısızlık zamanlamayı kapatmaz
    }

    [Fact]
    public void A_delivered_run_resets_the_attempt_counter()
    {
        var schedule = NewSchedule();
        schedule.RecordFailure("hata", NextPeriod, Now);

        schedule.RecordRun("Delivered", null, NextPeriod, Now.AddMinutes(15));

        Assert.Equal((0, "Delivered", NextPeriod), (schedule.Attempts, schedule.LastStatus, schedule.NextRunAt));
    }

    [Fact]
    public void Retry_delay_never_exceeds_an_hour_and_the_limit_is_three_attempts()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), ReportSchedule.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(60), ReportSchedule.RetryDelay(2));
        Assert.Equal(3, ReportSchedule.MaxAttempts);
    }
}
