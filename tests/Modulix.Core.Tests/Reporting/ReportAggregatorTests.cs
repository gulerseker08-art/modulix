using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Customization;
using Modulix.Core.Reporting;
using static Modulix.Core.Tests.Reporting.ReportingKit;

namespace Modulix.Core.Tests.Reporting;

public sealed class ReportAggregatorTests
{
    private static readonly ReportMeasure Count = new(null, Aggregation.Count);
    private static readonly ReportMeasure SumAmount = new("amount", Aggregation.Sum);

    [Fact]
    public void Amounts_are_split_by_currency_never_mixed()
    {
        // Kabul senaryosu 1/8: farklı para birimleri tek toplamda birleşmez.
        var rows = new[]
        {
            Deal("new", 100m, "TRY"), Deal("new", 50m, "TRY"), Deal("new", 10m, "USD"), Deal("won", 200m, "TRY"),
        };

        var result = Compute(Spec(groups: [new("stage")], measures: [Count, SumAmount]), rows);

        var openTry = result.Summary.Single(r => r.Keys[0] == "new" && r.Currency == "TRY");
        var openUsd = result.Summary.Single(r => r.Keys[0] == "new" && r.Currency == "USD");
        Assert.Equal(150m, Sum(openTry, 1));
        Assert.Equal(2m, Sum(openTry, 0));
        Assert.Equal(10m, Sum(openUsd, 1));
        Assert.Equal(3, result.Summary.Count);
        Assert.Contains(result.Totals, t => t is { Currency: "TRY" } && t.Values[1] == 350m);
        Assert.Contains(result.Totals, t => t is { Currency: "USD" } && t.Values[1] == 10m);
    }

    [Fact]
    public void Rows_without_value_or_currency_are_excluded_and_counted_not_assumed_zero()
    {
        var rows = new[] { Deal("new", 100m, "TRY"), Deal("new", null, "TRY"), Deal("new", 40m, null) };

        var result = Compute(Spec(groups: [new("stage")], measures: [Count, SumAmount]), rows);

        Assert.Equal(2, result.ExcludedPerMeasure[1]);
        var group = result.Summary.Single(r => r.Currency == "TRY");
        Assert.Equal(100m, Sum(group, 1));
        Assert.Equal(2m, Sum(group, 0));
        // Para birimi olmayan kayıt kendi grubunda sayılır, ama tutarı toplamaya girmez.
        var noCurrency = result.Summary.Single(r => r.Currency is null);
        Assert.Equal(1m, Sum(noCurrency, 0));
        Assert.Null(Sum(noCurrency, 1));
    }

    [Fact]
    public void Drill_detail_total_equals_the_summary_row_value()
    {
        // Kabul senaryosu 11: grafik/özet satırı ile ayrıntı listesi aynı süzülmüş kümeden türer.
        var rows = new[]
        {
            Deal("new", 100m, "TRY"), Deal("new", 25.5m, "TRY"), Deal("new", 9m, "USD"), Deal("won", 70m, "TRY"),
        };
        var spec = Spec(groups: [new("stage")], measures: [SumAmount]);
        var summary = Compute(spec, rows);
        var row = summary.Summary.Single(r => r.Keys[0] == "new" && r.Currency == "TRY");

        var detail = Compute(spec, rows, new ReportDrill(row.Keys, row.Currency));

        Assert.Equal(2, detail.Matched.Count);
        Assert.Empty(detail.Summary);
        Assert.Equal(Sum(row), detail.Totals.Single().Values[0]);
        Assert.Equal(125.5m, detail.Totals.Single().Values[0]);
    }

    [Fact]
    public void Drill_of_the_empty_group_returns_only_rows_with_no_key()
    {
        var rows = new[] { Deal("new", 1m, closeDate: null), Deal("new", 2m, closeDate: "2026-11-02") };
        var spec = Spec(groups: [new("closeDate", DateGrain.Month)], measures: [Count]);

        var summary = Compute(spec, rows);
        var empty = summary.Summary.Single(r => r.Keys[0] is null);
        var detail = Compute(spec, rows, new ReportDrill([null], empty.Currency));

        Assert.Single(detail.Matched);
    }

    [Fact]
    public void Filters_apply_in_memory_for_every_operator_kind()
    {
        var rows = new[]
        {
            Deal("new", 100m, score: 10, closeDate: "2026-11-01", title: "Alfa", vip: true),
            Deal("won", 200m, score: 20, closeDate: "2026-11-15", title: "Beta", vip: false),
            Deal("lost", 300m, score: null, closeDate: null, title: "Gama"),
        };

        int Matched(params ReportFilter[] filters) => Compute(Spec(columns: ["title"], filters: filters), rows).Matched.Count;

        Assert.Equal(1, Matched(new ReportFilter("stage", FilterOperator.Equals, "WON")));
        Assert.Equal(2, Matched(new ReportFilter("stage", FilterOperator.NotEquals, "won")));
        Assert.Equal(2, Matched(new ReportFilter("stage", FilterOperator.In, Values: ["new", "lost"])));
        Assert.Equal(1, Matched(new ReportFilter("score", FilterOperator.Greater, "10")));
        Assert.Equal(2, Matched(new ReportFilter("score", FilterOperator.GreaterOrEqual, "10")));
        Assert.Equal(1, Matched(new ReportFilter("score", FilterOperator.IsEmpty)));
        Assert.Equal(2, Matched(new ReportFilter("closeDate", FilterOperator.NotEmpty)));
        Assert.Equal(1, Matched(new ReportFilter("closeDate", FilterOperator.Less, "2026-11-10")));
        Assert.Equal(1, Matched(new ReportFilter("closeDate", FilterOperator.GreaterOrEqual, "2026-11-10")));
        Assert.Equal(1, Matched(new ReportFilter("title", FilterOperator.Contains, "ET")));
        Assert.Equal(1, Matched(new ReportFilter("vip", FilterOperator.Equals, "true")));
        // Birden çok süzgeç VE ile birleşir.
        Assert.Equal(1, Matched(new ReportFilter("stage", FilterOperator.NotEquals, "won"), new ReportFilter("score", FilterOperator.Equals, "10")));
    }

    [Fact]
    public void Date_grouping_uses_the_tenant_time_zone_not_utc()
    {
        // 31 Ekim 22:30 UTC = 1 Kasım 01:30 İstanbul (UTC+3): ay Kasım'dır.
        var rows = new[] { Deal(seenAt: "2026-10-31T22:30:00+00:00"), Deal(seenAt: "2026-10-31T20:30:00+00:00") };

        var result = Compute(Spec(groups: [new("seenAt", DateGrain.Month)], measures: [Count]), rows);

        Assert.Equal(1m, result.Summary.Single(r => r.Keys[0] == "2026-11").Values[0]);
        Assert.Equal(1m, result.Summary.Single(r => r.Keys[0] == "2026-10").Values[0]);
    }

    [Theory]
    [InlineData(DateGrain.Day, "2026-11-03")]
    [InlineData(DateGrain.Month, "2026-11")]
    [InlineData(DateGrain.Year, "2026")]
    public void Date_grain_formats_the_group_key(DateGrain grain, string expected)
    {
        var result = Compute(Spec(groups: [new("closeDate", grain)], measures: [Count]), [Deal(closeDate: "2026-11-03")]);

        Assert.Equal(expected, result.Summary.Single().Keys[0]);
    }

    [Fact]
    public void Empty_result_is_a_real_zero_for_count_and_unknown_for_sum()
    {
        // "Veri yok" ile gerçek sıfır ayrımı: sayı 0, tutar toplamı tanımsız (null).
        var result = Compute(Spec(measures: [Count, SumAmount]), []);

        var totals = result.Totals.Single();
        Assert.Equal(0m, totals.Values[0]);
        Assert.Null(totals.Values[1]);
    }

    [Fact]
    public void Summary_is_sorted_by_measure_and_limited_with_a_flag()
    {
        var rows = Enumerable.Range(0, ReportLimits.MaxSummaryRows + 5)
            .Select(i => Deal("new", i + 1, "TRY", closeDate: new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd")))
            .ToList();

        var result = Compute(Spec(groups: [new("closeDate", DateGrain.Day)], measures: [SumAmount], sort: new ReportSort("sum:amount", SortDirection.Descending)), rows);

        Assert.True(result.SummaryLimited);
        Assert.Equal(ReportLimits.MaxSummaryRows, result.Summary.Count);
        Assert.Equal(ReportLimits.MaxSummaryRows + 5m, result.Summary[0].Values[0]);
        // Toplam, sınırlanan satırlardan değil bütün kümeden hesaplanır.
        Assert.Equal(Enumerable.Range(1, ReportLimits.MaxSummaryRows + 5).Sum(i => (decimal)i), result.Totals.Single().Values[0]);
    }

    [Fact]
    public void Average_min_and_max_ignore_missing_values()
    {
        var rows = new[] { Deal(score: 10), Deal(score: 20), Deal(score: null) };

        var result = Compute(Spec(measures: [new("score", Aggregation.Average), new("score", Aggregation.Min), new("score", Aggregation.Max), Count]), rows);

        var totals = result.Totals.Single();
        Assert.Equal(15m, totals.Values[0]);
        Assert.Equal(10m, totals.Values[1]);
        Assert.Equal(20m, totals.Values[2]);
        Assert.Equal(3m, totals.Values[3]);
        Assert.Equal(1, result.ExcludedPerMeasure[0]);
    }

    [Fact]
    public void Table_mode_sorts_typed_with_missing_values_last_in_both_directions()
    {
        var rows = new[] { Deal(score: 5, title: "b"), Deal(score: null, title: "x"), Deal(score: 30, title: "a"), Deal(score: 12, title: "c") };

        var asc = Compute(Spec(columns: ["title", "score"], sort: new ReportSort("score")), rows).Matched.Select(r => r.Values["title"]!.ToString());
        var desc = Compute(Spec(columns: ["title", "score"], sort: new ReportSort("score", SortDirection.Descending)), rows).Matched.Select(r => r.Values["title"]!.ToString());

        Assert.Equal(["b", "c", "a", "x"], asc);
        Assert.Equal(["a", "c", "b", "x"], desc);
    }

    [Fact]
    public void Custom_number_field_can_be_summed_and_choice_field_grouped()
    {
        var number = Custom("vade", "Vade (gün)", CustomFieldType.Number);
        var choice = Custom("segment", "Segment", CustomFieldType.Choice, options: [new("kobi", "KOBİ"), new("kurumsal", "Kurumsal")]);
        var columns = Columns(customs: [number, choice]);
        var rows = new[]
        {
            Deal(custom: """{"vade": 30, "segment": "kobi"}"""), Deal(custom: """{"vade": 45, "segment": "kobi"}"""), Deal(custom: """{"vade": 10, "segment": "kurumsal"}"""),
        };

        var result = Compute(Spec(groups: [new("custom.segment")], measures: [new("custom.vade", Aggregation.Sum)]), rows, columns: columns);

        Assert.Equal(75m, result.Summary.Single(r => r.Keys[0] == "kobi").Values[0]);
        Assert.Equal(10m, result.Summary.Single(r => r.Keys[0] == "kurumsal").Values[0]);
    }

    [Fact]
    public void Group_by_choice_and_second_group_combine_keys()
    {
        var rows = new[] { Deal("new", vip: true), Deal("new", vip: true), Deal("new", vip: false), Deal("won", vip: true) };

        var result = Compute(Spec(groups: [new("stage"), new("vip")], measures: [Count], sort: new ReportSort("stage")), rows);

        Assert.Equal(2m, result.Summary.Single(r => r.Keys[0] == "new" && r.Keys[1] == "true").Values[0]);
        Assert.Equal(3, result.Summary.Count);
    }
}
