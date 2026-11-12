using Modulix.Shared.Logistics;

namespace Modulix.Web.Services;

/// <summary>Lojistik ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class LogisticsLabels
{
    public static string DriverType(string? key) => LogisticsAccess.DriverTypes.FirstOrDefault(t => t.Key == key).Label ?? key ?? string.Empty;

    public static string Status(string? key) => LogisticsAccess.Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Etkin başarı tonu, izinde dikkat tonu, pasif nötr ton.</summary>
    public static string StatusBadge(string? key) => key switch
    {
        "active" => "status-customer",
        "onLeave" => "stage-negotiation",
        _ => "status-inactive",
    };

    /// <summary>Kadrolu marka tonu, taşeron ikincil ton.</summary>
    public static string TypeBadge(string? key) => key == LogisticsAccess.Subcontracted ? "stage-proposal" : "stage-qualified";
}
