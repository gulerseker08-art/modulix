using Modulix.Shared.Records;

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
    public const string FreightRead = "logistics.freight.read";

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

    public const string TripsRead = "logistics.trips.read";
    public const string TripsWrite = "logistics.trips.write";

    /// <summary>Panoyu açmak için bunlardan biri yeter; her bölüm kendi okuma iznine bağlıdır.</summary>
    public static IReadOnlyList<string> DashboardAny { get; } = [RequestsRead, ShipmentsRead, TripsRead, DriversRead];

    public const string TripEntityKey = "logistics.trip";
    public const string TripStopEntityKey = "logistics.trip-stop";
    public const string DeliveryProofEntityKey = "logistics.delivery-proof";

    /// <summary>Teslim sonuçları (sabit; sunucudaki <c>DeliveryProof.Outcomes</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> DeliveryOutcomes { get; } =
    [
        ("delivered", "Teslim edildi"),
        ("partial", "Kısmi teslim"),
        ("failed", "Teslim edilemedi"),
    ];

    public const string TripVehicleLookup = "logistics.trip.vehicle";
    public const string TripTrailerLookup = "logistics.trip.trailer";
    public const string TripCarrierLookup = "logistics.trip.carrier";

    /// <summary>Sefer durumları (sabit; sunucudaki <c>Trip.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> TripStatuses { get; } =
    [
        ("planning", "Planlanıyor"),
        ("ready", "Hazır"),
        ("inProgress", "Yolda"),
        ("completed", "Tamamlandı"),
        ("cancelled", "İptal edildi"),
    ];

    /// <summary>Durak türleri (sabit; sunucudaki <c>TripStop.Types</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> StopTypes { get; } =
    [
        ("loading", "Yükleme"),
        ("unloading", "Boşaltma"),
        ("border", "Sınır / gümrük"),
        ("rest", "Mola"),
        ("emptyReturn", "Boş dönüş"),
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

/// <summary>Seferin uygunluk denetimi (ADR-0038 dilim 5): engel varsa plan onaylanmaz; uyarılar onayla birlikte seferde saklanır.</summary>
public sealed record TripCheckDto(bool CanConfirm, IReadOnlyList<TripCheckIssueDto> Issues, TripLoadDto Load);

public sealed record TripCheckIssueDto(string Code, string Message, bool Blocking);

/// <summary>Seferin yükü ve araç + dorse kapasitesi; kapasitesi girilmemiş ölçü boş.</summary>
public sealed record TripLoadDto(
    int ShipmentCount, int StopCount, decimal WeightKg, decimal? PayloadKg, int Pallets, int? PalletCapacity, decimal LoadingMeters, decimal? LoadingMetersCapacity,
    decimal VolumeM3, decimal? VolumeCapacity);

/// <summary>Planlama bekleyen sevkiyatı sefere ekleme isteği.</summary>
public sealed record AssignShipmentRequest(Guid ShipmentId);

/// <summary>
/// Teslim komutu isteği (ADR-0038 K8, dilim 6). <c>Outcome</c>: <c>delivered</c>, <c>partial</c> ya da <c>failed</c>. Teslim ve kısmi teslimde teslim alan,
/// kısmi teslimde açıklama (<c>Notes</c>), başarısız teslimde neden zorunludur. Zaman sunucuda verilir; fotoğraf ve imza kanıt kaydına dosya olarak eklenir.
/// </summary>
public sealed record RecordDeliveryRequest(long Version, string Outcome, string? ReceivedBy, bool Damaged, bool Shortage, string? FailureReason, string? Notes);

/// <summary>Teslim komutunun sonucu: güncel sevkiyat ve yazılan (değişmez) teslim kanıtı.</summary>
public sealed record DeliveryResponse(RecordResponse Shipment, RecordResponse Proof);

/// <summary>
/// Lojistik operasyon panosu (ADR-0038 dilim 9a; <c>GET /api/logistics/dashboard</c>). Bölümler okuma iznine göre gelir: izni olmayan bölüm boştur.
/// Alan adları sunucudaki <c>LogisticsDashboard</c> kaydıyla aynıdır.
/// </summary>
public sealed record LogisticsDashboardDto(
    DateOnly Today,
    DashboardRequestsDto? Requests,
    DashboardShipmentsDto? Shipments,
    DashboardTripsDto? Trips,
    DashboardDeliveriesDto? Deliveries,
    DashboardCredentialsDto? Credentials);

public sealed record DashboardRequestsDto(int Awaiting, int? OldestWaitingDays);

public sealed record DashboardShipmentsDto(
    int Pending, int PickupSoonUnplanned, int Late, int InTransit, IReadOnlyList<DashboardStatusCountDto> ByStatus, IReadOnlyList<DashboardLateShipmentDto> LateItems);

public sealed record DashboardStatusCountDto(string Status, int Count);

public sealed record DashboardLateShipmentDto(Guid Id, string PickupCity, string DeliveryCity, DateOnly DeliveryBy, string Status, int DaysLate);

public sealed record DashboardTripsDto(int Planning, int Ready, int InProgress, int StartingToday, IReadOnlyList<DashboardTripDto> Active);

public sealed record DashboardTripDto(Guid Id, string Number, string Status, DateOnly PlannedStart, Guid? VehicleId, Guid? TrailerId, int Stops, int Arrived, string? NextCity);

public sealed record DashboardDeliveriesDto(int Delivered, int Partial, int Failed, int Damaged, decimal? SuccessPercent, IReadOnlyList<DashboardDayDto> Days);

public sealed record DashboardDayDto(DateOnly Day, int Delivered, int Partial, int Failed);

public sealed record DashboardCredentialsDto(int Expired, int ExpiringSoon, IReadOnlyList<DashboardCredentialDto> Items);

public sealed record DashboardCredentialDto(Guid DriverId, string DriverName, string TypeLabel, DateOnly ValidTo);
