using System.Globalization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Lookups;

namespace Modulix.Core.Reporting;

public interface IReportRunner
{
    /// <summary>
    /// Raporu çağıranın yetkisiyle çalıştırır. Hata durumları (veri kümesi yok, modül kapalı, yetki yok, geçersiz tanım) istisna değil,
    /// <see cref="ReportResult.Status"/> ve <see cref="ReportResult.Issues"/> ile döner; bu durumlarda veri okunmaz.
    /// </summary>
    Task<ReportResult> RunAsync(ReportRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Rapor çalıştırıcı (ADR-0035, dilim 3). Kayıtlar yalnızca <see cref="IRecordPipeline"/> üzerinden okunur: izin, veri kapsamı, şirket kapsamı
/// ve hassas alan kuralları aggregate'ten önce uygulanır; çağıranın göremediği sütun hiç hesaba girmez. Rapor tanımı (ve paylaşımı) yetki
/// kazandırmaz. Okuma en fazla <see cref="ReportLimits.MaxRowsRead"/> kayıtla sınırlıdır; aşılırsa sonuç eksik olabilir ve öyle işaretlenir.
/// </summary>
public sealed class ReportRunner(
    ReportColumnResolver resolver,
    IRecordPipeline pipeline,
    IChoiceLists choiceLists,
    ILookupSearch lookups,
    ITenantMembers members,
    ITenantClock clock) : IReportRunner
{
    public async Task<ReportResult> RunAsync(ReportRunRequest request, CancellationToken cancellationToken)
    {
        var day = await clock.GetAsync(cancellationToken);
        var now = day.UtcNow;
        if (await resolver.ResolveAsync(request.Spec.Dataset, cancellationToken) is not { } resolved)
        {
            return ReportResult.Failed(ReportRunStatus.UnknownDataset,
                [new ReportIssue(ReportIssueCode.UnknownDataset, null, "Rapor veri kümesi bulunamadı.")], now);
        }

        var dataset = resolved.Dataset;
        if (resolved.Columns is not { } columns)
        {
            return ReportResult.Failed(resolved.Access, resolved.Issues, now, dataset.DisplayName, dataset.Version);
        }

        var spec = ReportSpecValidator.Normalize(request.Spec, columns);
        var issues = ReportSpecValidator.Validate(spec, columns).ToList();
        if (request.Drill is not null && !spec.IsSummary)
        {
            issues.Add(new ReportIssue(ReportIssueCode.InvalidValue, null, "Ayrıntı yalnızca özet raporlarda kullanılır."));
        }

        if (issues.Count > 0)
        {
            // Yetkisiz sütun ile geçersiz tanım ayrı durumlardır: ilki "yetkiniz yok", ikincisi "tanım düzeltilmeli".
            var forbidden = issues.All(i => i.Code == ReportIssueCode.NotPermitted);
            return ReportResult.Failed(forbidden ? ReportRunStatus.Forbidden : ReportRunStatus.Invalid, issues, now, dataset.DisplayName, dataset.Version);
        }

        var read = await ReadAsync(dataset, columns, spec, cancellationToken);
        if (read.Error is { } error)
        {
            var status = error.Code is RecordErrorCode.PermissionDenied or RecordErrorCode.FeatureNotLicensed or RecordErrorCode.NoActiveTenant
                ? ReportRunStatus.Forbidden
                : ReportRunStatus.Invalid;
            return ReportResult.Failed(status, [new ReportIssue(ReportIssueCode.NoAccess, null, error.Message)], now, dataset.DisplayName, dataset.Version);
        }

        var computation = ReportAggregator.Compute(spec, columns, read.Rows, request.Drill, day.Zone);
        var result = await BuildAsync(request, spec, columns, dataset, computation, read, day, cancellationToken);
        return result with { Issues = [.. result.Issues, .. DriftIssues(request, dataset)] };
    }

    private static IEnumerable<ReportIssue> DriftIssues(ReportRunRequest request, DatasetDefinition dataset)
    {
        if (request.StoredDatasetVersion is { } stored && stored < dataset.Version)
        {
            yield return new ReportIssue(ReportIssueCode.VersionDrift, null,
                $"Rapor, '{dataset.DisplayName}' veri kümesinin eski sürümüyle (v{stored}) kaydedilmiş; güncel sürüm v{dataset.Version}. Sonuçları gözden geçirin.", false);
        }
    }

    // ---- Okuma

    private sealed record ReadResult(List<RecordData> Rows, bool Truncated, RecordError? Error);

    private async Task<ReadResult> ReadAsync(DatasetDefinition dataset, ReportColumnSet columns, ReportSpec spec, CancellationToken cancellationToken)
    {
        var pushdown = StoreFilters(columns, spec);
        var result = await ReadPagesAsync(dataset.EntityKey, pushdown, cancellationToken);

        // Depo bir değeri (ör. eski seçim anahtarı) reddederse süzgeçsiz oku: süzgeç bellekte zaten uygulanır, sonuç değişmez.
        if (result.Error is { Code: RecordErrorCode.ValidationFailed } && pushdown.Count > 0)
        {
            result = await ReadPagesAsync(dataset.EntityKey, new Dictionary<string, string>(), cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Depo süzgeçleri: yalnızca okuma satır sınırına takılmamak için daraltır. Süzgeç her durumda bellekte de uygulandığından
    /// (bkz. <see cref="ReportAggregator.Compute"/>) buradaki eksiklik ya da fazlalık sonucu değiştirmez.
    /// </summary>
    private static Dictionary<string, string> StoreFilters(ReportColumnSet columns, ReportSpec spec)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filter in spec.Filters)
        {
            if (columns.Find(filter.Field) is not { } column || filter.Value is null)
            {
                continue;
            }

            var name = filter.Operator switch
            {
                FilterOperator.Equals => column.StoreFilter,
                FilterOperator.Greater or FilterOperator.GreaterOrEqual => column.StoreFilterFrom,
                FilterOperator.Less or FilterOperator.LessOrEqual => column.StoreFilterTo,
                _ => null,
            };
            if (name is not null && filters.Count < RecordQuery.MaxFilters)
            {
                filters.TryAdd(name, filter.Value);
            }
        }

        return filters;
    }

    private async Task<ReadResult> ReadPagesAsync(string entityKey, IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var rows = new List<RecordData>();
        for (var page = 1; rows.Count < ReportLimits.MaxRowsRead; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(entityKey, page, RecordQuery.MaxBulkPageSize, null, filters) { Bulk = true }, cancellationToken);
            if (!result.IsSuccess)
            {
                return new ReadResult([], false, result.Error);
            }

            rows.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || rows.Count >= result.Value.TotalCount)
            {
                return new ReadResult(rows, false, null);
            }
        }

        return new ReadResult(rows, true, null);
    }

    // ---- Sonuç

    private async Task<ReportResult> BuildAsync(
        ReportRunRequest request, ReportSpec spec, ReportColumnSet columns, DatasetDefinition dataset, ReportComputation computation,
        ReadResult read, TenantDay day, CancellationToken cancellationToken)
    {
        var issues = new List<ReportIssue>();
        if (read.Truncated)
        {
            issues.Add(new ReportIssue(ReportIssueCode.Truncated, null,
                $"Okuma sınırı ({ReportLimits.MaxRowsRead} kayıt) aşıldı; sonuç eksik olabilir. Süzgeçlerle daraltın.", false));
        }

        for (var i = 0; i < spec.Measures.Count; i++)
        {
            if (computation.ExcludedPerMeasure[i] > 0)
            {
                issues.Add(new ReportIssue(ReportIssueCode.ExcludedRows, spec.Measures[i].Field,
                    $"{computation.ExcludedPerMeasure[i]} kayıtta '{Label(columns, spec.Measures[i])}' değeri ya da para birimi yok; toplamlara girmedi.", false));
            }
        }

        var labeler = new Labeler(choiceLists, lookups, members);
        if (spec.IsSummary && request.Drill is null)
        {
            return await SummaryAsync(spec, columns, dataset, computation, read, day, issues, labeler, cancellationToken);
        }

        return await TableAsync(request, spec, columns, dataset, computation, read, day, issues, labeler, cancellationToken);
    }

    private async Task<ReportResult> SummaryAsync(
        ReportSpec spec, ReportColumnSet columns, DatasetDefinition dataset, ReportComputation computation, ReadResult read, TenantDay day,
        List<ReportIssue> issues, Labeler labeler, CancellationToken cancellationToken)
    {
        var hasCurrency = ReportAggregator.MoneyCurrencyField(spec, columns) is not null;
        var resultColumns = new List<ReportResultColumn>();
        foreach (var group in spec.Groups)
        {
            var column = columns.Find(group.Field)!;
            resultColumns.Add(new ReportResultColumn(group.Field, column.Label, column.Kind, ReportColumnRole.Group));
        }

        if (hasCurrency)
        {
            resultColumns.Add(new ReportResultColumn("currency", "Para birimi", DatasetColumnKind.Text, ReportColumnRole.Currency));
        }

        for (var i = 0; i < spec.Measures.Count; i++)
        {
            var measure = spec.Measures[i];
            resultColumns.Add(new ReportResultColumn(
                ReportSpecValidator.MeasureId(measure), Label(columns, measure), MeasureKind(columns, measure), ReportColumnRole.Measure,
                computation.ExcludedPerMeasure[i]));
        }

        // Etiketler satırlardan önce toplu çözülür (lookup/seçim); kayıt başına çağrı yapılmaz.
        var labels = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        for (var g = 0; g < spec.Groups.Count; g++)
        {
            var keys = computation.Summary.Select(r => r.Keys[g]).Where(k => k is not null).Select(k => k!).Distinct();
            labels[g] = await labeler.ResolveAsync(columns.Find(spec.Groups[g].Field)!, keys, cancellationToken);
        }

        ReportResultRow ToRow(ReportSummaryRow row)
        {
            var cells = new List<object?>();
            for (var g = 0; g < spec.Groups.Count; g++)
            {
                cells.Add(row.Keys[g] is { } key ? labels[g].GetValueOrDefault(key, key) : null);
            }

            if (hasCurrency)
            {
                cells.Add(row.Currency);
            }

            cells.AddRange(row.Values.Select(v => (object?)v));
            return new ReportResultRow(cells, row.Keys, row.Currency);
        }

        return new ReportResult(
            ReportRunStatus.Ok, true, resultColumns, [.. computation.Summary.Select(ToRow)], [.. computation.Totals.Select(ToTotalRow(spec, hasCurrency))],
            read.Rows.Count, computation.Matched.Count, read.Truncated, computation.SummaryLimited, 1, computation.Summary.Count, day.UtcNow, dataset.Version,
            computation.SummaryLimited
                ? [.. issues, new ReportIssue(ReportIssueCode.Truncated, null, $"İlk {ReportLimits.MaxSummaryRows} grup gösteriliyor; süzgeçlerle daraltın.", false)]
                : issues,
            dataset.DisplayName, dataset.RecordPath, spec.Chart);
    }

    private static Func<ReportSummaryRow, ReportResultRow> ToTotalRow(ReportSpec spec, bool hasCurrency) => row =>
    {
        var cells = new List<object?>();
        cells.AddRange(spec.Groups.Select(_ => (object?)null));
        if (hasCurrency)
        {
            cells.Add(row.Currency);
        }

        cells.AddRange(row.Values.Select(v => (object?)v));
        return new ReportResultRow(cells, [], row.Currency);
    };

    private async Task<ReportResult> TableAsync(
        ReportRunRequest request, ReportSpec spec, ReportColumnSet columns, DatasetDefinition dataset, ReportComputation computation,
        ReadResult read, TenantDay day, List<ReportIssue> issues, Labeler labeler, CancellationToken cancellationToken)
    {
        var shown = (spec.IsSummary ? [.. dataset.DefaultColumns] : spec.Columns)
            .Select(columns.Find).Where(c => c is not null).Select(c => c!).Take(ReportLimits.MaxColumns).ToList();
        var pageSize = request.ForExport ? ReportLimits.MaxRowsRead : Math.Clamp(request.PageSize, 1, ReportLimits.MaxPageSize);
        var page = request.ForExport ? 1 : Math.Clamp(request.Page, 1, 100_000);
        var slice = computation.Matched.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var labelMap = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var column in shown.Where(c => c.Kind is DatasetColumnKind.Choice or DatasetColumnKind.Lookup or DatasetColumnKind.User))
        {
            var keys = slice.Select(r => ReportAggregator.Text(ReportAggregator.Raw(r.Values, column.Field))).Where(k => k is not null).Select(k => k!).Distinct();
            labelMap[column.Field] = await labeler.ResolveAsync(column, keys, cancellationToken);
        }

        var moneyCurrency = shown.FirstOrDefault(c => c is { Kind: DatasetColumnKind.Money, CurrencyField: not null })?.CurrencyField;
        var rows = slice.Select(record =>
        {
            var cells = shown.Select(column => Cell(column, record.Values, labelMap.GetValueOrDefault(column.Field), day.Zone)).ToList();
            var currency = moneyCurrency is null ? null : ReportAggregator.Text(ReportAggregator.Raw(record.Values, moneyCurrency))?.ToUpperInvariant();
            return new ReportResultRow(cells, [], currency, record.Id);
        }).ToList();

        var resultColumns = shown.Select(c => new ReportResultColumn(c.Field, c.Label, c.Kind, ReportColumnRole.Value)).ToList();
        var totals = spec.Measures.Count == 0 ? [] : TotalRows(spec, columns, computation);
        return new ReportResult(
            ReportRunStatus.Ok, false, resultColumns, rows, totals, read.Rows.Count, computation.Matched.Count, read.Truncated, false, page, pageSize, day.UtcNow,
            dataset.Version, issues, dataset.DisplayName, dataset.RecordPath, ChartKind.None);
    }

    /// <summary>Ayrıntıda gösterilen toplamlar: özet satırıyla aynı ölçüler, ayrıntıdaki kayıtların (süzgeç + grup) toplamı.</summary>
    private static List<ReportResultRow> TotalRows(ReportSpec spec, ReportColumnSet columns, ReportComputation computation)
    {
        var hasCurrency = ReportAggregator.MoneyCurrencyField(spec, columns) is not null;
        return [.. computation.Totals.Select(ToTotalRow(spec, hasCurrency))];
    }

    private static object? Cell(DatasetColumn column, System.Text.Json.Nodes.JsonObject values, IReadOnlyDictionary<string, string>? labels, TimeZoneInfo zone)
    {
        var node = ReportAggregator.Raw(values, column.Field);
        switch (column.Kind)
        {
            case DatasetColumnKind.Number or DatasetColumnKind.Money:
                return ReportAggregator.Number(node);
            case DatasetColumnKind.Flag:
                return ReportAggregator.Flag(node);
            case DatasetColumnKind.Date:
                return ReportAggregator.Date(node, column.Kind, zone)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case DatasetColumnKind.DateTime:
                return DateTimeOffset.TryParse(ReportAggregator.Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                    ? TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : null;
            case DatasetColumnKind.Choice or DatasetColumnKind.Lookup or DatasetColumnKind.User:
            {
                var key = ReportAggregator.Text(node);
                return key is null ? null : labels?.GetValueOrDefault(key, key) ?? key;
            }

            default:
                return ReportAggregator.Text(node);
        }
    }

    private static string Label(ReportColumnSet columns, ReportMeasure measure)
    {
        var aggregation = measure.Aggregation switch
        {
            Aggregation.Count => "Kayıt sayısı",
            Aggregation.Sum => "Toplam",
            Aggregation.Average => "Ortalama",
            Aggregation.Min => "En düşük",
            _ => "En yüksek",
        };
        return measure.Field is null ? aggregation : $"{aggregation} {columns.Find(measure.Field)?.Label ?? measure.Field}";
    }

    private static DatasetColumnKind MeasureKind(ReportColumnSet columns, ReportMeasure measure) =>
        measure.Aggregation == Aggregation.Count || measure.Field is null ? DatasetColumnKind.Number : columns.Find(measure.Field)?.Kind ?? DatasetColumnKind.Number;

    /// <summary>Seçim ve lookup değerlerinin görünen etiketleri (çağıranın görebildiği ölçüde; erişilemeyen kayıt kimliğiyle kalır).</summary>
    private sealed class Labeler(IChoiceLists choiceLists, ILookupSearch lookups, ITenantMembers members)
    {
        public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(DatasetColumn column, IEnumerable<string> keys, CancellationToken cancellationToken)
        {
            var distinct = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (column.Kind)
            {
                case DatasetColumnKind.Choice when column.ChoiceList is { } listKey:
                {
                    var list = await choiceLists.GetAsync(listKey, cancellationToken);
                    foreach (var key in distinct)
                    {
                        labels[key] = list.Find(key)?.Label ?? key;
                    }

                    break;
                }

                case DatasetColumnKind.Choice:
                    foreach (var option in column.Options ?? [])
                    {
                        labels[option.Key] = option.Label;
                    }

                    break;
                case DatasetColumnKind.User:
                    foreach (var member in await members.ListActiveAsync(cancellationToken))
                    {
                        labels[member.UserId.ToString()] = member.DisplayName;
                    }

                    break;
                case DatasetColumnKind.Lookup when column.LookupKey is { } lookupKey:
                {
                    var ids = distinct.Select(k => Guid.TryParse(k, out var id) ? id : (Guid?)null).Where(i => i is not null).Select(i => i!.Value).ToList();
                    foreach (var chunk in ids.Chunk(RecordLookupLimits.MaxResolveIds))
                    {
                        var resolved = await lookups.ResolveAsync(lookupKey, chunk, cancellationToken);
                        if (!resolved.IsSuccess)
                        {
                            continue;
                        }

                        foreach (var item in resolved.Value!.Where(i => !i.Unavailable && i.Label is not null))
                        {
                            labels[item.Id.ToString()] = item.Label!;
                        }
                    }

                    break;
                }
            }

            return labels;
        }
    }
}
