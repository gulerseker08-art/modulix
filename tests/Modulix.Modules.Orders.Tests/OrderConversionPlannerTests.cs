using Modulix.Modules.Crm.Contracts.Sales;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023: tekliften sipariş kalemi hesabı (tam ve kısmi dönüşüm, kalan miktar, kuruş farkı).</summary>
public class OrderConversionPlannerTests
{
    private static AcceptedQuoteLine Line(string name, decimal quantity, decimal net, decimal tax, Guid? key = null) =>
        new(key ?? Guid.NewGuid(), 1, "KOD", name, null, quantity, "adet", quantity == 0 ? 0 : net / quantity, 20m, net, tax, net + tax, null);

    private static IReadOnlyDictionary<Guid, ConsumptionState> Empty { get; } = new Dictionary<Guid, ConsumptionState>();

    [Fact]
    public void Full_conversion_takes_every_line_with_its_whole_quantity_and_amounts()
    {
        var a = Line("Kurulum", 2m, 2000m, 400m);
        var b = Line("Destek", 1m, 500m, 100m);

        var plan = OrderConversionPlanner.Plan([a, b], ConversionMode.Full, null, Empty);

        Assert.True(plan.IsSuccess);
        Assert.Equal([2m, 1m], plan.Lines.Select(l => l.Quantity));
        Assert.Equal([2000m, 500m], plan.Lines.Select(l => l.Net));
        Assert.Equal([400m, 100m], plan.Lines.Select(l => l.Tax));
        Assert.Equal(3000m, plan.Lines.Sum(l => l.Gross));
    }

    [Fact]
    public void Full_conversion_is_a_conflict_when_any_line_was_already_converted()
    {
        var a = Line("Kurulum", 2m, 2000m, 400m);
        var ledger = new Dictionary<Guid, ConsumptionState> { [a.StableKey] = new(2m, 2000m, 400m, 1m, 1000m, 200m) };

        var plan = OrderConversionPlanner.Plan([a], ConversionMode.Full, null, ledger);

        Assert.Equal(PlanErrorKind.Conflict, plan.Error);
    }

    [Fact]
    public void Full_conversion_does_not_accept_quantities_and_unknown_mode_is_invalid()
    {
        var a = Line("Kurulum", 2m, 2000m, 400m);

        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Full, [new(a.StableKey, 1m)], Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], "Weird", null, Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([], ConversionMode.Full, null, Empty).Error);
    }

    [Fact]
    public void Partial_conversion_takes_the_requested_quantity_with_proportional_amounts()
    {
        var a = Line("Kurulum", 4m, 4000m, 800m);

        var plan = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m)], Empty);

        Assert.True(plan.IsSuccess);
        var planned = Assert.Single(plan.Lines);
        Assert.Equal((1m, 1000m, 200m, 1200m), (planned.Quantity, planned.Net, planned.Tax, planned.Gross));
    }

    [Fact]
    public void Partial_conversion_cannot_exceed_the_accepted_quantity_even_across_orders()
    {
        var a = Line("Kurulum", 4m, 4000m, 800m);
        var ledger = new Dictionary<Guid, ConsumptionState> { [a.StableKey] = new(4m, 4000m, 800m, 3m, 3000m, 600m) };

        var over = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 2m)], ledger);
        var exact = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m)], ledger);

        Assert.Equal(PlanErrorKind.Conflict, over.Error);
        Assert.True(exact.IsSuccess);
    }

    [Fact]
    public void Last_remaining_quantity_closes_the_line_without_a_cent_difference()
    {
        var a = Line("Hizmet", 3m, 100m, 20m);

        // 100 / 3 → 33,33 + 33,33 + 33,34; vergi 20 / 3 → 6,67 + 6,67 + 6,66. Toplam her zaman kabul edilen tutardır.
        var first = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m)], Empty).Lines.Single();
        var ledger = new Dictionary<Guid, ConsumptionState> { [a.StableKey] = new(3m, 100m, 20m, 1m, first.Net, first.Tax) };
        var second = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m)], ledger).Lines.Single();
        ledger[a.StableKey] = new(3m, 100m, 20m, 2m, first.Net + second.Net, first.Tax + second.Tax);
        var third = OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m)], ledger).Lines.Single();

        Assert.Equal(100m, first.Net + second.Net + third.Net);
        Assert.Equal(20m, first.Tax + second.Tax + third.Tax);
        Assert.Equal(33.34m, third.Net);
    }

    [Fact]
    public void Partial_conversion_rejects_empty_duplicate_unknown_and_non_positive_requests()
    {
        var a = Line("Kurulum", 4m, 4000m, 800m);

        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Partial, null, Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 1m), new(a.StableKey, 1m)], Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(Guid.NewGuid(), 1m)], Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, 0m)], Empty).Error);
        Assert.Equal(PlanErrorKind.Invalid, OrderConversionPlanner.Plan([a], ConversionMode.Partial, [new(a.StableKey, -1m)], Empty).Error);
    }
}

public class OrderRulesTests
{
    [Theory]
    [InlineData(OrderStatus.Draft, true)]
    [InlineData(OrderStatus.PendingApproval, false)]
    [InlineData(OrderStatus.Approved, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Closed, false)]
    public void Commercial_fields_are_editable_only_in_draft(string status, bool allowed)
    {
        var result = OrderRules.CanEditContent(status);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(allowed, result.Message is null);
    }

    [Fact]
    public void Order_numbers_have_the_expected_shape_and_differ()
    {
        var at = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

        var first = OrderRules.NewNumber(at);
        var second = OrderRules.NewNumber(at);

        Assert.Matches(@"^SP-20261101-[A-HJ-NP-Z2-9]{6}$", first);
        Assert.NotEqual(first, second);
    }
}
