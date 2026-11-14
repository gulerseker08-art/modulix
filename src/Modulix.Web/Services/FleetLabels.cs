using Modulix.Shared.Fleet;

namespace Modulix.Web.Services;

/// <summary>Filo ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class FleetLabels
{
    /// <summary>Mülkiyet ve durum tabloları her okunuşta etkin dile göre kurulur (Türkçe kaynak metin sözlükte yoksa yedek kalır).</summary>
    public static IReadOnlyList<(string Key, string Label)> Ownerships => WarehouseLabels.Translate("fleet.ownership", FleetAccess.Ownerships);

    public static IReadOnlyList<(string Key, string Label)> Statuses => WarehouseLabels.Translate("fleet.status", FleetAccess.Statuses);

    public static string Ownership(string? key) => Ownerships.FirstOrDefault(o => o.Key == key).Label ?? key ?? string.Empty;

    public static string Status(string? key) => Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Etkin başarı tonu, bakımda dikkat tonu, pasif nötr ton.</summary>
    public static string StatusBadge(string? key) => key switch
    {
        "active" => "status-customer",
        "maintenance" => "stage-negotiation",
        _ => "status-inactive",
    };

    /// <summary>Araç türü kategorisinin tonu: motorlu marka tonu, çekilen (dorse, römork) ikincil ton.</summary>
    public static string KindBadge(string? category) => category == FleetAccess.KindTowed ? "stage-proposal" : "stage-qualified";

    /// <summary>Bakım türü kategorisinin tonu: plansız onarım dikkat tonu, planlı bakım marka tonu.</summary>
    public static string MaintenanceBadge(string? category) => category == FleetAccess.MaintenanceUnplanned ? "stage-negotiation" : "stage-qualified";

    /// <summary>
    /// Sonraki bakımın rozeti: geçmişse kayıp tonu ("Gecikti"), 14 gün içindeyse dikkat tonu, daha ilerideyse nötr. Eşik yalnızca görseldir;
    /// uyarı kuralları firmanın otomasyonundadır (tarihe kala tetikleyicisi).
    /// </summary>
    public static (string Badge, string Text) Due(DateOnly? due, DateOnly today) => due switch
    {
        null => ("status-inactive", Texts.Get("fleet.due.none")),
        { } d when d < today => ("stage-lost", Texts.Get("fleet.due.overdue", Date(d))),
        { } d when d <= today.AddDays(14) => ("stage-negotiation", Date(d)),
        { } d => ("status-customer", Date(d)),
    };

    public static string Date(DateOnly day) => day.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly? Day(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static string Km(int? km) => km is { } v ? $"{v.ToString("#,##0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} km" : "—";

    public static string Money(decimal? amount, string? currency) =>
        amount is { } a ? $"{a.ToString("#,##0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} {currency}".Trim() : "—";

    /// <summary>Kapasiteyi Türkçe sayı gösterimiyle ve birimiyle yazar; boşsa "—".</summary>
    public static string Capacity(decimal? value, string unit) =>
        value is { } v ? $"{v.ToString("#,##0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} {unit}" : "—";
}
