namespace Modulix.Web.Services;

/// <summary>Destek raporlarının gösterim yardımcıları (ADR-0024 dilim 6). Hesap sunucudadır; burada yalnızca biçim vardır. Metinler sözlükten gelir (ADR-0034, dilim 12).</summary>
public static class ReportLabels
{
    public static string Minutes(double? minutes)
    {
        if (minutes is not { } value)
        {
            return "—";
        }

        var total = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        var days = total / 1440;
        var hours = total % 1440 / 60;
        var mins = total % 60;
        return days > 0 ? SupportLabels.Format("support.duration.dayHour", "{0} g {1} sa", days, hours)
            : hours > 0 ? SupportLabels.Format("support.duration.hourMinute", "{0} sa {1} dk", hours, mins)
            : SupportLabels.Format("support.duration.minutes", "{0} dk", mins);
    }

    public static string Percent(double? value) => value is { } v ? SupportLabels.Format("support.percent", "%{0}", v.ToString("0.#")) : "—";

    public static string Metric(string metric) => SupportLabels.Lookup(SlaLabels.MetricGroup, SlaLabels.MetricSource, metric);

    internal static readonly (string Key, string Label)[] GranularitySource = [("day", "Günlük"), ("week", "Haftalık"), ("month", "Aylık")];

    public const string GranularityGroup = "support.granularity";

    public static IReadOnlyList<(string Key, string Label)> Granularities => SupportLabels.Translate(GranularityGroup, GranularitySource);

    public static string Granularity(string key) => SupportLabels.Lookup(GranularityGroup, GranularitySource, key is "week" or "month" ? key : "day");

    /// <summary>Talep listesine rapor kırılımı bağlantısı (süzgeç sorgu dizesi olarak taşınır).</summary>
    public static string Drill(string drillDown) => $"destek/talepler?filtre={Uri.EscapeDataString(drillDown)}";
}
