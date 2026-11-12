using Modulix.Shared.Logistics;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>Lojistik ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class LogisticsLabels
{

    /// <summary>İş akışı uçlarındaki tür adları (<c>/api/logistics/{tür}/...</c>).</summary>
    public const string Requests = "requests";
    public const string Shipments = "shipments";

    public static string CargoStatus(string kind, string? key) =>
        (kind == Requests ? LogisticsAccess.RequestStatuses : LogisticsAccess.ShipmentStatuses).FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Yeni ve planlama bekleyen marka tonu, ilerleyen dikkat tonu, tamamlanan başarı tonu, ret/iptal/başarısız kayıp ya da nötr ton.</summary>
    public static string CargoStatusBadge(string? key) => key switch
    {
        "new" or "pending" => "stage-qualified",
        "planned" or "loading" or "inTransit" => "stage-negotiation",
        "approved" or "delivered" => "status-customer",
        "partial" => "stage-proposal",
        "rejected" or "failed" => "stage-lost",
        _ => "status-inactive",
    };

    /// <summary>Güzergâh: "İstanbul → Ankara".</summary>
    public static string Route(RecordResponse record) => $"{WarehouseLabels.Text(record, "pickupCity")} → {WarehouseLabels.Text(record, "deliveryCity")}";
    public static string DriverType(string? key) => LogisticsAccess.DriverTypes.FirstOrDefault(t => t.Key == key).Label ?? key ?? string.Empty;

    public static string Status(string? key) => LogisticsAccess.Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Etkin başarı tonu, izinde dikkat tonu, pasif nötr ton.</summary>
    public static string StatusBadge(string? key) => key switch
    {
        "active" => "status-customer",
        "onLeave" => "stage-negotiation",
        _ => "status-inactive",
    };

    /// <summary>Anlaşmanın geçerlilik aralığı: "01.01.2026 – 31.12.2026" ya da bitişsizse "01.01.2026'dan itibaren".</summary>
    public static string Validity(RecordResponse terms)
    {
        var from = FleetLabels.Day(WarehouseLabels.Text(terms, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(terms, "validTo"));
        return (from, to) switch
        {
            ({ } f, { } t) => $"{FleetLabels.Date(f)} – {FleetLabels.Date(t)}",
            ({ } f, null) => $"{FleetLabels.Date(f)} tarihinden itibaren",
            _ => "—",
        };
    }

    /// <summary>
    /// Anlaşmanın bugünkü durumu: pasif nötr, süresi dolmuş kayıp tonu, henüz başlamamış marka tonu, 30 gün içinde bitecek dikkat tonu,
    /// geçerli başarı tonu. Eşik yalnızca görseldir; uyarı kuralları firmanın otomasyonundadır.
    /// </summary>
    public static (string Badge, string Text) TermsState(RecordResponse terms, DateOnly today)
    {
        if (!WarehouseLabels.Flag(terms, "isActive", true))
        {
            return ("status-inactive", "Pasif");
        }

        var from = FleetLabels.Day(WarehouseLabels.Text(terms, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(terms, "validTo"));
        return (from, to) switch
        {
            (_, { } t) when t < today => ("stage-lost", "Süresi doldu"),
            ({ } f, _) when f > today => ("stage-qualified", "Başlamadı"),
            (_, { } t) when t <= today.AddDays(30) => ("stage-negotiation", "Bitiyor"),
            _ => ("status-customer", "Geçerli"),
        };
    }

    /// <summary>Kadrolu marka tonu, taşeron ikincil ton.</summary>
    public static string TypeBadge(string? key) => key == LogisticsAccess.Subcontracted ? "stage-proposal" : "stage-qualified";
}
