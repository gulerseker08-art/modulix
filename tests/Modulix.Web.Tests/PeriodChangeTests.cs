using System.Globalization;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Ana sayfa dönem karşılaştırması: yön, rozet sınıfı, ikon ve işaretli metin.</summary>
public class PeriodChangeTests
{
    private static readonly NumberFormatInfo Turkish = CultureInfo.GetCultureInfo("tr-TR").NumberFormat;

    [Theory]
    [InlineData(5, 2, 1, null, "arrow-up")]
    [InlineData(2, 5, -1, "is-down", "arrow-down")]
    [InlineData(3, 3, 0, "is-flat", null)]
    [InlineData(0, 0, 0, "is-flat", null)]
    public void Direction_sets_trend_badge_class_and_icon(int current, int previous, int trend, string? css, string? icon)
    {
        PeriodChange change = new(current, previous);

        Assert.Equal(trend, change.Trend);
        Assert.Equal(css, change.CssClass);
        Assert.Equal(icon, change.Icon);
    }

    [Fact]
    public void Text_is_signed_and_grouped_and_zero_has_no_sign()
    {
        Assert.Equal("+1.250", new PeriodChange(1250, 10).Text(Turkish));
        Assert.Equal("+1", new PeriodChange(1, 4).Text(Turkish));
        Assert.Equal("0", new PeriodChange(0, 4).Text(Turkish));
    }
}
