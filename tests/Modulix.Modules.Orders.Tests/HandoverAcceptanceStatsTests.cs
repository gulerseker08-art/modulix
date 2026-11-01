using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023 dilim 6: ortalama devir kabul süresi hesabı (saf).</summary>
public class HandoverAcceptanceStatsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Average_is_the_mean_of_first_send_to_acceptance_in_hours()
    {
        var average = HandoverAcceptanceStats.AverageHours([(Start, Start.AddHours(2)), (Start, Start.AddHours(5)), (Start, Start.AddHours(8))]);

        Assert.Equal(5d, average);
    }

    [Fact]
    public void Average_is_rounded_to_one_decimal()
    {
        Assert.Equal(1.3d, HandoverAcceptanceStats.AverageHours([(Start, Start.AddMinutes(80))]));
    }

    [Fact]
    public void Negative_durations_from_clock_skew_are_ignored_and_no_sample_gives_null()
    {
        Assert.Equal(4d, HandoverAcceptanceStats.AverageHours([(Start, Start.AddHours(4)), (Start, Start.AddHours(-1))]));
        Assert.Null(HandoverAcceptanceStats.AverageHours([]));
        Assert.Null(HandoverAcceptanceStats.AverageHours([(Start, Start.AddHours(-3))]));
    }
}
