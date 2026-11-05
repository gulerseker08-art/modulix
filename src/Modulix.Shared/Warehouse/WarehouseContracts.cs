namespace Modulix.Shared.Warehouse;

/// <summary>
/// Depo modülünün izin, kayıt türü ve liste anahtarları (arayüz için); sunucudaki <c>WarehouseModule</c> ile aynıdır
/// (birim testle denetlenir). Depo kayıtları genel kayıt uçlarından (<c>/api/records/{entityKey}</c>) okunur ve yazılır.
/// </summary>
public static class WarehouseAccess
{
    public const string ModuleKey = "warehouse";

    public const string FacilitiesRead = "warehouse.facilities.read";
    public const string FacilitiesWrite = "warehouse.facilities.write";
    public const string ZonesRead = "warehouse.zones.read";
    public const string ZonesWrite = "warehouse.zones.write";
    public const string LocationLevelsRead = "warehouse.locationlevels.read";
    public const string LocationLevelsWrite = "warehouse.locationlevels.write";
    public const string LocationsRead = "warehouse.locations.read";
    public const string LocationsWrite = "warehouse.locations.write";

    public const string FacilityEntityKey = "warehouse.facility";
    public const string ZoneEntityKey = "warehouse.zone";
    public const string LocationLevelEntityKey = "warehouse.location-level";
    public const string LocationEntityKey = "warehouse.location";

    /// <summary>Alan türü listesi; kategorileri kodun anladığı anlamdır, adları tenant'ındır.</summary>
    public const string ZoneKindList = "warehouse.zone-kind";

    public const string ZoneKindControlled = "controlled";
    public const string ZoneKindFree = "free";
}
