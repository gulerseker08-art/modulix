using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Modules;

/// <summary>ADR-0028: bitiş tarihinden sonra ek süre (yazma açık), ardından salt okunur. Sınır anları önceki evrededir.</summary>
public class EntitlementPhasesTests
{
    private static readonly DateTimeOffset End = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Right_without_an_end_date_is_always_active()
    {
        Assert.Equal(EntitlementPhase.Active, EntitlementPhases.At(null, End.AddYears(10), 14));
    }

    [Theory]
    [InlineData(-30, EntitlementPhase.Active)]
    [InlineData(0, EntitlementPhase.Active)] // bitiş anının kendisi hâlâ süre içindedir
    [InlineData(1, EntitlementPhase.Grace)]
    [InlineData(14 * 24 * 60, EntitlementPhase.Grace)] // ek sürenin son anı (dakika)
    [InlineData((14 * 24 * 60) + 1, EntitlementPhase.ReadOnly)]
    [InlineData(100 * 24 * 60, EntitlementPhase.ReadOnly)]
    public void Phase_follows_the_end_date_and_the_grace_period(int minutesAfterEnd, EntitlementPhase expected)
    {
        var now = End.AddMinutes(minutesAfterEnd);

        Assert.Equal(expected, EntitlementPhases.At(End, now, 14));
    }

    [Fact]
    public void Zero_grace_days_goes_read_only_right_after_the_end()
    {
        Assert.Equal(EntitlementPhase.Active, EntitlementPhases.At(End, End, 0));
        Assert.Equal(EntitlementPhase.ReadOnly, EntitlementPhases.At(End, End.AddSeconds(1), 0));
    }

    [Fact]
    public void Describe_reports_when_the_module_turns_read_only()
    {
        var info = EntitlementPhases.Describe("crm", End, End.AddDays(3), 14);

        Assert.Equal("crm", info.ModuleKey);
        Assert.Equal(EntitlementPhase.Grace, info.Phase);
        Assert.Equal(End, info.ValidUntil);
        Assert.Equal(End.AddDays(14), info.GraceEndsAt);

        var open = EntitlementPhases.Describe("crm", null, End, 14);
        Assert.Equal(EntitlementPhase.Active, open.Phase);
        Assert.Null(open.GraceEndsAt);
    }
}
