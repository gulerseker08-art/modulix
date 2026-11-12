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

    public const string DriverEntityKey = "logistics.driver";

    public const string DriverPersonLookup = "logistics.driver.person";
    public const string DriverCarrierLookup = "logistics.driver.carrier";
    public const string DriverVehicleLookup = "logistics.driver.vehicle";

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
