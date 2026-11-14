using Modulix.Shared.Logistics;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>Lojistik ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class LogisticsLabels
{

    /// <summary>İş akışı uçlarındaki tür adları (<c>/api/logistics/{tür}/...</c>).</summary>
    public const string Requests = "requests";
    public const string Shipments = "shipments";
    public const string Trips = "trips";

    /// <summary>Seçenek tabloları her okunuşta etkin dile göre kurulur (Türkçe kaynak metin sözlükte yoksa yedek kalır).</summary>
    public static IReadOnlyList<(string Key, string Label)> TripStatuses => WarehouseLabels.Translate("logistics.trip-status", LogisticsAccess.TripStatuses);

    public static IReadOnlyList<(string Key, string Label)> StopTypes => WarehouseLabels.Translate("logistics.stop-type", LogisticsAccess.StopTypes);

    public static IReadOnlyList<(string Key, string Label)> RequestStatuses => WarehouseLabels.Translate("logistics.request-status", LogisticsAccess.RequestStatuses);

    public static IReadOnlyList<(string Key, string Label)> ShipmentStatuses => WarehouseLabels.Translate("logistics.shipment-status", LogisticsAccess.ShipmentStatuses);

    public static IReadOnlyList<(string Key, string Label)> DeliveryOutcomes => WarehouseLabels.Translate("logistics.delivery-outcome", LogisticsAccess.DeliveryOutcomes);

    public static IReadOnlyList<(string Key, string Label)> DriverTypes => WarehouseLabels.Translate("logistics.driver-type", LogisticsAccess.DriverTypes);

    public static IReadOnlyList<(string Key, string Label)> Statuses => WarehouseLabels.Translate("logistics.driver-status", LogisticsAccess.Statuses);

    public static string TripStatus(string? key) => TripStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Planlanan marka tonu, hazır bilgi tonu, yolda dikkat tonu, tamamlanan başarı tonu, iptal nötr ton.</summary>
    public static string TripStatusBadge(string? key) => key switch
    {
        "planning" => "stage-qualified",
        "ready" => "stage-proposal",
        "inProgress" => "stage-negotiation",
        "completed" => "status-customer",
        _ => "status-inactive",
    };

    public static string StopType(string? key) => StopTypes.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Durak türü ikonu: yükleme, boşaltma, sınır, mola, boş dönüş.</summary>
    public static string StopIcon(string? key) => key switch
    {
        "loading" => "package",
        "unloading" => "check-circle",
        "border" => "shield",
        "rest" => "clock",
        _ => "truck",
    };

    public static string CargoStatus(string kind, string? key) =>
        (kind == Requests ? RequestStatuses : ShipmentStatuses).FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

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

    /// <summary>Güzergâh: "İstanbul → Ankara"; Türkiye dışındaki uç ülke koduyla yazılır: "Hamburg (DE) → İstanbul" (ADR-0043).</summary>
    public static string Route(RecordResponse record) => $"{Place(record, "pickupCity", "pickupCountry")} → {Place(record, "deliveryCity", "deliveryCountry")}";

    private static string Place(RecordResponse record, string cityField, string countryField)
    {
        var city = WarehouseLabels.Text(record, cityField);
        var country = WarehouseLabels.Text(record, countryField);
        return country.Length == 0 || country == CountryCatalog.DefaultCode ? city : $"{city} ({country})";
    }

    public static string DriverType(string? key) => DriverTypes.FirstOrDefault(t => t.Key == key).Label ?? key ?? string.Empty;

    public static string DeliveryOutcome(string? key) => DeliveryOutcomes.FirstOrDefault(o => o.Key == key).Label ?? key ?? string.Empty;

    public static string Status(string? key) => Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

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
            ({ } f, { } t) => Texts.Get("logistics.validity.range", FleetLabels.Date(f), FleetLabels.Date(t)),
            ({ } f, null) => Texts.Get("logistics.validity.since", FleetLabels.Date(f)),
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
            return ("status-inactive", Texts.Get("common.inactive"));
        }

        var from = FleetLabels.Day(WarehouseLabels.Text(terms, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(terms, "validTo"));
        return (from, to) switch
        {
            (_, { } t) when t < today => ("stage-lost", Texts.Get("logistics.terms-state.expired")),
            ({ } f, _) when f > today => ("stage-qualified", Texts.Get("logistics.terms-state.not-started")),
            (_, { } t) when t <= today.AddDays(30) => ("stage-negotiation", Texts.Get("logistics.terms-state.ending")),
            _ => ("status-customer", Texts.Get("logistics.terms-state.valid")),
        };
    }

    /// <summary>Kadrolu marka tonu, taşeron ikincil ton.</summary>
    public static string TypeBadge(string? key) => key == LogisticsAccess.Subcontracted ? "stage-proposal" : "stage-qualified";
}
