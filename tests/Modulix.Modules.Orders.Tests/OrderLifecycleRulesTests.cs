using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023 dilim 2: geçiş tablosu, iptal/azaltma hesabı ve değişiklik talebi kuralları (saf kurallar).</summary>
public class OrderTransitionsTests
{
    private static OrderState State(string status, string fulfillment = FulfillmentStatus.NotStarted, int lines = 1) =>
        new(status, HandoverStatus.NotStarted, fulfillment, lines);

    [Theory]
    [InlineData(OrderAction.SubmitForApproval, OrderStatus.Draft, true)]
    [InlineData(OrderAction.SubmitForApproval, OrderStatus.PendingApproval, false)]
    [InlineData(OrderAction.SubmitForApproval, OrderStatus.Approved, false)]
    [InlineData(OrderAction.WithdrawApproval, OrderStatus.PendingApproval, true)]
    [InlineData(OrderAction.WithdrawApproval, OrderStatus.Draft, false)]
    [InlineData(OrderAction.Approve, OrderStatus.PendingApproval, true)]
    [InlineData(OrderAction.Approve, OrderStatus.Draft, false)]
    [InlineData(OrderAction.ReturnToDraft, OrderStatus.PendingApproval, true)]
    [InlineData(OrderAction.ReturnToDraft, OrderStatus.Approved, false)]
    [InlineData(OrderAction.Cancel, OrderStatus.Draft, true)]
    [InlineData(OrderAction.Cancel, OrderStatus.PendingApproval, true)]
    [InlineData(OrderAction.Cancel, OrderStatus.Approved, true)]
    [InlineData(OrderAction.Cancel, OrderStatus.Cancelled, false)]
    [InlineData(OrderAction.Cancel, OrderStatus.Closed, false)]
    public void Transition_table_allows_only_the_defined_moves(string action, string status, bool allowed)
    {
        var result = OrderTransitions.Can(action, State(status));

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(allowed, result.Code is null);
    }

    [Fact]
    public void Submitting_requires_at_least_one_active_line()
    {
        var none = OrderTransitions.Can(OrderAction.SubmitForApproval, State(OrderStatus.Draft, lines: 0));

        Assert.False(none.Allowed);
        Assert.Equal("no-lines", none.Code);
    }

    [Fact]
    public void Normal_close_needs_completed_fulfillment_and_force_close_is_only_for_remaining_obligations()
    {
        Assert.Equal("obligation-remaining", OrderTransitions.Can(OrderAction.Close, State(OrderStatus.Approved, FulfillmentStatus.Partial)).Code);
        Assert.True(OrderTransitions.Can(OrderAction.Close, State(OrderStatus.Approved, FulfillmentStatus.Completed)).Allowed);
        Assert.True(OrderTransitions.Can(OrderAction.ForceClose, State(OrderStatus.Approved, FulfillmentStatus.NotStarted)).Allowed);
        Assert.Equal("nothing-remaining", OrderTransitions.Can(OrderAction.ForceClose, State(OrderStatus.Approved, FulfillmentStatus.Completed)).Code);
        Assert.Equal("not-approved", OrderTransitions.Can(OrderAction.Close, State(OrderStatus.Draft)).Code);
        Assert.Equal("unknown", OrderTransitions.Can("explode", State(OrderStatus.Draft)).Code);
    }

    [Fact]
    public void Reasons_are_required_for_withdrawal_return_cancel_and_force_close_and_every_action_has_a_target()
    {
        Assert.All([OrderAction.WithdrawApproval, OrderAction.ReturnToDraft, OrderAction.Cancel, OrderAction.ForceClose], a => Assert.True(OrderTransitions.RequiresReason(a)));
        Assert.All([OrderAction.SubmitForApproval, OrderAction.Approve, OrderAction.Close], a => Assert.False(OrderTransitions.RequiresReason(a)));
        Assert.All(OrderAction.All, a => Assert.Contains(OrderTransitions.TargetStatus(a), OrderStatus.All));
    }
}

public class OrderAmountsTests
{
    private static LineAmounts Line(decimal quantity, decimal delivered, decimal cancelled, decimal net, decimal tax) =>
        new(Guid.NewGuid(), "Kalem", quantity, delivered, cancelled, net, tax, false);

    [Fact]
    public void Cancelling_an_undelivered_line_cancels_all_of_it_and_releases_its_amounts()
    {
        var line = Line(4m, 0m, 0m, 4000m, 800m);

        var plan = Assert.Single(OrderAmounts.PlanCancellation([line]));

        Assert.Equal((4m, 4m, 4000m, 800m), (plan.NewCancelled, plan.ReleasedQuantity, plan.ReleasedNet, plan.ReleasedTax));
        Assert.True(plan.IsFullyCancelled(line.Quantity));
    }

    [Fact]
    public void Cancelling_a_partly_delivered_line_keeps_the_delivered_quantity_and_its_amounts()
    {
        var line = Line(4m, 1m, 0m, 4000m, 800m);

        var plan = Assert.Single(OrderAmounts.PlanCancellation([line]));

        // Teslim edilen 1 adet korunur; yalnızca 3 adet ve orantılı tutar serbest kalır.
        Assert.Equal((3m, 3m, 3000m, 600m), (plan.NewCancelled, plan.ReleasedQuantity, plan.ReleasedNet, plan.ReleasedTax));
        Assert.False(plan.IsFullyCancelled(line.Quantity));
    }

    [Fact]
    public void Cancelling_twice_or_a_fully_delivered_line_releases_nothing_more()
    {
        Assert.Empty(OrderAmounts.PlanCancellation([Line(4m, 4m, 0m, 4000m, 800m)]));
        Assert.Empty(OrderAmounts.PlanCancellation([Line(4m, 0m, 4m, 4000m, 800m)]));
    }

    [Fact]
    public void Effective_amounts_follow_the_active_quantity_without_changing_the_snapshot()
    {
        var line = Line(3m, 0m, 1m, 100m, 20m);

        Assert.Equal((2m, 66.67m, 13.33m), (line.Effective, line.EffectiveNet, line.EffectiveTax));
        Assert.Equal(100m, line.Net);
        Assert.Equal(0m, Line(3m, 0m, 3m, 100m, 20m).EffectiveNet);
    }

    [Fact]
    public void Reducing_releases_the_difference_and_totals_sum_the_effective_amounts()
    {
        var a = Line(4m, 0m, 0m, 4000m, 800m);
        var b = Line(1m, 0m, 0m, 300m, 0m);

        var reduction = OrderAmounts.Reduce(a, 3m);
        var (net, tax) = OrderAmounts.Totals([a with { Cancelled = reduction.NewCancelled }, b]);

        Assert.Equal((1m, 1000m, 200m), (reduction.ReleasedQuantity, reduction.ReleasedNet, reduction.ReleasedTax));
        Assert.Equal((3300m, 600m), (net, tax));
    }
}

public class OrderChangeRulesTests
{
    private static readonly Guid LineA = Guid.NewGuid();
    private static readonly Guid LineB = Guid.NewGuid();

    private static IReadOnlyList<LineAmounts> Lines(decimal deliveredA = 0m) =>
    [
        new(LineA, "Cihaz", 4m, deliveredA, 0m, 4000m, 800m, false),
        new(LineB, "Montaj", 1m, 0m, 0m, 300m, 0m, true),
    ];

    private static OrderHeaderState State(string status = OrderStatus.Approved, string handover = HandoverStatus.NotStarted, string fulfillment = FulfillmentStatus.NotStarted, string? contract = null) =>
        new(status, handover, fulfillment, contract, new Dictionary<string, string?>
        {
            ["deliveryAddress"] = "Gebze", ["location"] = null, ["plannedStartDate"] = "2026-12-01", ["plannedDeliveryDate"] = "2026-12-15",
            ["paymentTerms"] = "30 gün", ["deliveryTerms"] = null, ["commercialTerms"] = null, ["commitments"] = null, ["operationsNotes"] = null,
        });

    private static OrderChangeSet Set(Dictionary<string, string?>? header = null, params OrderLineChange[] lines) => new(header ?? [], lines);

    private static readonly IReadOnlyDictionary<Guid, DateOnly?> NoPlanned = new Dictionary<Guid, DateOnly?>();

    [Fact]
    public void Unchanged_values_are_dropped_and_an_empty_result_is_rejected()
    {
        var set = Set(new() { ["deliveryAddress"] = " Gebze ", ["paymentTerms"] = "30 gün" }, new OrderLineChange(LineA, 4m));

        var effective = OrderChangeRules.Effective(set, State(), Lines(), NoPlanned);
        var errors = OrderChangeRules.Validate(effective, State(), Lines());

        Assert.True(effective.IsEmpty);
        Assert.Equal("changes", Assert.Single(errors).Field);
    }

    [Fact]
    public void Only_approved_orders_accept_change_requests()
    {
        var effective = Set(new() { ["location"] = "Kocaeli" });

        Assert.Equal("status", Assert.Single(OrderChangeRules.Validate(effective, State(OrderStatus.Draft), Lines())).Field);
        Assert.Equal("status", Assert.Single(OrderChangeRules.Validate(effective, State(OrderStatus.Closed), Lines())).Field);
        Assert.Empty(OrderChangeRules.Validate(effective, State(), Lines()));
    }

    [Fact]
    public void Quantity_cannot_increase_go_below_the_delivered_amount_or_become_negative()
    {
        Assert.Contains(OrderChangeRules.Validate(Set(null, new OrderLineChange(LineA, 5m)), State(), Lines()), e => e.Field == "lines" && e.Message.Contains("artırılamaz"));
        Assert.Contains(OrderChangeRules.Validate(Set(null, new OrderLineChange(LineA, 1m)), State(), Lines(deliveredA: 2m)), e => e.Message.Contains("teslim edildi"));
        Assert.Contains(OrderChangeRules.Validate(Set(null, new OrderLineChange(LineA, -1m)), State(), Lines()), e => e.Message.Contains("negatif"));
        Assert.Empty(OrderChangeRules.Validate(Set(null, new OrderLineChange(LineA, 2m)), State(), Lines(deliveredA: 2m)));
    }

    [Fact]
    public void Header_fields_and_dates_are_validated_and_the_commercial_copy_stays_closed()
    {
        Assert.Contains(OrderChangeRules.Validate(Set(new() { ["netTotal"] = "1" }), State(), Lines()), e => e.Field == "netTotal");
        Assert.Contains(OrderChangeRules.Validate(Set(new() { ["plannedDeliveryDate"] = "15/12/2026" }), State(), Lines()), e => e.Field == "plannedDeliveryDate");
        Assert.Contains(OrderChangeRules.Validate(Set(new() { ["plannedDeliveryDate"] = "2026-11-01" }), State(), Lines()), e => e.Message.Contains("önce olamaz"));
        Assert.Contains(OrderChangeRules.Validate(Set(null, new OrderLineChange(Guid.NewGuid(), 1m)), State(), Lines()), e => e.Message.Contains("bu siparişte yok"));
        Assert.Contains(OrderChangeRules.Validate(Set(null, new OrderLineChange(LineA, 0m), new OrderLineChange(LineB, 0m)), State(), Lines()), e => e.Message.Contains("iptal edin"));
    }

    [Fact]
    public void Impact_lists_operations_deliveries_contract_and_the_amount_difference()
    {
        var set = OrderChangeRules.Effective(
            Set(new() { ["plannedDeliveryDate"] = "2027-01-10", ["location"] = "Kocaeli" }, new OrderLineChange(LineA, 2m)), State(), Lines(deliveredA: 1m), NoPlanned);
        var state = State(handover: HandoverStatus.Accepted, fulfillment: FulfillmentStatus.Partial, contract: "SZ-1");

        var impact = OrderChangeRules.Impact(set, state, Lines(deliveredA: 1m));

        Assert.Contains(impact.Operations, o => o.Contains("operasyona gönderilmiş"));
        Assert.Contains(impact.Operations, o => o.Contains("silinmez"));
        Assert.Contains(impact.Deliveries, d => d.Contains("teslim edilen 1") && d.Contains("kalan açık miktar 1"));
        Assert.Contains(impact.Deliveries, d => d.Contains("2026-12-15") && d.Contains("2027-01-10"));
        Assert.Contains(impact.Contracts, c => c.Contains("SZ-1"));
        Assert.Equal((-2000m, -400m), (impact.NetDelta, impact.TaxDelta));
    }

    [Fact]
    public void Impact_is_empty_for_a_note_only_change_on_an_order_that_was_not_handed_over()
    {
        var set = Set(new() { ["operationsNotes"] = "Kapıdan giriş" });

        var impact = OrderChangeRules.Impact(set, State(contract: "SZ-1"), Lines());

        Assert.Empty(impact.Operations);
        Assert.Empty(impact.Deliveries);
        Assert.Empty(impact.Contracts);
        Assert.Equal(0m, impact.NetDelta);
    }
}
