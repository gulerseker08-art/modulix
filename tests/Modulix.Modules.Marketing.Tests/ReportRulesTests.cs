using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Reports;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 8: "gönderildi" tanımı, birim maliyet ve ilk dokunuş seçimi (saf).</summary>
public class ReportRulesTests
{
    [Theory]
    [InlineData(DeliveryStatuses.Accepted, true)]
    [InlineData(DeliveryStatuses.Unknown, true)]
    [InlineData(DeliveryStatuses.Queued, false)]
    [InlineData(DeliveryStatuses.Sending, false)]
    [InlineData(DeliveryStatuses.Failed, false)]
    [InlineData(DeliveryStatuses.Skipped, false)]
    [InlineData(DeliveryStatuses.Cancelled, false)]
    public void Only_accepted_and_unknown_messages_count_as_sent(string status, bool sent) => Assert.Equal(sent, ReportRules.IsSent(status));

    [Theory]
    [InlineData(300, 2, 150)]
    [InlineData(100, 3, 33.33)]
    [InlineData(0, 5, 0)]
    public void Unit_cost_is_rounded_to_two_decimals(double cost, int count, double expected) => Assert.Equal((decimal)expected, ReportRules.CostPer((decimal)cost, count));

    [Fact]
    public void Unit_cost_is_unknown_without_a_cost_or_a_count()
    {
        Assert.Null(ReportRules.CostPer(null, 4));
        Assert.Null(ReportRules.CostPer(100m, 0));
        Assert.Null(ReportRules.CostPer(null, 0));
    }

    [Fact]
    public void First_touch_is_the_earliest_message_per_target_regardless_of_input_order()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        var touches = new[]
        {
            new TouchPoint(t1, b, day.AddDays(2)),
            new TouchPoint(t1, a, day),
            new TouchPoint(t2, b, day.AddDays(1)),
            new TouchPoint(t2, a, day.AddDays(3)),
        };

        var first = ReportRules.FirstTouch(touches);
        Assert.Equal((a, b), (first[t1], first[t2]));
        Assert.Equal(first, ReportRules.FirstTouch(touches.Reverse()));
    }

    [Fact]
    public void A_tie_resolves_to_the_smaller_campaign_id_so_the_result_is_stable()
    {
        var target = Guid.NewGuid();
        var small = new Guid("00000000-0000-0000-0000-000000000001");
        var large = new Guid("00000000-0000-0000-0000-000000000002");
        var at = DateTimeOffset.UtcNow;

        Assert.Equal(small, ReportRules.FirstTouch([new TouchPoint(target, large, at), new TouchPoint(target, small, at)])[target]);
        Assert.Empty(ReportRules.FirstTouch([]));
    }
}
