namespace Modulix.Shared.Fleet;

/// <summary>
/// Filo modülünün izin, kayıt türü, liste ve lookup anahtarları (arayüz için); sunucudaki <c>FleetModule</c> ile aynıdır (birim testle
/// denetlenir). Araç kayıtları genel kayıt uçlarından (<c>/api/records/fleet.vehicle</c>) okunur ve yazılır.
/// </summary>
public static class FleetAccess
{
    public const string ModuleKey = "fleet";

    public const string VehiclesRead = "fleet.vehicles.read";
    public const string VehiclesWrite = "fleet.vehicles.write";

    public const string VehicleEntityKey = "fleet.vehicle";

    public const string VehicleKindList = "fleet.vehicle-kind";

    public const string VehicleCarrierLookup = "fleet.vehicle.carrier";

    /// <summary>Araç türü kategorileri: motorlu ve çekilen (dorse, römork).</summary>
    public const string KindPowered = "powered";
    public const string KindTowed = "towed";

    /// <summary>Mülkiyet anahtarları (sabit; sunucudaki <c>Vehicle.Ownerships</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> Ownerships { get; } =
    [
        ("owned", "Özmal"),
        ("leased", "Kiralık"),
        ("subcontracted", "Taşeron"),
    ];

    /// <summary>Durum anahtarları (sabit; sunucudaki <c>Vehicle.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("active", "Etkin"),
        ("maintenance", "Bakımda"),
        ("passive", "Pasif"),
    ];

    public const string Subcontracted = "subcontracted";
}
