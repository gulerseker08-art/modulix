namespace Modulix.Web.Services;

/// <summary>Destek raporlarının gösterim yardımcıları (ADR-0024 dilim 6). Hesap sunucudadır; burada yalnızca biçim vardır.</summary>
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
        return days > 0 ? $"{days} g {hours} sa" : hours > 0 ? $"{hours} sa {mins} dk" : $"{mins} dk";
    }

    public static string Percent(double? value) => value is { } v ? $"%{v:0.#}" : "—";

    public static string Metric(string metric) => metric switch { "FirstResponse" => "İlk yanıt", "Resolution" => "Çözüm", _ => metric };

    public static string Granularity(string key) => key switch { "week" => "Haftalık", "month" => "Aylık", _ => "Günlük" };

    /// <summary>Talep listesine rapor kırılımı bağlantısı (süzgeç sorgu dizesi olarak taşınır).</summary>
    public static string Drill(string drillDown) => $"destek/talepler?filtre={Uri.EscapeDataString(drillDown)}";
}
