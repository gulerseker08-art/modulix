using System.Globalization;
using Modulix.Core.Contracts.Records;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Lead raporu hesabı (tanımlar <see cref="LeadReportDto"/> üzerinde). Saf hesap: girdi, boru hattından kullanıcının kapsamıyla
/// okunmuş Lead'lerdir; bu yüzden rapor kapsamı aşamaz. Oranlar ve ortalamalar paydası sıfırsa <c>null</c> döner.
/// </summary>
internal static class LeadReport
{
    public static LeadReportDto Build(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        IReadOnlyList<RecordData> cohort,
        IReadOnlyList<RecordData> open,
        IReadOnlyList<RecordData> convertedInPeriod,
        int? targetHours,
        bool truncated)
    {
        var rows = cohort.Select(Row.From).ToList();
        var contacted = rows.Where(r => r.FirstContactAt is not null).ToList();
        var lags = contacted
            .Select(r => (r.FirstContactAt!.Value - r.ReceivedAt).TotalHours)
            .Where(h => h >= 0)
            .Order()
            .ToList();

        int? breaches = targetHours is { } target
            ? lags.Count(h => h > target) + rows.Count(r => r.FirstContactAt is null && LeadStatus.IsOpen(r.Status) && (now - r.ReceivedAt).TotalHours > target)
            : null;

        var converted = rows.Where(r => r.Status == LeadStatus.Converted).ToList();
        var toConvert = converted.Where(r => r.ConvertedAt is not null).Select(r => (r.ConvertedAt!.Value - r.ReceivedAt).TotalHours).Where(h => h >= 0).ToList();

        return new LeadReportDto(
            from,
            to,
            rows.Count,
            Count(rows, r => r.Source ?? string.Empty),
            Count(rows, r => r.Status),
            open.Select(Row.From).GroupBy(r => r.OwnerId).Select(g => new LeadOwnerCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList(),
            contacted.Count,
            lags.Count == 0 ? null : Math.Round(lags.Average(), 2),
            lags.Count == 0 ? null : Math.Round(Median(lags), 2),
            targetHours,
            breaches,
            converted.Count,
            rows.Count == 0 ? null : Math.Round((double)converted.Count / rows.Count, 4),
            toConvert.Count == 0 ? null : Math.Round(toConvert.Average(), 2),
            convertedInPeriod.Count,
            Count(rows.Where(r => r.Status == LeadStatus.Disqualified).ToList(), r => r.DisqualificationReason ?? string.Empty),
            truncated);
    }

    private static IReadOnlyList<LeadCount> Count(IReadOnlyList<Row> rows, Func<Row, string> key) =>
        rows.GroupBy(key).Select(g => new LeadCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    private sealed record Row(
        string Status, string? Source, Guid? OwnerId, DateTimeOffset ReceivedAt, DateTimeOffset? FirstContactAt, DateTimeOffset? ConvertedAt,
        string? DisqualificationReason)
    {
        public static Row From(RecordData record)
        {
            string? Text(string name) => record.Values[name]?.GetValue<string>();
            DateTimeOffset? Time(string name) =>
                Text(name) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
            return new Row(
                Text("status") ?? LeadStatus.New,
                Text("source"),
                Guid.TryParse(Text("ownerId"), out var owner) ? owner : null,
                Time("firstReceivedAt") ?? DateTimeOffset.MinValue,
                Time("firstContactAt"),
                Time("convertedAt"),
                Text("disqualificationReason"));
        }
    }
}
