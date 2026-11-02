using Modulix.Core.Contracts.Marketing;
using Modulix.Modules.Marketing.Segments;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 2: koşul ağacının ayrıştırılması, sınırları ve alan/operatör/değer doğrulaması (saf, veritabansız).</summary>
public class SegmentConditionsTests
{
    private static readonly IReadOnlyDictionary<string, SegmentFieldDefinition> Fields = new[]
    {
        new SegmentFieldDefinition("t.city", "Şehir", "G", SegmentTarget.Company, SegmentFieldType.Text, "t.company"),
        new SegmentFieldDefinition("t.status", "Durum", "G", SegmentTarget.Company, SegmentFieldType.Choice, "t.company")
        {
            Options = new Dictionary<string, string> { ["Customer"] = "Müşteri", ["Prospect"] = "Aday" },
        },
        new SegmentFieldDefinition("t.created", "Tarih", "G", SegmentTarget.Company, SegmentFieldType.Date, "t.company"),
        new SegmentFieldDefinition("t.revenue", "Ciro", "G", SegmentTarget.Company, SegmentFieldType.Number, "t.company"),
        new SegmentFieldDefinition("t.vip", "VIP", "G", SegmentTarget.Company, SegmentFieldType.Bool, "t.company"),
        new SegmentFieldDefinition("t.owner", "Sorumlu", "G", SegmentTarget.Company, SegmentFieldType.Id, "t.company"),
        new SegmentFieldDefinition("t.opps", "Fırsatı var", "G", SegmentTarget.Company, SegmentFieldType.Relation, "t.opp"),
        new SegmentFieldDefinition("t.leadCity", "Lead şehri", "G", SegmentTarget.Lead, SegmentFieldType.Text, "t.lead"),
    }.ToDictionary(f => f.Key);

    private static string Leaf(string field, string op, params string[] values) =>
        $$"""{"field":"{{field}}","operator":"{{op}}","values":[{{string.Join(',', values.Select(v => $"\"{v}\""))}}]}""";

    private static IReadOnlyList<ConditionError> Check(string json)
    {
        var (condition, errors) = SegmentConditions.Parse(json);
        Assert.NotNull(condition);
        Assert.Empty(errors);
        return SegmentConditions.Validate(condition, Fields, SegmentTarget.Company);
    }

    [Fact]
    public void Nested_and_or_groups_parse_and_round_trip_to_canonical_json()
    {
        var json = $$"""{"op":"AND","items":[{{Leaf("t.city", "eq", "Bursa")}},{"op":"or","items":[{{Leaf("t.status", "eq", "Customer")}},{{Leaf("t.status", "in", "Prospect", "Customer")}}]}]}""";

        var (condition, errors) = SegmentConditions.Parse(json);

        Assert.Empty(errors);
        Assert.Equal("and", condition!.Group);
        Assert.Equal(3, SegmentConditions.Leaves(condition).Count());
        var canonical = SegmentConditions.Serialize(condition);
        Assert.Equal(canonical, SegmentConditions.Serialize(SegmentConditions.Parse(canonical).Condition!));
        Assert.Empty(SegmentConditions.Validate(condition, Fields, SegmentTarget.Company));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"op":"and","items":[]}""")]
    [InlineData("""{"op":"xor","items":[{"field":"a","operator":"eq","values":["1"]}]}""")]
    [InlineData("""{"field":"a","operator":"eq","values":["1"],"sql":"1=1"}""")]
    [InlineData("""{"field":"a","operator":"1","values":["1"]}""")]
    [InlineData("""{"field":"a","operator":"drop","values":["1"]}""")]
    [InlineData("""{"field":"a","operator":"eq","values":"x"}""")]
    [InlineData("""{"op":"and","field":"a","operator":"eq","values":["1"]}""")]
    public void Malformed_conditions_are_rejected_with_errors(string json)
    {
        var (condition, errors) = SegmentConditions.Parse(json);

        Assert.Null(condition);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Depth_and_leaf_count_are_limited()
    {
        var leaf = Leaf("t.city", "eq", "Bursa");
        var deep = leaf;
        for (var i = 0; i < SegmentConditions.MaxDepth; i++)
        {
            deep = $$"""{"op":"and","items":[{{deep}}]}""";
        }

        Assert.Contains(SegmentConditions.Parse(deep).Errors, e => e.Message.Contains("derinliği", StringComparison.Ordinal));

        var wide = $$"""{"op":"or","items":[{{string.Join(',', Enumerable.Repeat(leaf, SegmentConditions.MaxLeaves + 1))}}]}""";
        Assert.Contains(SegmentConditions.Parse(wide).Errors, e => e.Message.Contains("En fazla", StringComparison.Ordinal));

        var limit = $$"""{"op":"or","items":[{{string.Join(',', Enumerable.Repeat(leaf, SegmentConditions.MaxLeaves))}}]}""";
        Assert.Empty(SegmentConditions.Parse(limit).Errors);
    }

    [Fact]
    public void Unknown_field_or_wrong_target_is_reported_the_same_way()
    {
        Assert.Single(Check(Leaf("t.yok", "eq", "x")));
        Assert.Single(Check(Leaf("t.leadCity", "eq", "x")));
    }

    [Theory]
    [InlineData("t.city", "between", "a", "b")]
    [InlineData("t.status", "contains", "Cust")]
    [InlineData("t.created", "eq", "2026-01-01")]
    [InlineData("t.vip", "ne", "true")]
    [InlineData("t.opps", "eq", "1")]
    [InlineData("t.owner", "contains", "x")]
    public void Operators_must_match_the_field_type(string field, string op, string value, string? second = null)
    {
        var values = second is null ? new[] { value } : [value, second];
        Assert.Contains(Check(Leaf(field, op, values)), e => e.Path == "operator");
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("contains")]
    public void Value_count_follows_the_operator(string op)
    {
        Assert.Contains(Check(Leaf("t.city", op)), e => e.Path == "values");
        Assert.Contains(Check(Leaf("t.city", op, "a", "b")), e => e.Path == "values");
        Assert.Empty(Check(Leaf("t.city", op, "a")));
    }

    [Fact]
    public void Empty_operators_take_no_value_and_in_takes_many()
    {
        Assert.Empty(Check(Leaf("t.city", "empty")));
        Assert.Empty(Check(Leaf("t.city", "notempty")));
        Assert.NotEmpty(Check(Leaf("t.city", "empty", "x")));
        Assert.Empty(Check(Leaf("t.city", "in", "Bursa", "İzmir", "Kocaeli")));
        Assert.NotEmpty(Check(Leaf("t.city", "in")));
    }

    [Fact]
    public void Choice_values_must_be_in_the_option_list()
    {
        Assert.Empty(Check(Leaf("t.status", "eq", "Customer")));
        Assert.Contains(Check(Leaf("t.status", "eq", "Bilinmeyen")), e => e.Message.Contains("Listede olmayan", StringComparison.Ordinal));
    }

    [Fact]
    public void Dates_need_iso_days_and_ranges_must_not_be_reversed()
    {
        Assert.Empty(Check(Leaf("t.created", "between", "2026-01-01", "2026-01-31")));
        Assert.NotEmpty(Check(Leaf("t.created", "between", "2026-02-01", "2026-01-31")));
        Assert.NotEmpty(Check(Leaf("t.created", "after", "01.01.2026")));
        Assert.NotEmpty(Check(Leaf("t.created", "before", "2026-02-30")));
    }

    [Theory]
    [InlineData("lastdays", "30", true)]
    [InlineData("nextdays", "60", true)]
    [InlineData("lastdays", "0", false)]
    [InlineData("lastdays", "-5", false)]
    [InlineData("nextdays", "99999", false)]
    [InlineData("lastdays", "otuz", false)]
    public void Relative_day_windows_are_bounded(string op, string days, bool valid) =>
        Assert.Equal(valid, Check(Leaf("t.created", op, days)).Count == 0);

    [Theory]
    [InlineData("12.5", true)]
    [InlineData("abc", false)]
    public void Numbers_are_parsed_with_the_invariant_culture(string value, bool valid) =>
        Assert.Equal(valid, Check(Leaf("t.revenue", "eq", value)).Count == 0);

    [Fact]
    public void Relations_take_an_optional_day_window()
    {
        Assert.Empty(Check(Leaf("t.opps", "exists")));
        Assert.Empty(Check(Leaf("t.opps", "notexists")));
        Assert.Empty(Check(Leaf("t.opps", "exists", "90")));
        Assert.NotEmpty(Check(Leaf("t.opps", "exists", "0")));
        Assert.NotEmpty(Check(Leaf("t.opps", "exists", "90", "30")));
    }

    [Fact]
    public void Booleans_and_ids_are_checked()
    {
        Assert.Empty(Check(Leaf("t.vip", "eq", "true")));
        Assert.NotEmpty(Check(Leaf("t.vip", "eq", "evet")));
        Assert.Empty(Check(Leaf("t.owner", "eq", Guid.NewGuid().ToString())));
        Assert.NotEmpty(Check(Leaf("t.owner", "eq", "kimlik")));
        Assert.NotEmpty(Check(Leaf("t.owner", "eq", Guid.Empty.ToString())));
    }

    [Fact]
    public void Overlong_values_are_rejected()
    {
        Assert.NotEmpty(Check(Leaf("t.city", "eq", new string('a', SegmentConditions.MaxValueLength + 1))));
    }
}
