using Modulix.Core.Views;

namespace Modulix.Core.Tests.Views;

/// <summary>ADR-0036: kişisel liste görünümü doğrulama kuralları (veritabanı yok).</summary>
public class GridViewRulesTests
{
    private static GridViewInput Input(
        string screen = "crm.companies", string name = "Görünüm", string[]? columns = null, string? sort = null, string? filters = null) =>
        new(screen, name, columns ?? ["name", "city"], sort, filters);

    [Fact]
    public void A_complete_valid_view_has_no_errors()
    {
        Assert.Empty(GridViewRules.Validate(Input(sort: "-city", filters: "status=Active&ownerId=me")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void The_name_is_required(string name)
    {
        Assert.Contains(GridViewRules.Validate(Input(name: name)), e => e.Field == "name");
    }

    [Fact]
    public void The_name_has_a_length_limit()
    {
        Assert.Empty(GridViewRules.Validate(Input(name: new string('a', GridView.NameMaxLength))));
        Assert.Contains(GridViewRules.Validate(Input(name: new string('a', GridView.NameMaxLength + 1))), e => e.Field == "name");
    }

    [Theory]
    [InlineData("Crm.Companies")]
    [InlineData("crm companies")]
    [InlineData("")]
    public void The_screen_key_is_lowercase_dotted_text(string screen)
    {
        Assert.Contains(GridViewRules.Validate(Input(screen: screen)), e => e.Field == "screenKey");
    }

    [Fact]
    public void Columns_are_required_unique_safe_keys_within_the_limit()
    {
        Assert.Contains(GridViewRules.Validate(Input(columns: [])), e => e.Field == "columns");
        Assert.Contains(GridViewRules.Validate(Input(columns: ["name", "name"])), e => e.Field == "columns");
        Assert.Contains(GridViewRules.Validate(Input(columns: ["name;drop"])), e => e.Field == "columns");
        Assert.Contains(GridViewRules.Validate(Input(columns: [.. Enumerable.Range(0, GridViewRules.MaxColumns + 1).Select(i => "c" + i)])), e => e.Field == "columns");
        Assert.Empty(GridViewRules.Validate(Input(columns: [.. Enumerable.Range(0, GridViewRules.MaxColumns).Select(i => "c" + i)])));
    }

    [Theory]
    [InlineData("name desc")]
    [InlineData("--name")]
    [InlineData("1name")]
    public void The_sort_key_must_be_a_plain_key_with_an_optional_minus(string sort)
    {
        Assert.Contains(GridViewRules.Validate(Input(sort: sort)), e => e.Field == "sort");
    }

    [Theory]
    [InlineData("pageSize=500")]
    [InlineData("page=2")]
    [InlineData("search=abc")]
    [InlineData("sort=name")]
    [InlineData("status")]
    [InlineData("status=a&&b=c")]
    public void Filters_are_key_value_pairs_without_paging_search_or_sort(string filters)
    {
        Assert.Contains(GridViewRules.Validate(Input(filters: filters)), e => e.Field == "filters");
    }

    [Fact]
    public void Columns_round_trip_through_the_stored_text()
    {
        var stored = GridViewRules.JoinColumns(["name", " city ", "phone"]);

        Assert.Equal("name,city,phone", stored);
        Assert.Equal(["name", "city", "phone"], GridViewRules.SplitColumns(stored));
        Assert.Empty(GridViewRules.SplitColumns(string.Empty));
    }
}
