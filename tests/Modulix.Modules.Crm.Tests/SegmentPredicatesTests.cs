using Modulix.Core.Contracts.Marketing;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Tests;

/// <summary>
/// ADR-0025 dilim 2: segment koşullarının ifade ağacı davranışı (NULL, büyük küçük harf, gün sınırları). Ağaçlar bellekte derlenerek denenir;
/// EF aynı ağacı SQL'e çevirir, tenant ve kapsam entegrasyon testlerindedir.
/// </summary>
public class SegmentPredicatesTests
{
    private sealed record Row(string Name, string? City, DateTimeOffset? At, Guid? Owner);

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>Bugün 15 Mart 2026 (İstanbul); gün başlangıçları UTC+3.</summary>
    private static SegmentEvaluationContext Context(DateOnly? today = null) => new(
        null,
        today ?? new DateOnly(2026, 3, 15),
        day => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Istanbul.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))));

    private static SegmentLeaf Leaf(SegmentOperator op, params string[] values) => new("f", op, values);

    private static readonly Row[] Rows =
    [
        new("a", "Bursa", null, null),
        new("b", "İzmir", null, null),
        new("c", null, null, null),
        new("d", "", null, null),
        new("e", "Bursa nilüfer", null, null),
    ];

    private static string[] Names(SegmentOperator op, params string[] values) =>
        [.. Predicates.Text(Rows.AsQueryable(), r => r.City, Leaf(op, values)).Select(r => r.Name)];

    [Fact]
    public void Equality_and_contains_never_match_null()
    {
        Assert.Equal(["a"], Names(SegmentOperator.Eq, "Bursa"));
        Assert.Equal(["a", "e"], Names(SegmentOperator.Contains, "Bursa"));
        Assert.DoesNotContain("c", Names(SegmentOperator.Eq, ""));
        Assert.DoesNotContain("c", Names(SegmentOperator.In, "Bursa", "İzmir"));
    }

    [Fact]
    public void Not_equal_includes_null_values_by_definition()
    {
        Assert.Equal(["b", "c", "d", "e"], Names(SegmentOperator.Ne, "Bursa"));
    }

    [Fact]
    public void Empty_covers_null_and_blank_and_not_empty_is_the_complement()
    {
        Assert.Equal(["c", "d"], Names(SegmentOperator.Empty));
        Assert.Equal(["a", "b", "e"], Names(SegmentOperator.NotEmpty));
    }

    [Fact]
    public void In_matches_any_listed_value()
    {
        Assert.Equal(["a", "b"], Names(SegmentOperator.In, "Bursa", "İzmir"));
    }

    private static string[] DateNames(SegmentOperator op, SegmentEvaluationContext context, params string[] values)
    {
        // Gün sınırı örnekleri: 15 Mart'tan önceki gece yarısına çok yakın iki an.
        Row[] rows =
        [
            new("before-window", null, At(2026, 3, 8, 23, 59), null),
            new("window-start", null, At(2026, 3, 9, 0, 0), null),
            new("today-noon", null, At(2026, 3, 15, 12, 0), null),
            new("today-late", null, At(2026, 3, 15, 23, 59), null),
            new("tomorrow", null, At(2026, 3, 16, 0, 0), null),
            new("plus-sixty", null, At(2026, 5, 14, 12, 0), null),
            new("never", null, null, null),
        ];
        return [.. Predicates.Date(rows.AsQueryable(), r => r.At, Leaf(op, values), context).Select(r => r.Name)];
    }

    /// <summary>İstanbul yerel zamanı (UTC+3) olarak verilen an.</summary>
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Last_days_includes_today_and_uses_tenant_day_boundaries()
    {
        // Son 7 gün: 9 Mart 00:00 (dahil) – 16 Mart 00:00 (hariç).
        Assert.Equal(["window-start", "today-noon", "today-late"], DateNames(SegmentOperator.LastDays, Context(), "7"));
    }

    [Fact]
    public void Next_days_runs_from_today_inclusive()
    {
        Assert.Equal(["today-noon", "today-late", "tomorrow"], DateNames(SegmentOperator.NextDays, Context(), "1"));
        Assert.Contains("plus-sixty", DateNames(SegmentOperator.NextDays, Context(), "60"));
        Assert.DoesNotContain("plus-sixty", DateNames(SegmentOperator.NextDays, Context(), "59"));
    }

    [Fact]
    public void Between_is_inclusive_of_both_days_and_before_after_exclude_the_day_itself()
    {
        Assert.Equal(["today-noon", "today-late"], DateNames(SegmentOperator.Between, Context(), "2026-03-15", "2026-03-15"));
        Assert.DoesNotContain("today-noon", DateNames(SegmentOperator.Before, Context(), "2026-03-15"));
        Assert.DoesNotContain("today-late", DateNames(SegmentOperator.After, Context(), "2026-03-15"));
        Assert.Contains("tomorrow", DateNames(SegmentOperator.After, Context(), "2026-03-15"));
    }

    [Fact]
    public void Null_dates_match_only_empty_and_never_a_range()
    {
        foreach (var op in new[] { SegmentOperator.LastDays, SegmentOperator.NextDays, SegmentOperator.Before, SegmentOperator.After })
        {
            Assert.DoesNotContain("never", DateNames(op, Context(), op is SegmentOperator.Before or SegmentOperator.After ? "2026-03-15" : "30"));
        }

        Assert.Equal(["never"], DateNames(SegmentOperator.Empty, Context()));
        Assert.DoesNotContain("never", DateNames(SegmentOperator.NotEmpty, Context()));
    }

    [Fact]
    public void Id_equality_and_emptiness()
    {
        var owner = Guid.NewGuid();
        Row[] rows = [new("x", null, null, owner), new("y", null, null, Guid.NewGuid()), new("z", null, null, null)];

        string[] Run(SegmentOperator op, params string[] values) =>
            [.. Predicates.Id(rows.AsQueryable(), r => r.Owner, Leaf(op, values)).Select(r => r.Name)];

        Assert.Equal(["x"], Run(SegmentOperator.Eq, owner.ToString()));
        Assert.Equal(["x"], Run(SegmentOperator.In, owner.ToString(), Guid.NewGuid().ToString()));
        Assert.Equal(["z"], Run(SegmentOperator.Empty));
        Assert.Equal(["x", "y"], Run(SegmentOperator.NotEmpty));
    }

    [Fact]
    public void Operator_not_valid_for_the_type_fails_loudly_instead_of_matching_everything()
    {
        Assert.Throws<FormatException>(() => Predicates.Text(Rows.AsQueryable(), r => r.City, Leaf(SegmentOperator.Between, "a", "b")).ToList());
    }
}
