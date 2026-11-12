namespace Modulix.Shared.Logistics;

/// <summary>
/// Lojistik modülünün izin, kayıt türü ve lookup anahtarları (arayüz için); sunucudaki <c>LogisticsModule</c> ile aynıdır (birim testle
/// denetlenir). Sürücü kartları genel kayıt uçlarından (<c>/api/records/logistics.driver</c>) okunur ve yazılır; sürücünün adı ve telefonu
/// ortak kişi kaydındadır (<c>/api/records/core.person</c>).
/// </summary>
public static class LogisticsAccess
{
    public const string ModuleKey = "logistics";

    public const string DriversRead = "logistics.drivers.read";
    public const string DriversWrite = "logistics.drivers.write";
    public const string CarriersRead = "logistics.carriers.read";
    public const string CarriersWrite = "logistics.carriers.write";

    public const string DriverEntityKey = "logistics.driver";
    public const string CarrierTermsEntityKey = "logistics.carrier-terms";

    public const string DriverPersonLookup = "logistics.driver.person";
    public const string DriverCarrierLookup = "logistics.driver.carrier";
    public const string DriverVehicleLookup = "logistics.driver.vehicle";
    public const string TermsCarrierLookup = "logistics.carrier-terms.carrier";

    public const string RequestsRead = "logistics.requests.read";
    public const string RequestsWrite = "logistics.requests.write";
    public const string RequestsApprove = "logistics.requests.approve";
    public const string ShipmentsRead = "logistics.shipments.read";
    public const string ShipmentsWrite = "logistics.shipments.write";

    public const string TransportRequestEntityKey = "logistics.transport-request";
    public const string ShipmentEntityKey = "logistics.shipment";

    public const string TransportModeList = "logistics.transport-mode";

    public const string RequestCustomerLookup = "logistics.transport-request.customer";
    public const string RequestSenderLookup = "logistics.transport-request.sender";
    public const string RequestReceiverLookup = "logistics.transport-request.receiver";
    public const string ShipmentCustomerLookup = "logistics.shipment.customer";
    public const string ShipmentSenderLookup = "logistics.shipment.sender";
    public const string ShipmentReceiverLookup = "logistics.shipment.receiver";

    /// <summary>Talep durumları (sabit; sunucudaki <c>TransportRequest.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> RequestStatuses { get; } =
    [
        ("new", "Yeni"),
        ("approved", "Onaylandı"),
        ("rejected", "Reddedildi"),
        ("cancelled", "İptal edildi"),
    ];

    /// <summary>Sevkiyat durumları (sabit; sunucudaki <c>Shipment.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ShipmentStatuses { get; } =
    [
        ("pending", "Planlama bekliyor"),
        ("planned", "Planlandı"),
        ("loading", "Yükleniyor"),
        ("inTransit", "Yolda"),
        ("delivered", "Teslim edildi"),
        ("partial", "Kısmi teslim"),
        ("failed", "Teslim edilemedi"),
        ("cancelled", "İptal edildi"),
    ];

    public const string Subcontracted = "subcontracted";

    /// <summary>Sürücü tipleri (sabit; sunucudaki <c>Driver.Types</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> DriverTypes { get; } =
    [
        ("employee", "Kadrolu"),
        ("subcontracted", "Taşeron"),
    ];

    /// <summary>Durumlar (sabit; sunucudaki <c>Driver.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("active", "Etkin"),
        ("onLeave", "İzinde"),
        ("passive", "Pasif"),
    ];
}

/// <summary>Talep ve sevkiyat iş akışı eylemi (ADR-0038 K7): yapılabilir mi, değilse neden; gerekçe ister mi; yıkıcı mı (iptal, ret).</summary>
public sealed record LogisticsActionDto(string Action, string Label, bool Allowed, string? Message, bool RequiresReason, bool Destructive);

/// <summary>İş akışı eylemi isteği; sürüm eşzamanlı değişikliği yakalar (409).</summary>
public sealed record RunLogisticsActionRequest(string Action, long Version, string? Reason);
