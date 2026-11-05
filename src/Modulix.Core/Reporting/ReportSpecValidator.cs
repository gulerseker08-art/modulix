using System.Globalization;
using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>
/// Rapor tanımının doğrulaması ve normalleştirmesi (saf). Tanım yalnızca veri kümesinin bildirilmiş, çağıranın görebildiği sütunlarına
/// başvurabilir; hesaplanan alan, serbest ifade ya da birleştirme yoktur, bu yüzden döngü ya da sınırsız işlem üretilemez.
/// Her sorun için ne yapılacağı söylenir; yetkisiz (hassas) sütun ile kaldırılmış sütun birbirinden ayrılır.
/// </summary>
public static class ReportSpecValidator
{
    /// <summary>Ölçü kimliği: sıralama ve sonuç sütunu anahtarı ("count", "sum:amount").</summary>
    public static string MeasureId(ReportMeasure measure) =>
        measure.Aggregation == Aggregation.Count && measure.Field is null
            ? "count"
            : $"{measure.Aggregation.ToString().ToLowerInvariant()}:{measure.Field}";

    /// <summary>
    /// Eksikleri tamamlar: tablo raporunda sütun yoksa varsayılan sütunlar; gruplama var ölçü yoksa kayıt sayısı; tarih gruplamasında
    /// ayrıntı yoksa ay. Doğrulamadan önce çağrılır.
    /// </summary>
    public static ReportSpec Normalize(ReportSpec spec, ReportColumnSet columns)
    {
        var groups = spec.Groups.Select(g =>
            columns.Find(g.Field) is { Kind: DatasetColumnKind.Date or DatasetColumnKind.DateTime } && g.Grain is null
                ? g with { Grain = DateGrain.Month }
                : g).ToList();
        var measures = spec.Measures.ToList();
        var summary = groups.Count > 0 || measures.Count > 0;
        if (groups.Count > 0 && measures.Count == 0)
        {
            measures.Add(new ReportMeasure(null, Aggregation.Count));
        }

        var shown = spec.Columns.ToList();
        if (!summary && shown.Count == 0)
        {
            shown = [.. columns.Dataset.DefaultColumns.Where(c => columns.Find(c) is { } column && column.Use.HasFlag(ColumnUse.Show))];
        }

        return spec with { Groups = groups, Measures = measures, Columns = shown };
    }

    public static IReadOnlyList<ReportIssue> Validate(ReportSpec spec, ReportColumnSet columns)
    {
        var issues = new List<ReportIssue>();
        if (spec.Dataset != columns.Dataset.Key)
        {
            issues.Add(new ReportIssue(ReportIssueCode.UnknownDataset, null, "Rapor tanımı bu veri kümesine ait değil."));
            return issues;
        }

        if (!Enum.IsDefined(spec.Chart))
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidChart, null, "Grafik türü geçersiz."));
        }

        var summary = spec.IsSummary;
        if (!summary)
        {
            if (spec.Columns.Count == 0)
            {
                issues.Add(new ReportIssue(ReportIssueCode.TooMany, null, "En az bir sütun seçin."));
            }

            if (spec.Columns.Count > ReportLimits.MaxColumns)
            {
                issues.Add(new ReportIssue(ReportIssueCode.TooMany, null, $"En fazla {ReportLimits.MaxColumns} sütun seçilebilir."));
            }

            foreach (var field in spec.Columns.Distinct())
            {
                RequireUse(issues, columns, field, ColumnUse.Show, "tabloda gösterilemez");
            }
        }

        if (spec.Filters.Count > ReportLimits.MaxFilters)
        {
            issues.Add(new ReportIssue(ReportIssueCode.TooMany, null, $"En fazla {ReportLimits.MaxFilters} süzgeç kullanılabilir."));
        }

        foreach (var filter in spec.Filters)
        {
            ValidateFilter(issues, columns, filter);
        }

        ValidateGroups(issues, columns, spec);
        ValidateMeasures(issues, columns, spec);
        ValidateSort(issues, columns, spec, summary);
        ValidateChart(issues, columns, spec);
        return issues;
    }

    private static void ValidateFilter(List<ReportIssue> issues, ReportColumnSet columns, ReportFilter filter)
    {
        if (!RequireUse(issues, columns, filter.Field, ColumnUse.Filter, "süzgeçte kullanılamaz") || columns.Find(filter.Field) is not { } column)
        {
            return;
        }

        if (!Enum.IsDefined(filter.Operator) || !Allowed(column.Kind).Contains(filter.Operator))
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidOperator, filter.Field, $"'{column.Label}' için bu süzgeç işleci kullanılamaz."));
            return;
        }

        switch (filter.Operator)
        {
            case FilterOperator.IsEmpty or FilterOperator.NotEmpty:
                return;
            case FilterOperator.In:
                if (filter.Values is not { Count: > 0 } values || values.Count > ReportLimits.MaxInValues || values.Any(v => !ValidValue(column, v)))
                {
                    issues.Add(new ReportIssue(ReportIssueCode.InvalidValue, filter.Field, $"'{column.Label}' için değer listesi geçersiz (1-{ReportLimits.MaxInValues} değer)."));
                }

                return;
            default:
                if (!ValidValue(column, filter.Value))
                {
                    issues.Add(new ReportIssue(ReportIssueCode.InvalidValue, filter.Field, $"'{column.Label}' için değer geçersiz."));
                }

                return;
        }
    }

    private static void ValidateGroups(List<ReportIssue> issues, ReportColumnSet columns, ReportSpec spec)
    {
        if (spec.Groups.Count > ReportLimits.MaxGroups)
        {
            issues.Add(new ReportIssue(ReportIssueCode.TooMany, null, $"En fazla {ReportLimits.MaxGroups} gruplama sütunu kullanılabilir."));
        }

        if (spec.Groups.Select(g => g.Field).Distinct().Count() != spec.Groups.Count)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidGrouping, null, "Aynı sütun iki kez gruplanamaz."));
        }

        foreach (var group in spec.Groups)
        {
            if (!RequireUse(issues, columns, group.Field, ColumnUse.Group, "gruplanamaz") || columns.Find(group.Field) is not { } column)
            {
                continue;
            }

            var isDate = column.Kind is DatasetColumnKind.Date or DatasetColumnKind.DateTime;
            if (group.Grain is { } grain && (!isDate || !Enum.IsDefined(grain)))
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidGrouping, group.Field, $"'{column.Label}' için tarih ayrıntısı kullanılamaz."));
            }
        }
    }

    private static void ValidateMeasures(List<ReportIssue> issues, ReportColumnSet columns, ReportSpec spec)
    {
        if (spec.Measures.Count > ReportLimits.MaxMeasures)
        {
            issues.Add(new ReportIssue(ReportIssueCode.TooMany, null, $"En fazla {ReportLimits.MaxMeasures} ölçü kullanılabilir."));
        }

        if (spec.Measures.Select(MeasureId).Distinct().Count() != spec.Measures.Count)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, null, "Aynı ölçü iki kez eklenemez."));
        }

        var currencyFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var measure in spec.Measures)
        {
            if (!Enum.IsDefined(measure.Aggregation))
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, measure.Field, "Toplama türü geçersiz."));
                continue;
            }

            if (measure.Aggregation == Aggregation.Count && measure.Field is null)
            {
                continue;
            }

            if (measure.Field is null)
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, null, "Bu toplama için sütun seçin."));
                continue;
            }

            if (!RequireUse(issues, columns, measure.Field, ColumnUse.Measure, "toplanamaz") || columns.Find(measure.Field) is not { } column)
            {
                continue;
            }

            if (column.Kind is not (DatasetColumnKind.Number or DatasetColumnKind.Money))
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, measure.Field, $"'{column.Label}' sayısal değil; yalnızca sayılabilir."));
            }
            else if (measure.Aggregation == Aggregation.Count)
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, measure.Field, "Kayıt sayısı için sütun seçmeyin."));
            }
            else if (column is { Kind: DatasetColumnKind.Money, CurrencyField: { } currency })
            {
                currencyFields.Add(currency);
            }
            else if (column.Kind == DatasetColumnKind.Money)
            {
                issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, measure.Field, $"'{column.Label}' için para birimi tanımlı değil."));
            }
        }

        // Para birimleri hiçbir zaman birbirine toplanmaz; sonuç para birimine göre ayrılır. Ölçüler aynı para birimi alanını paylaşmalı.
        if (currencyFields.Count > 1)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidMeasure, null, "Farklı para birimi alanlarına bağlı tutarlar aynı raporda toplanamaz."));
        }
    }

    private static void ValidateSort(List<ReportIssue> issues, ReportColumnSet columns, ReportSpec spec, bool summary)
    {
        if (spec.Sort is not { } sort)
        {
            return;
        }

        var valid = Enum.IsDefined(sort.Direction) && (summary
            ? spec.Groups.Any(g => g.Field == sort.Field) || spec.Measures.Any(m => MeasureId(m) == sort.Field)
            : spec.Columns.Contains(sort.Field) && columns.Find(sort.Field) is not null);
        if (!valid)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidSort, sort.Field, "Sıralama sütunu raporda yok."));
        }
    }

    private static void ValidateChart(List<ReportIssue> issues, ReportColumnSet columns, ReportSpec spec)
    {
        if (spec.Chart == ChartKind.None)
        {
            return;
        }

        if (spec.Groups.Count == 0 || spec.Measures.Count == 0)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidChart, null, "Grafik için gruplama ve en az bir ölçü gerekir."));
            return;
        }

        if (spec.Chart == ChartKind.Pie && (spec.Groups.Count != 1 || spec.Measures.Count != 1))
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidChart, null, "Pasta grafik tek gruplama ve tek ölçü ister."));
        }

        if (spec.Chart == ChartKind.Line && columns.Find(spec.Groups[0].Field) is not { Kind: DatasetColumnKind.Date or DatasetColumnKind.DateTime })
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidChart, null, "Çizgi grafik ilk gruplama olarak bir tarih sütunu ister."));
        }
    }

    /// <summary>Sütunun kullanıma açık olduğunu doğrular; yoksa nedenini (yetki mi, kaldırılmış mı, bilinmiyor mu) söyler.</summary>
    private static bool RequireUse(List<ReportIssue> issues, ReportColumnSet columns, string field, ColumnUse use, string denied)
    {
        if (columns.IsHidden(field))
        {
            issues.Add(new ReportIssue(ReportIssueCode.NotPermitted, field, $"'{LabelOf(columns, field)}' sütunu için yetkiniz yok."));
            return false;
        }

        if (columns.IsRemoved(field))
        {
            issues.Add(new ReportIssue(ReportIssueCode.ColumnRemoved, field, $"'{field}' özel alanı artık kullanılamıyor (arşivlendi). Raporu düzenleyip sütunu çıkarın."));
            return false;
        }

        if (columns.Find(field) is not { } column)
        {
            issues.Add(new ReportIssue(ReportIssueCode.UnknownColumn, field, $"'{field}' sütunu veri kümesinde yok."));
            return false;
        }

        if (!column.Use.HasFlag(use))
        {
            issues.Add(new ReportIssue(ReportIssueCode.UnknownColumn, field, $"'{column.Label}' {denied}."));
            return false;
        }

        return true;
    }

    private static string LabelOf(ReportColumnSet columns, string field) =>
        columns.Dataset.Columns.FirstOrDefault(c => c.Field == field)?.Label ?? field;

    private static IReadOnlySet<FilterOperator> Allowed(DatasetColumnKind kind) => kind switch
    {
        DatasetColumnKind.Text => TextOperators,
        DatasetColumnKind.Number or DatasetColumnKind.Money or DatasetColumnKind.Date or DatasetColumnKind.DateTime => OrderedOperators,
        DatasetColumnKind.Flag => FlagOperators,
        _ => ChoiceOperators,
    };

    private static readonly HashSet<FilterOperator> TextOperators =
        [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Contains, FilterOperator.In, FilterOperator.IsEmpty, FilterOperator.NotEmpty];

    private static readonly HashSet<FilterOperator> OrderedOperators =
    [
        FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.Greater, FilterOperator.GreaterOrEqual, FilterOperator.Less, FilterOperator.LessOrEqual,
        FilterOperator.IsEmpty, FilterOperator.NotEmpty,
    ];

    private static readonly HashSet<FilterOperator> FlagOperators = [FilterOperator.Equals, FilterOperator.NotEquals];

    private static readonly HashSet<FilterOperator> ChoiceOperators =
        [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In, FilterOperator.IsEmpty, FilterOperator.NotEmpty];

    /// <summary>Süzgeç değerinin sütun türüne göre geçerli olup olmadığı (uzunluk sınırı dahil).</summary>
    public static bool ValidValue(DatasetColumn column, string? value)
    {
        if (value is not { Length: > 0 and <= ReportLimits.MaxValueLength })
        {
            return false;
        }

        return column.Kind switch
        {
            DatasetColumnKind.Number or DatasetColumnKind.Money => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            DatasetColumnKind.Date or DatasetColumnKind.DateTime => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            DatasetColumnKind.Flag => bool.TryParse(value, out _),
            DatasetColumnKind.Lookup or DatasetColumnKind.User => Guid.TryParse(value, out _),
            _ => true,
        };
    }
}
