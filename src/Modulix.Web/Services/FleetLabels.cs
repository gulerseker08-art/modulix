using Modulix.Shared.Fleet;

namespace Modulix.Web.Services;

/// <summary>Filo ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class FleetLabels
{
    public static string Ownership(string? key) => FleetAccess.Ownerships.FirstOrDefault(o => o.Key == key).Label ?? key ?? string.Empty;

    public static string Status(string? key) => FleetAccess.Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Etkin başarı tonu, bakımda dikkat tonu, pasif nötr ton.</summary>
    public static string StatusBadge(string? key) => key switch
    {
        "active" => "status-customer",
        "maintenance" => "stage-negotiation",
        _ => "status-inactive",
    };

    /// <summary>Araç türü kategorisinin tonu: motorlu marka tonu, çekilen (dorse, römork) ikincil ton.</summary>
    public static string KindBadge(string? category) => category == FleetAccess.KindTowed ? "stage-proposal" : "stage-qualified";

    /// <summary>Kapasiteyi Türkçe sayı gösterimiyle ve birimiyle yazar; boşsa "—".</summary>
    public static string Capacity(decimal? value, string unit) =>
        value is { } v ? $"{v.ToString("#,##0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} {unit}" : "—";
}
