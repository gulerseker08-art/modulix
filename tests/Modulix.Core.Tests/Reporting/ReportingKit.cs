using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Customization;
using Modulix.Core.Reporting;

namespace Modulix.Core.Tests.Reporting;

/// <summary>Rapor altyapısı testlerinin ortak veri kümesi ve kayıt üreticisi. Core sektöre özel bir şey bilmez: "sales.deal" yalnızca testtir.</summary>
internal static class ReportingKit
{
    public const string Entity = "sales.deal";
    public static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private const ColumnUse All = ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show;

    public static DatasetDefinition Dataset { get; } = new("sales.deals", "Anlaşmalar", Entity, 2,
    [
        new DatasetColumn("title", "Başlık", DatasetColumnKind.Text, ColumnUse.Filter | ColumnUse.Show),
        new DatasetColumn("stage", "Aşama", DatasetColumnKind.Choice, All) { Options = [new("new", "Yeni"), new("won", "Kazanıldı"), new("lost", "Kaybedildi")] },
        new DatasetColumn("amount", "Tutar", DatasetColumnKind.Money, ColumnUse.Measure | ColumnUse.Filter | ColumnUse.Show) { CurrencyField = "currency" },
        new DatasetColumn("currency", "Para birimi", DatasetColumnKind.Text, All),
        new DatasetColumn("score", "Puan", DatasetColumnKind.Number, ColumnUse.Measure | ColumnUse.Filter | ColumnUse.Show),
        new DatasetColumn("closeDate", "Kapanış", DatasetColumnKind.Date, All),
        new DatasetColumn("seenAt", "Görülme", DatasetColumnKind.DateTime, All),
        new DatasetColumn("vip", "Öncelikli", DatasetColumnKind.Flag, All),
        new DatasetColumn("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = "sales.deal.owner" },
    ])
    {
        AllowsCustomFields = true,
        DefaultColumns = ["title", "stage", "amount"],
    };

    public static ReportColumnSet Columns(IEnumerable<string>? hidden = null, params CustomFieldInfo[] customs) =>
        ReportColumnSet.Build(Dataset, (hidden ?? []).ToHashSet(StringComparer.Ordinal), customs);

    public static CustomFieldInfo Custom(string key, string label, CustomFieldType type, bool archived = false, params CustomFieldOption[] options) =>
        new(Guid.NewGuid(), Entity, key, label, type, false, false, 0, archived, options);

    public static ReportSpec Spec(
        IEnumerable<string>? columns = null, IEnumerable<ReportFilter>? filters = null, IEnumerable<ReportGroup>? groups = null,
        IEnumerable<ReportMeasure>? measures = null, ReportSort? sort = null, ChartKind chart = ChartKind.None) =>
        new(Dataset.Key, [.. columns ?? []], [.. filters ?? []], [.. groups ?? []], [.. measures ?? []], sort, chart);

    public static RecordData Deal(
        string stage = "new", decimal? amount = null, string? currency = "TRY", decimal? score = null, string? closeDate = null, string? seenAt = null,
        bool? vip = null, string title = "Fırsat", string? custom = null)
    {
        var values = new JsonObject { ["title"] = title, ["stage"] = stage, ["amount"] = amount, ["currency"] = currency, ["score"] = score, ["closeDate"] = closeDate, ["seenAt"] = seenAt, ["vip"] = vip };
        if (custom is not null)
        {
            values["custom"] = JsonNode.Parse(custom);
        }

        return new RecordData(Entity, Guid.NewGuid(), 1, values);
    }

    public static ReportComputation Compute(ReportSpec spec, IEnumerable<RecordData> rows, ReportDrill? drill = null, ReportColumnSet? columns = null)
    {
        columns ??= Columns();
        return ReportAggregator.Compute(ReportSpecValidator.Normalize(spec, columns), columns, rows, drill, Istanbul);
    }

    public static decimal? Sum(ReportSummaryRow row, int measureIndex = 0) => row.Values[measureIndex];
}
