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
    public const string AttorneysRead = "warehouse.attorneys.read";
    public const string AttorneysWrite = "warehouse.attorneys.write";
    public const string StockItemsRead = "warehouse.stockitems.read";
    public const string StockItemsWrite = "warehouse.stockitems.write";
    public const string DeclarationsRead = "warehouse.customs.declarations.read";
    public const string DeclarationsWrite = "warehouse.customs.declarations.write";

    public const string FacilityEntityKey = "warehouse.facility";
    public const string ZoneEntityKey = "warehouse.zone";
    public const string LocationLevelEntityKey = "warehouse.location-level";
    public const string LocationEntityKey = "warehouse.location";
    public const string PowerOfAttorneyEntityKey = "warehouse.power-of-attorney";
    public const string StockItemEntityKey = "warehouse.stock-item";
    public const string CustomsDeclarationEntityKey = "warehouse.customs-declaration";
    public const string CustomsDeclarationLineEntityKey = "warehouse.customs-declaration-line";

    /// <summary>Gümrük rejimi, beyanname durumu, kap cinsi, menşe ve satır birimi listeleri; hepsinin değer adları tenant'ındır.</summary>
    public const string CustomsRegimeList = "warehouse.customs-regime";
    public const string DeclarationStatusList = "warehouse.declaration-status";
    public const string ContainerTypeList = "warehouse.container-type";
    public const string OriginCountryList = "warehouse.origin-country";
    public const string DeclarationLineUnitList = "warehouse.declaration-line-unit";

    /// <summary>Beyanname durumu kategorileri (kodun anladığı anlam).</summary>
    public const string DeclarationDraft = "draft";
    public const string DeclarationRegistered = "registered";
    public const string DeclarationCancelled = "cancelled";

    /// <summary>Alan türü listesi; kategorileri kodun anladığı anlamdır, adları tenant'ındır.</summary>
    public const string ZoneKindList = "warehouse.zone-kind";

    /// <summary>Stok birimi listesi; birimin adı tenant'ındır, kategorisi yoktur.</summary>
    public const string StockUnitList = "warehouse.stock-unit";

    // Lookup alanları (ADR-0016); seçim kutuları bu anahtarlarla çalışır.
    public const string ZoneFacilityLookup = "warehouse.zone.facility";
    public const string LocationLevelFacilityLookup = "warehouse.location-level.facility";
    public const string LocationFacilityLookup = "warehouse.location.facility";
    public const string LocationParentLookup = "warehouse.location.parent";
    public const string LocationZoneLookup = "warehouse.location.zone";
    public const string AttorneyImporterLookup = "warehouse.power-of-attorney.importer";
    public const string AttorneyBrokerLookup = "warehouse.power-of-attorney.broker";
    public const string DeclarationImporterLookup = "warehouse.customs-declaration.importer";
    public const string DeclarationBrokerLookup = "warehouse.customs-declaration.broker";

    public const string ZoneKindControlled = "controlled";
    public const string ZoneKindFree = "free";
}
