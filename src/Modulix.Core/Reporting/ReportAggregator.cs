using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>Özet satırı: grup anahtarları, para birimi (tutar ölçüsü varsa) ve ölçü değerleri (ölçü sırasıyla; değer yoksa null).</summary>
public sealed record ReportSummaryRow(IReadOnlyList<string?> Keys, string? Currency, IReadOnlyList<decimal?> Values, int RowCount);

/// <summary>Hesap sonucu. <see cref="Matched"/> süzgeç (ve ayrıntı) sonrası kayıtlardır; özet satırları ve toplamlar aynı kümeden hesaplanır.</summary>
public sealed record ReportComputation(
    IReadOnlyList<RecordData> Matched,
    IReadOnlyList<ReportSummaryRow> Summary,
    IReadOnlyList<ReportSummaryRow> Totals,
    IReadOnlyList<int> ExcludedPerMeasure,
    bool SummaryLimited);

/// <summary>
/// Rapor hesabı (saf: veritabanı, izin ve kullanıcı bilmez). Girdi, kayıt boru hattından gelmiş (izin, kapsam ve hassas alan kuralları
/// uygulanmış) kayıtlardır; süzgeç, gruplama, toplama ve sıralama bellekte yapılır. Kart/grafik, özet satırı ve ayrıntı listesi aynı süzülmüş
/// kümeden türer: bir özet satırının değeri, o satırın ayrıntısındaki kayıtların aynı toplamına eşittir. Tutar toplamları para birimine göre
/// ayrılır (para birimleri toplanmaz); değeri ya da para birimi olmayan kayıt toplama girmez ve <see cref="ReportComputation.ExcludedPerMeasure"/>
/// ile bildirilir (sıfır varsayılmaz).
/// </summary>
public static class ReportAggregator
{
    public static ReportComputation Compute(
        ReportSpec spec, ReportColumnSet columns, IEnumerable<RecordData> rows, ReportDrill? drill, TimeZoneInfo zone)
    {
        var moneyCurrency = MoneyCurrencyField(spec, columns);
        var matched = rows.Where(r => spec.Filters.All(f => columns.Find(f.Field) is { } c && Matches(f, c, r.Values, zone))).ToList();
        if (drill is not null)
        {
            matched = [.. matched.Where(r => InDrill(spec, columns, drill, moneyCurrency, r.Values, zone))];
        }

        var excluded = new int[spec.Measures.Count];
        var totals = Accumulate(spec, columns, matched, [], moneyCurrency, zone, excluded);
        if (!spec.IsSummary || drill is not null)
        {
            return new ReportComputation(Sorted(spec, columns, matched, zone), [], totals, excluded, false);
        }

        // Toplamda sayılan dışlanmış kayıt adedi zaten bulundu; gruplu hesapta tekrar sayılmaz.
        var summary = Accumulate(spec, columns, matched, spec.Groups, moneyCurrency, zone, new int[spec.Measures.Count]);
        summary = SortSummary(spec, summary, columns);
        var limited = summary.Count > ReportLimits.MaxSummaryRows;
        return new ReportComputation(matched, [.. summary.Take(ReportLimits.MaxSummaryRows)], totals, excluded, limited);
    }

    /// <summary>Tutar ölçüsü varsa onların ortak para birimi alanı (doğrulama ortak olmasını ister); yoksa null.</summary>
    public static string? MoneyCurrencyField(ReportSpec spec, ReportColumnSet columns) =>
        spec.Measures.Select(m => m.Field is null ? null : columns.Find(m.Field))
            .FirstOrDefault(c => c is { Kind: DatasetColumnKind.Money, CurrencyField: not null })?.CurrencyField;

    private static List<ReportSummaryRow> Accumulate(
        ReportSpec spec, ReportColumnSet columns, List<RecordData> rows, IReadOnlyList<ReportGroup> groups, string? currencyField,
        TimeZoneInfo zone, int[] excluded)
    {
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        var order = new List<Bucket>();
        var measureColumns = spec.Measures.Select(m => m.Field is null ? null : columns.Find(m.Field)).ToList();
        foreach (var row in rows)
        {
            var keys = groups.Select(g => GroupKey(columns.Find(g.Field)!, g, row.Values, zone)).ToArray();
            var currency = currencyField is null ? null : CurrencyOf(row.Values, currencyField);
            var id = string.Join('\u001f', keys.Select(k => k ?? "\u0000")) + '\u001e' + (currency ?? "\u0000");
            if (!buckets.TryGetValue(id, out var bucket))
            {
                bucket = new Bucket(keys, currency, spec.Measures.Count);
                buckets[id] = bucket;
                order.Add(bucket);
            }

            bucket.Rows++;
            for (var i = 0; i < spec.Measures.Count; i++)
            {
                var measure = spec.Measures[i];
                if (measure.Field is null)
                {
                    continue;
                }

                var column = measureColumns[i]!;
                var value = Number(Raw(row.Values, measure.Field));
                if (value is null || (column.Kind == DatasetColumnKind.Money && currency is null))
                {
                    excluded[i]++;
                    continue;
                }

                bucket.Add(i, value.Value);
            }
        }

        // Hiç kayıt yoksa ve gruplama da yoksa: tek bir boş toplam satırı (sıfır adet) gösterilir; para birimi bilinmediği için tutar yok.
        if (order.Count == 0 && groups.Count == 0)
        {
            order.Add(new Bucket([], null, spec.Measures.Count));
        }

        return [.. order.Select(b => new ReportSummaryRow(b.Keys, b.Currency, [.. spec.Measures.Select((m, i) => b.Result(i, m.Aggregation))], b.Rows))];
    }

    private static List<ReportSummaryRow> SortSummary(ReportSpec spec, List<ReportSummaryRow> rows, ReportColumnSet columns)
    {
        var direction = spec.Sort?.Direction ?? SortDirection.Ascending;
        var groupIndex = spec.Sort is { } sort ? spec.Groups.Select((g, i) => (g, i)).Where(x => x.g.Field == sort.Field).Select(x => x.i).DefaultIfEmpty(-1).First() : 0;
        var measureIndex = spec.Sort is { } s2 ? spec.Measures.Select((m, i) => (m, i)).Where(x => ReportSpecValidator.MeasureId(x.m) == s2.Field).Select(x => x.i).DefaultIfEmpty(-1).First() : -1;

        IOrderedEnumerable<ReportSummaryRow> ordered;
        if (measureIndex >= 0)
        {
            ordered = direction == SortDirection.Descending
                ? rows.OrderByDescending(r => r.Values[measureIndex] ?? decimal.MinValue)
                : rows.OrderBy(r => r.Values[measureIndex] ?? decimal.MaxValue);
        }
        else if (groupIndex >= 0 && spec.Groups.Count > 0)
        {
            ordered = direction == SortDirection.Descending
                ? rows.OrderBy(r => r.Keys[groupIndex] is null).ThenByDescending(r => r.Keys[groupIndex], StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Keys[groupIndex] is null).ThenBy(r => r.Keys[groupIndex], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            return rows;
        }

        // Eşitlikte kararlı sıra: diğer grup anahtarları, sonra para birimi.
        return [.. ordered.ThenBy(r => string.Join('\u001f', r.Keys), StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Currency, StringComparer.Ordinal)];
    }

    private static List<RecordData> Sorted(ReportSpec spec, ReportColumnSet columns, List<RecordData> rows, TimeZoneInfo zone)
    {
        if (spec.IsSummary || spec.Sort is not { } sort || columns.Find(sort.Field) is not { } column)
        {
            return rows;
        }

        // Sıralama anahtarı sütun türüne göre tiplenir; değeri olmayanlar her yönde sonda kalır.
        var keyed = rows.Select(r => (Row: r, Key: SortKey(column, r.Values, zone))).ToList();
        var present = keyed.Where(k => k.Key is not null);
        var missing = keyed.Where(k => k.Key is null);
        var ordered = sort.Direction == SortDirection.Descending
            ? present.OrderByDescending(k => k.Key, SortKeyComparer.Instance)
            : present.OrderBy(k => k.Key, SortKeyComparer.Instance);
        return [.. ordered.Concat(missing).Select(k => k.Row)];
    }

    private static IComparable? SortKey(DatasetColumn column, JsonObject values, TimeZoneInfo zone) => column.Kind switch
    {
        DatasetColumnKind.Number or DatasetColumnKind.Money => Number(Raw(values, column.Field)),
        DatasetColumnKind.Date or DatasetColumnKind.DateTime => Date(Raw(values, column.Field), column.Kind, zone),
        DatasetColumnKind.Flag => Flag(Raw(values, column.Field)),
        _ => Text(Raw(values, column.Field)),
    };

    private sealed class SortKeyComparer : IComparer<IComparable?>
    {
        public static readonly SortKeyComparer Instance = new();

        public int Compare(IComparable? x, IComparable? y) =>
            x is string a && y is string b ? string.Compare(a, b, StringComparison.OrdinalIgnoreCase) : x?.CompareTo(y) ?? 0;
    }

    // ---- Değer okuma

    public static JsonNode? Raw(JsonObject values, string field) =>
        RecordFieldNames.IsCustom(field)
            ? (values[RecordFields.Custom] as JsonObject)?[RecordFieldNames.CustomKey(field)]
            : values[field];

    public static string? Text(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var text = node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node.ToJsonString().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static decimal? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<decimal>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<double>(out var f) && double.IsFinite(f))
        {
            return (decimal)f;
        }

        return value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public static bool? Flag(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>Tarih sütunu "yyyy-MM-dd"; zaman damgası firmanın saat diliminde yerel güne çevrilir (UTC günüyle karıştırılmaz).</summary>
    public static DateOnly? Date(JsonNode? node, DatasetColumnKind kind, TimeZoneInfo zone)
    {
        var text = Text(node);
        if (text is null)
        {
            return null;
        }

        if (kind == DatasetColumnKind.DateTime)
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime)
                : null;
        }

        return DateOnly.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private static string? CurrencyOf(JsonObject values, string field) => Text(Raw(values, field))?.Trim().ToUpperInvariant();

    // ---- Süzgeç ve grup

    public static bool Matches(ReportFilter filter, DatasetColumn column, JsonObject values, TimeZoneInfo zone)
    {
        var node = Raw(values, column.Field);
        switch (column.Kind)
        {
            case DatasetColumnKind.Number or DatasetColumnKind.Money:
            {
                var n = Number(node);
                return filter.Operator switch
                {
                    FilterOperator.IsEmpty => n is null,
                    FilterOperator.NotEmpty => n is not null,
                    FilterOperator.NotEquals => n is null || n != ParseNumber(filter.Value),
                    _ => n is { } v && Compare(v.CompareTo(ParseNumber(filter.Value)), filter.Operator),
                };
            }

            case DatasetColumnKind.Date or DatasetColumnKind.DateTime:
            {
                var d = Date(node, column.Kind, zone);
                return filter.Operator switch
                {
                    FilterOperator.IsEmpty => d is null,
                    FilterOperator.NotEmpty => d is not null,
                    FilterOperator.NotEquals => d is null || d != ParseDay(filter.Value),
                    _ => d is { } v && Compare(v.CompareTo(ParseDay(filter.Value)), filter.Operator),
                };
            }

            case DatasetColumnKind.Flag:
            {
                var f = Flag(node);
                var wanted = bool.Parse(filter.Value!);
                return filter.Operator == FilterOperator.Equals ? f == wanted : f != wanted;
            }

            default:
            {
                var s = Text(node);
                return filter.Operator switch
                {
                    FilterOperator.IsEmpty => s is null,
                    FilterOperator.NotEmpty => s is not null,
                    FilterOperator.Equals => string.Equals(s, filter.Value, StringComparison.OrdinalIgnoreCase),
                    FilterOperator.NotEquals => !string.Equals(s, filter.Value, StringComparison.OrdinalIgnoreCase),
                    FilterOperator.Contains => s is not null && filter.Value is not null && s.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
                    FilterOperator.In => s is not null && (filter.Values ?? []).Any(v => string.Equals(s, v, StringComparison.OrdinalIgnoreCase)),
                    _ => false,
                };
            }
        }
    }

    private static bool Compare(int comparison, FilterOperator op) => op switch
    {
        FilterOperator.Equals => comparison == 0,
        FilterOperator.Greater => comparison > 0,
        FilterOperator.GreaterOrEqual => comparison >= 0,
        FilterOperator.Less => comparison < 0,
        FilterOperator.LessOrEqual => comparison <= 0,
        _ => false,
    };

    private static decimal ParseNumber(string? value) => decimal.Parse(value!, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string? value) => DateOnly.ParseExact(value!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? GroupKey(DatasetColumn column, ReportGroup group, JsonObject values, TimeZoneInfo zone)
    {
        var node = Raw(values, column.Field);
        switch (column.Kind)
        {
            case DatasetColumnKind.Date or DatasetColumnKind.DateTime:
                return Date(node, column.Kind, zone) is { } day
                    ? (group.Grain ?? DateGrain.Month) switch
                    {
                        DateGrain.Day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        DateGrain.Year => day.ToString("yyyy", CultureInfo.InvariantCulture),
                        _ => day.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    }
                    : null;
            case DatasetColumnKind.Flag:
                return Flag(node) is { } flag ? (flag ? "true" : "false") : null;
            case DatasetColumnKind.Number or DatasetColumnKind.Money:
                return Number(node)?.ToString(CultureInfo.InvariantCulture);
            default:
                return Text(node);
        }
    }

    private static bool InDrill(ReportSpec spec, ReportColumnSet columns, ReportDrill drill, string? currencyField, JsonObject values, TimeZoneInfo zone)
    {
        for (var i = 0; i < spec.Groups.Count; i++)
        {
            var wanted = i < drill.Keys.Count ? drill.Keys[i] : null;
            var actual = GroupKey(columns.Find(spec.Groups[i].Field)!, spec.Groups[i], values, zone);
            if (!string.Equals(actual, string.IsNullOrEmpty(wanted) ? null : wanted, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return currencyField is null
            || string.Equals(CurrencyOf(values, currencyField), string.IsNullOrEmpty(drill.Currency) ? null : drill.Currency, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Bucket(string?[] keys, string? currency, int measureCount)
    {
        private readonly decimal[] sum = new decimal[measureCount];
        private readonly int[] count = new int[measureCount];
        private readonly decimal?[] min = new decimal?[measureCount];
        private readonly decimal?[] max = new decimal?[measureCount];

        public string?[] Keys { get; } = keys;

        public string? Currency { get; } = currency;

        public int Rows { get; set; }

        public void Add(int index, decimal value)
        {
            sum[index] += value;
            count[index]++;
            min[index] = min[index] is { } lo && lo <= value ? lo : value;
            max[index] = max[index] is { } hi && hi >= value ? hi : value;
        }

        public decimal? Result(int index, Aggregation aggregation) => aggregation switch
        {
            Aggregation.Count => Rows,
            Aggregation.Sum => count[index] == 0 ? null : sum[index],
            Aggregation.Average => count[index] == 0 ? null : sum[index] / count[index],
            Aggregation.Min => min[index],
            _ => max[index],
        };
    }
}
