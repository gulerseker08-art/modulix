using Modulix.Core.Pricing;

namespace Modulix.Core.Tests.Pricing;

/// <summary>Teklif hesaplama motoru: indirim sırası, genel indirim dağıtımı, vergi grupları, opsiyonel/paket/tekrarlayan kalemler.</summary>
public class QuoteCalculatorTests
{
    private static readonly QuotePolicy Exclusive = new(false);

    private static QuoteLineInput Line(decimal qty, decimal price, decimal tax = 20m, QuoteLineRole role = QuoteLineRole.Standard,
        QuoteDiscountKind kind = QuoteDiscountKind.None, decimal discount = 0m, bool selected = false, Guid? parent = null, string? period = null) =>
        new(Guid.NewGuid(), qty, price, tax, role, kind, discount, selected, parent, period);

    [Fact]
    public void Line_discount_then_tax_gives_1800_net_and_2160_total()
    {
        var result = QuoteCalculator.Calculate([Line(2, 1000m, 20m, kind: QuoteDiscountKind.Percent, discount: 10m)], Exclusive);

        Assert.Equal(1800m, result.OneTime.Net);
        Assert.Equal(360m, result.OneTime.Tax);
        Assert.Equal(2160m, result.OneTime.Gross);
    }

    [Fact]
    public void Document_discount_is_allocated_and_sums_exactly_with_mixed_tax_rates()
    {
        var lines = new[] { Line(1, 100m, 10m), Line(1, 100m, 20m), Line(1, 100m, 0m) };

        var result = QuoteCalculator.Calculate(lines, Exclusive, QuoteDiscountKind.Amount, 10m);

        Assert.Equal(10m, result.Lines.Sum(l => l.DocumentDiscountShare));
        Assert.Equal(290m, result.OneTime.Net);
        Assert.Equal(3, result.TaxGroups.Count);
        Assert.Equal(result.OneTime.Net, result.TaxGroups.Sum(g => g.Net));
        Assert.Equal(result.OneTime.Gross, result.OneTime.Net + result.OneTime.Tax);
    }

    [Fact]
    public void Rounding_remainder_goes_to_largest_line()
    {
        var lines = new[] { Line(1, 33.33m, 0m), Line(1, 33.33m, 0m), Line(1, 33.34m, 0m) };

        var result = QuoteCalculator.Calculate(lines, Exclusive, QuoteDiscountKind.Amount, 10m);

        Assert.Equal(10m, result.DocumentDiscountTotal);
        Assert.Equal(10m, result.Lines.Sum(l => l.DocumentDiscountShare));
        Assert.Equal(90m, result.OneTime.Net);
    }

    [Fact]
    public void Tax_inclusive_prices_extract_net_from_gross()
    {
        var result = QuoteCalculator.Calculate([Line(1, 120m, 20m)], new QuotePolicy(true));

        Assert.Equal(120m, result.OneTime.Gross);
        Assert.Equal(100m, result.OneTime.Net);
        Assert.Equal(20m, result.OneTime.Tax);
    }

    [Fact]
    public void Unselected_optional_is_excluded_from_main_total_but_reported()
    {
        var result = QuoteCalculator.Calculate([Line(1, 1000m, 0m), Line(1, 500m, 0m, QuoteLineRole.Optional)], Exclusive);

        Assert.Equal(1000m, result.OneTime.Net);
        Assert.Equal(500m, result.OptionalNet);
    }

    [Fact]
    public void Selected_alternative_joins_total()
    {
        var result = QuoteCalculator.Calculate([Line(1, 1000m, 0m), Line(1, 500m, 0m, QuoteLineRole.Alternative, selected: true)], Exclusive);

        Assert.Equal(1500m, result.OneTime.Net);
        Assert.Equal(0m, result.OptionalNet);
    }

    [Fact]
    public void Package_children_are_not_double_counted()
    {
        var package = Line(1, 1000m, 0m);
        var child = Line(2, 300m, 0m, QuoteLineRole.PackageChild, parent: package.Id);

        var result = QuoteCalculator.Calculate([package, child], Exclusive);

        Assert.Equal(1000m, result.OneTime.Net);
    }

    [Fact]
    public void Package_child_needs_valid_parent()
    {
        var ex = Assert.Throws<PricingException>(() => QuoteCalculator.Calculate([Line(1, 10m, 0m, QuoteLineRole.PackageChild)], Exclusive));

        Assert.Equal(QuoteErrors.InvalidParent, ex.Code);
    }

    [Fact]
    public void Recurring_amounts_are_kept_apart_from_one_time()
    {
        var result = QuoteCalculator.Calculate([Line(1, 1000m, 0m), Line(1, 200m, 0m, period: "Monthly")], Exclusive);

        Assert.Equal(1000m, result.OneTime.Net);
        var monthly = Assert.Single(result.Recurring);
        Assert.Equal("Monthly", monthly.Period);
        Assert.Equal(200m, monthly.Net);
    }

    [Theory]
    [InlineData(0, 10, QuoteErrors.InvalidQuantity)]
    [InlineData(-1, 10, QuoteErrors.InvalidQuantity)]
    [InlineData(1, -5, QuoteErrors.NegativePrice)]
    public void Invalid_quantity_or_price_is_rejected(int qty, int price, string code)
    {
        var ex = Assert.Throws<PricingException>(() => QuoteCalculator.Calculate([Line(qty, price)], Exclusive));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Excessive_discounts_are_rejected()
    {
        Assert.Equal(QuoteErrors.DiscountExceedsAmount, Assert.Throws<PricingException>(() =>
            QuoteCalculator.Calculate([Line(1, 100m, kind: QuoteDiscountKind.Amount, discount: 101m)], Exclusive)).Code);
        Assert.Equal(QuoteErrors.InvalidDiscount, Assert.Throws<PricingException>(() =>
            QuoteCalculator.Calculate([Line(1, 100m)], Exclusive, QuoteDiscountKind.Percent, 150m)).Code);
    }

    [Fact]
    public void Zero_priced_line_is_allowed_and_totals_stay_zero()
    {
        var result = QuoteCalculator.Calculate([Line(1, 0m, 20m)], Exclusive);

        Assert.Equal(0m, result.OneTime.Gross);
    }
}
