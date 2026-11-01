using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>SLA ekranlarının etiketleri; anahtarlar sunucudaki <c>SlaMetric</c>, <c>SlaEscalationAction</c> ve örnek durumlarıyla aynıdır.</summary>
public static class SlaLabels
{
    public static IReadOnlyList<(string Key, string Label)> Metrics { get; } = [("FirstResponse", "İlk yanıt"), ("Resolution", "Çözüm")];

    public static IReadOnlyList<(string Key, string Label)> Actions { get; } =
        [("NotifyOwner", "Sahibine bildir"), ("NotifyUser", "Kullanıcıya bildir (ör. ekip lideri)"), ("Reassign", "Yeniden ata")];

    /// <summary>Örnek durumu (sunucunun hesapladığı <c>State</c>). Renk tek gösterge değildir; metin her zaman vardır.</summary>
    public static IReadOnlyList<(string Key, string Label)> States { get; } =
    [
        ("Running", "Hedefte (çalışıyor)"), ("Paused", "Duraklatıldı"), ("Breached", "Hedef aşıldı"), ("Met", "Hedefte tamamlandı"),
        ("Missed", "Hedef aşıldıktan sonra tamamlandı"), ("Cancelled", "İptal edildi"),
    ];

    public static IReadOnlyList<(int Day, string Label)> Days { get; } =
        [(1, "Pazartesi"), (2, "Salı"), (3, "Çarşamba"), (4, "Perşembe"), (5, "Cuma"), (6, "Cumartesi"), (0, "Pazar")];

    public static string ShortDay(int day) => day switch { 1 => "Pzt", 2 => "Sal", 3 => "Çar", 4 => "Per", 5 => "Cum", 6 => "Cmt", _ => "Paz" };

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
        if (days > 0) { parts.Add($"{days} gün"); }
        if (hours > 0) { parts.Add($"{hours} sa"); }
        if (mins > 0 || parts.Count == 0) { parts.Add($"{mins} dk"); }
        return string.Join(' ', parts);
    }

    /// <summary>Hedefe kalan/aşan süre: gösterim amaçlıdır, otorite sunucu hesabıdır.</summary>
    public static string Remaining(DateTimeOffset due, DateTimeOffset now)
    {
        var minutes = (int)Math.Round((due - now).TotalMinutes);
        return minutes >= 0 ? $"{Duration(minutes)} kaldı" : $"{Duration(minutes)} aşıldı";
    }

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string HistoryKind(string kind) => kind switch
    {
        "SlaBreached" => "SLA hedefi aşıldı",
        "SlaEscalated" => "SLA eskalasyonu uygulandı",
        "SlaRecalculationRequested" => "SLA yeniden hesaplama istendi",
        "SlaRecalculated" => "SLA yeniden hesaplandı",
        _ => kind,
    };

    public static string Time(DateTimeOffset? time) => time is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
}
