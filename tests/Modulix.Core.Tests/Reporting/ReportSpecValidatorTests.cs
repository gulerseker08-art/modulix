using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Customization;
using Modulix.Core.Reporting;
using static Modulix.Core.Tests.Reporting.ReportingKit;

namespace Modulix.Core.Tests.Reporting;

public sealed class ReportSpecValidatorTests
{
    private static IReadOnlyList<ReportIssue> Validate(ReportSpec spec, ReportColumnSet? columns = null)
    {
        columns ??= Columns();
        return ReportSpecValidator.Validate(ReportSpecValidator.Normalize(spec, columns), columns);
    }

    [Fact]
    public void A_table_report_without_columns_gets_the_dataset_defaults()
    {
        var normalized = ReportSpecValidator.Normalize(Spec(), Columns());

        Assert.Equal(["title", "stage", "amount"], normalized.Columns);
        Assert.Empty(ReportSpecValidator.Validate(normalized, Columns()));
    }

    [Fact]
    public void A_grouping_without_measure_counts_records_and_date_groups_default_to_month()
    {
        var normalized = ReportSpecValidator.Normalize(Spec(groups: [new("closeDate")]), Columns());

        Assert.Equal(new ReportMeasure(null, Aggregation.Count), Assert.Single(normalized.Measures));
        Assert.Equal(DateGrain.Month, normalized.Groups[0].Grain);
    }

    [Fact]
    public void Hidden_sensitive_columns_are_unusable_everywhere_not_just_in_the_table()
    {
        // Kabul senaryosu 7: tutar izni olmayan kişi tutarı süzgeçle, gruplamayla ya da toplamla da çıkaramaz.
        var columns = Columns(hidden: ["amount"]);

        var issues = Validate(
            Spec(columns: ["title", "amount"], filters: [new("amount", FilterOperator.Greater, "100")], measures: [new("amount", Aggregation.Sum)]), columns);

        Assert.All(issues.Where(i => i.Field == "amount"), i => Assert.Equal(ReportIssueCode.NotPermitted, i.Code));
        Assert.True(issues.Count(i => i.Field == "amount") >= 2);
        Assert.Null(columns.Find("amount"));
        Assert.True(columns.IsHidden("amount"));
    }

    [Fact]
    public void A_column_hidden_through_its_currency_field_is_hidden_too()
    {
        var columns = Columns(hidden: ["currency"]);

        Assert.True(columns.IsHidden("amount"));
        Assert.Equal(ReportIssueCode.NotPermitted, Validate(Spec(measures: [new("amount", Aggregation.Sum)]), columns).First(i => i.Field == "amount").Code);
    }

    [Fact]
    public void Archived_custom_field_is_reported_as_removed_not_as_unknown()
    {
        // Alan kaldırılırsa rapor geçersiz işaretlenir ve ne olduğu söylenir.
        var archived = Custom("vade", "Vade", CustomFieldType.Number, archived: true);
        var columns = Columns(customs: [archived]);

        var issues = Validate(Spec(measures: [new("custom.vade", Aggregation.Sum)]), columns);

        var issue = Assert.Single(issues);
        Assert.Equal(ReportIssueCode.ColumnRemoved, issue.Code);
        Assert.Contains("arşivlendi", issue.Message);
    }

    [Fact]
    public void Renaming_a_custom_field_keeps_the_report_valid_because_the_key_is_the_identity()
    {
        var spec = Spec(groups: [new("custom.segment")], measures: [new(null, Aggregation.Count)]);
        var before = Columns(customs: [Custom("segment", "Segment", CustomFieldType.Choice, options: [new("a", "A")])]);
        var after = Columns(customs: [Custom("segment", "Müşteri segmenti", CustomFieldType.Choice, options: [new("a", "A")])]);

        Assert.Empty(Validate(spec, before));
        Assert.Empty(Validate(spec, after));
        Assert.Equal("Müşteri segmenti", after.Find("custom.segment")!.Label);
    }

    [Fact]
    public void Custom_field_of_a_dataset_that_does_not_accept_them_is_ignored()
    {
        var closed = Dataset with { AllowsCustomFields = false };
        var columns = ReportColumnSet.Build(closed, new HashSet<string>(), [Custom("vade", "Vade", CustomFieldType.Number)]);

        Assert.Null(columns.Find("custom.vade"));
    }

    [Theory]
    [InlineData("title", FilterOperator.Greater)]
    [InlineData("score", FilterOperator.Contains)]
    [InlineData("vip", FilterOperator.Contains)]
    [InlineData("stage", FilterOperator.Greater)]
    public void Operators_not_allowed_for_the_column_kind_are_rejected(string field, FilterOperator op)
    {
        var issues = Validate(Spec(filters: [new(field, op, "1")]));

        Assert.Contains(issues, i => i.Code == ReportIssueCode.InvalidOperator && i.Field == field);
    }

    [Theory]
    [InlineData("score", "abc")]
    [InlineData("closeDate", "03.11.2026")]
    [InlineData("vip", "belki")]
    [InlineData("ownerId", "not-a-guid")]
    [InlineData("score", "")]
    public void Invalid_filter_values_are_rejected(string field, string value)
    {
        var issues = Validate(Spec(filters: [new(field, FilterOperator.Equals, value)]));

        Assert.Contains(issues, i => i.Code == ReportIssueCode.InvalidValue && i.Field == field);
    }

    [Fact]
    public void In_filter_needs_between_one_and_the_maximum_number_of_values()
    {
        Assert.Contains(Validate(Spec(filters: [new("stage", FilterOperator.In, Values: [])])), i => i.Code == ReportIssueCode.InvalidValue);
        var many = Enumerable.Range(0, ReportLimits.MaxInValues + 1).Select(i => $"v{i}").ToList();
        Assert.Contains(Validate(Spec(filters: [new("stage", FilterOperator.In, Values: many)])), i => i.Code == ReportIssueCode.InvalidValue);
        Assert.Empty(Validate(Spec(filters: [new("stage", FilterOperator.In, Values: ["new", "won"])])));
    }

    [Fact]
    public void Limits_on_filters_groups_measures_and_columns_are_enforced()
    {
        var filters = Enumerable.Range(0, ReportLimits.MaxFilters + 1).Select(_ => new ReportFilter("title", FilterOperator.IsEmpty)).ToList();
        Assert.Contains(Validate(Spec(filters: filters)), i => i.Code == ReportIssueCode.TooMany);

        Assert.Contains(Validate(Spec(groups: [new("stage"), new("vip"), new("closeDate")])), i => i.Code == ReportIssueCode.TooMany);
        Assert.Contains(Validate(Spec(groups: [new("stage"), new("stage")])), i => i.Code == ReportIssueCode.InvalidGrouping);

        var measures = new ReportMeasure[]
        {
            new(null, Aggregation.Count), new("score", Aggregation.Sum), new("score", Aggregation.Average), new("score", Aggregation.Min), new("score", Aggregation.Max),
            new("amount", Aggregation.Sum),
        };
        Assert.Contains(Validate(Spec(measures: measures)), i => i.Code == ReportIssueCode.TooMany);
    }

    [Fact]
    public void Money_and_number_cannot_be_grouped_and_text_cannot_be_summed()
    {
        Assert.Contains(Validate(Spec(groups: [new("amount")])), i => i.Code == ReportIssueCode.UnknownColumn);
        Assert.Contains(Validate(Spec(measures: [new("title", Aggregation.Sum)])), i => i.Code == ReportIssueCode.UnknownColumn);
        Assert.Contains(Validate(Spec(measures: [new("score", Aggregation.Count)])), i => i.Code == ReportIssueCode.InvalidMeasure);
        Assert.Contains(Validate(Spec(groups: [new("stage", DateGrain.Month)])), i => i.Code == ReportIssueCode.InvalidGrouping);
    }

    [Fact]
    public void An_unknown_column_is_named_in_the_issue()
    {
        var issue = Assert.Single(Validate(Spec(columns: ["nope"])));

        Assert.Equal(ReportIssueCode.UnknownColumn, issue.Code);
        Assert.Equal("nope", issue.Field);
    }

    [Fact]
    public void Chart_needs_a_grouping_and_a_measure()
    {
        Assert.Empty(Validate(Spec(groups: [new("stage")], measures: [new(null, Aggregation.Count)], chart: ChartKind.Bar)));
        Assert.Contains(Validate(Spec(measures: [new(null, Aggregation.Count)], chart: ChartKind.Bar)), i => i.Code == ReportIssueCode.InvalidChart);
        Assert.Contains(Validate(Spec(groups: [new("stage")], measures: [new(null, Aggregation.Count)], chart: ChartKind.Line)), i => i.Code == ReportIssueCode.InvalidChart);
    }

    [Fact]
    public void Line_chart_is_valid_with_a_date_group_and_pie_rejects_two_groups()
    {
        Assert.Empty(Validate(Spec(groups: [new("closeDate")], measures: [new(null, Aggregation.Count)], chart: ChartKind.Line)));
        Assert.Contains(
            Validate(Spec(groups: [new("stage"), new("vip")], measures: [new(null, Aggregation.Count)], chart: ChartKind.Pie)),
            i => i.Code == ReportIssueCode.InvalidChart);
    }

    [Fact]
    public void Sort_must_refer_to_something_in_the_report()
    {
        Assert.Contains(Validate(Spec(columns: ["title"], sort: new ReportSort("score"))), i => i.Code == ReportIssueCode.InvalidSort);
        Assert.Contains(Validate(Spec(groups: [new("stage")], measures: [new(null, Aggregation.Count)], sort: new ReportSort("sum:amount"))), i => i.Code == ReportIssueCode.InvalidSort);
        Assert.Empty(Validate(Spec(groups: [new("stage")], measures: [new(null, Aggregation.Count)], sort: new ReportSort("count", SortDirection.Descending))));
    }

    [Fact]
    public void Measures_on_money_need_a_currency_field_and_the_same_one()
    {
        var two = Dataset with
        {
            Columns =
            [
                .. Dataset.Columns,
                new DatasetColumn("wonAmount", "Kazanılan", DatasetColumnKind.Money, ColumnUse.Measure | ColumnUse.Show) { CurrencyField = "wonCurrency" },
            ],
        };
        var columns = ReportColumnSet.Build(two, new HashSet<string>(), []);
        var spec = Spec(measures: [new("amount", Aggregation.Sum), new("wonAmount", Aggregation.Sum)]);

        Assert.Contains(ReportSpecValidator.Validate(ReportSpecValidator.Normalize(spec, columns), columns), i => i.Code == ReportIssueCode.InvalidMeasure);
    }

    [Fact]
    public void Spec_round_trips_through_json_with_named_enums()
    {
        var spec = Spec(
            filters: [new("score", FilterOperator.GreaterOrEqual, "5")], groups: [new("closeDate", DateGrain.Year)], measures: [new("amount", Aggregation.Average)],
            sort: new ReportSort("average:amount", SortDirection.Descending), chart: ChartKind.Bar);

        var json = ReportSpecJson.Serialize(spec);
        var back = ReportSpecJson.Deserialize(json);

        Assert.Contains("\"greaterOrEqual\"", json);
        Assert.Contains("\"average\"", json);
        Assert.Contains("\"descending\"", json);
        Assert.Equal(spec.Filters, back.Filters);
        Assert.Equal(spec.Groups, back.Groups);
        Assert.Equal(spec.Measures, back.Measures);
        Assert.Equal(spec.Sort, back.Sort);
        Assert.Equal(ChartKind.Bar, back.Chart);
    }

    [Fact]
    public void Missing_lists_in_stored_json_become_empty_lists()
    {
        var back = ReportSpecJson.Deserialize("""{"dataset":"sales.deals"}""");

        Assert.Empty(back.Columns);
        Assert.Empty(back.Filters);
        Assert.Empty(back.Groups);
        Assert.Empty(back.Measures);
    }

    [Fact]
    public void Unknown_enum_text_in_json_is_a_parse_error()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            ReportSpecJson.Deserialize("""{"dataset":"sales.deals","filters":[{"field":"score","operator":"between","value":"1"}]}"""));
    }

    [Fact]
    public void Measure_ids_are_stable_names_used_by_sorting()
    {
        Assert.Equal("count", ReportSpecValidator.MeasureId(new ReportMeasure(null, Aggregation.Count)));
        Assert.Equal("sum:amount", ReportSpecValidator.MeasureId(new ReportMeasure("amount", Aggregation.Sum)));
    }
}
