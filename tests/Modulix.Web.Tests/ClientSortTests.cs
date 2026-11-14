using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>İstemcide sıralama: yön, Türkçe sıra, boş değerler, kararlılık ve bilinmeyen anahtar.</summary>
public class ClientSortTests
{
    private sealed record Row(string Name, int? Seats, bool Active);

    private static readonly Row[] Rows =
    [
        new("Şahin", 5, true),
        new("çınar", null, false),
        new("Zeytin", 20, true),
        new("Ihlamur", 5, false),
        new("İncir", 1, true),
    ];

    private static readonly Dictionary<string, Func<Row, object?>> Columns = new()
    {
        ["name"] = r => r.Name,
        ["seats"] = r => r.Seats,
        ["active"] = r => r.Active,
    };

    [Fact]
    public void Text_is_sorted_in_turkish_order_ignoring_case()
    {
        Assert.Equal(["çınar", "Ihlamur", "İncir", "Şahin", "Zeytin"], ClientSort.Apply(Rows, "name", Columns).Select(r => r.Name));
        Assert.Equal(["Zeytin", "Şahin", "İncir", "Ihlamur", "çınar"], ClientSort.Apply(Rows, "-name", Columns).Select(r => r.Name));
    }

    [Fact]
    public void Empty_values_stay_last_in_both_directions_and_equal_rows_keep_their_order()
    {
        Assert.Equal(["İncir", "Şahin", "Ihlamur", "Zeytin", "çınar"], ClientSort.Apply(Rows, "seats", Columns).Select(r => r.Name));
        Assert.Equal(["Zeytin", "Şahin", "Ihlamur", "İncir", "çınar"], ClientSort.Apply(Rows, "-seats", Columns).Select(r => r.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("password")]
    [InlineData("-password")]
    public void Blank_or_unknown_key_keeps_the_original_order(string? sort)
    {
        Assert.Equal(Rows, ClientSort.Apply(Rows, sort, Columns));
    }

    [Fact]
    public void Booleans_sort_false_before_true()
    {
        Assert.Equal([false, false, true, true, true], ClientSort.Apply(Rows, "active", Columns).Select(r => r.Active));
    }
}
