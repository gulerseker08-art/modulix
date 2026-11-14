using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>
/// SLA ekranlarının etiketleri; anahtarlar sunucudaki <c>SlaMetric</c>, <c>SlaEscalationAction</c> ve örnek durumlarıyla aynıdır. Türkçe ad kaynak
/// metindir ve <see cref="SupportLabels.Translate"/> ile her okunuşta çevrilir (ADR-0034, dilim 12).
/// </summary>
public static class SlaLabels
{
    public const string MetricGroup = "support.sla-metric";
    public const string ActionGroup = "support.sla-action";
    public const string StateGroup = "support.sla-state";
    public const string DayGroup = "support.weekday";
    public const string ShortDayGroup = "support.weekday-short";
    public const string HistoryGroup = "support.sla-history";

    internal static readonly (string Key, string Label)[] MetricSource = [("FirstResponse", "İlk yanıt"), ("Resolution", "Çözüm")];

    internal static readonly (string Key, string Label)[] ActionSource =
        [("NotifyOwner", "Sahibine bildir"), ("NotifyUser", "Kullanıcıya bildir (ör. ekip lideri)"), ("Reassign", "Yeniden ata")];

    internal static readonly (string Key, string Label)[] StateSource =
    [
        ("Running", "Hedefte (çalışıyor)"), ("Paused", "Duraklatıldı"), ("Breached", "Hedef aşıldı"), ("Met", "Hedefte tamamlandı"),
        ("Missed", "Hedef aşıldıktan sonra tamamlandı"), ("Cancelled", "İptal edildi"),
    ];

    // Gün anahtarı haftanın günü numarasıdır (0 = pazar).
    internal static readonly (string Key, string Label)[] DaySource =
        [("1", "Pazartesi"), ("2", "Salı"), ("3", "Çarşamba"), ("4", "Perşembe"), ("5", "Cuma"), ("6", "Cumartesi"), ("0", "Pazar")];

    internal static readonly (string Key, string Label)[] ShortDaySource =
        [("1", "Pzt"), ("2", "Sal"), ("3", "Çar"), ("4", "Per"), ("5", "Cum"), ("6", "Cmt"), ("0", "Paz")];

    internal static readonly (string Key, string Label)[] HistorySource =
    [
        ("SlaBreached", "SLA hedefi aşıldı"), ("SlaEscalated", "SLA eskalasyonu uygulandı"), ("SlaRecalculationRequested", "SLA yeniden hesaplama istendi"),
        ("SlaRecalculated", "SLA yeniden hesaplandı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Metrics => SupportLabels.Translate(MetricGroup, MetricSource);

    public static IReadOnlyList<(string Key, string Label)> Actions => SupportLabels.Translate(ActionGroup, ActionSource);

    /// <summary>Örnek durumu (sunucunun hesapladığı <c>State</c>). Renk tek gösterge değildir; metin her zaman vardır.</summary>
    public static IReadOnlyList<(string Key, string Label)> States => SupportLabels.Translate(StateGroup, StateSource);

    /// <summary>Gün tablosu (anahtar haftanın günü numarası, metin olarak).</summary>
    public static IReadOnlyList<(string Key, string Label)> DayRows => SupportLabels.Translate(DayGroup, DaySource);

    public static IReadOnlyList<(string Key, string Label)> ShortDays => SupportLabels.Translate(ShortDayGroup, ShortDaySource);

    public static IReadOnlyList<(string Key, string Label)> Histories => SupportLabels.Translate(HistoryGroup, HistorySource);

    public static IReadOnlyList<(int Day, string Label)> Days =>
        [.. SupportLabels.Translate(DayGroup, DaySource).Select(x => (int.Parse(x.Key, CultureInfo.InvariantCulture), x.Label))];

    public static string ShortDay(int day) =>
        SupportLabels.Lookup(ShortDayGroup, ShortDaySource, (day is >= 1 and <= 6 ? day : 0).ToString(CultureInfo.InvariantCulture));

    public static string StateBadge(string state) => state switch
    {
        "Met" => "won",
        "Breached" or "Missed" => "lost",
        "Paused" => "onhold",
        "Cancelled" => "cancelled",
        _ => "open",
    };

    /// <summary>"3 sa 20 dk" biçimi (dakikadan).</summary>
    public static string Duration(int minutes)
    {
        var abs = Math.Abs(minutes);
        var days = abs / 1440;
        var hours = abs % 1440 / 60;
        var mins = abs % 60;
        var parts = new List<string>();
        if (days > 0) { parts.Add(SupportLabels.Format("support.duration.days", "{0} gün", days)); }
        if (hours > 0) { parts.Add(SupportLabels.Format("support.duration.hours", "{0} sa", hours)); }
        if (mins > 0 || parts.Count == 0) { parts.Add(SupportLabels.Format("support.duration.minutes", "{0} dk", mins)); }
        return string.Join(' ', parts);
    }

    /// <summary>Hedefe kalan/aşan süre: gösterim amaçlıdır, otorite sunucu hesabıdır.</summary>
    public static string Remaining(DateTimeOffset due, DateTimeOffset now)
    {
        var minutes = (int)Math.Round((due - now).TotalMinutes);
        return minutes >= 0
            ? SupportLabels.Format("support.duration.left", "{0} kaldı", Duration(minutes))
            : SupportLabels.Format("support.duration.over", "{0} aşıldı", Duration(minutes));
    }

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string HistoryKind(string kind) => SupportLabels.Lookup(HistoryGroup, HistorySource, kind);

    public static string Time(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
}
