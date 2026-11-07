using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>CRM panosunun kısa tutar ve tempo yardımcıları.</summary>
public class SalesLabelsTests
{
    [Theory]
    [InlineData(6_840_000, "6,84 Mn")]
    [InlineData(10_000_000, "10,0 Mn")]
    [InlineData(18_420_000, "18,4 Mn")]
    [InlineData(640_000, "640 B")]
    [InlineData(2_500_000_000, "2,5 Mr")]
    [InlineData(950, "950")]
    [InlineData(0, "0")]
    public void Short_writes_the_value_with_its_unit(long value, string expected)
    {
        Assert.Equal(expected, SalesLabels.Short(value));
    }

    [Fact]
    public void Compact_puts_the_lira_sign_first_and_other_currencies_last()
    {
        Assert.Equal("₺6,84 Mn", SalesLabels.Compact(6_840_000m, "TRY"));
        Assert.Equal("640 B USD", SalesLabels.Compact(640_000m, "USD"));
    }

    [Theory]
    [InlineData("FY2026-Q4", "26-Q4")]
    [InlineData("2026-11", "26-11")]
    [InlineData("Mart", "Mart")]
    public void Short_period_drops_the_fiscal_year_prefix(string label, string expected)
    {
        Assert.Equal(expected, SalesLabels.ShortPeriod(label));
    }

    [Fact]
    public void Elapsed_percent_counts_today_as_passed()
    {
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 12, 31);

        Assert.Equal(40m, Math.Round(SalesLabels.PeriodElapsedPercent(start, end, new DateOnly(2026, 11, 6))!.Value));
        Assert.Equal(100m, SalesLabels.PeriodElapsedPercent(start, end, end));
    }

    [Fact]
    public void Elapsed_percent_is_empty_outside_the_period()
    {
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 12, 31);

        Assert.Null(SalesLabels.PeriodElapsedPercent(start, end, new DateOnly(2026, 9, 30)));
        Assert.Null(SalesLabels.PeriodElapsedPercent(start, end, new DateOnly(2027, 1, 1)));
    }
}
