using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo modülünün lookup alanları (ADR-0016): tesis, alan ve üst lokasyon seçimi. Hedef kayıtlara erişim kayıt boru hattının kurallarıyla
/// (izin, kapsam) belirlenir; "aranabilir" bir erişim yetkisi değildir. Alan ve üst lokasyon seçimi tesise bağlıdır: tesis seçiliyse
/// sonuçlar o tesisle sınırlanır ve ilişki kayıt sırasında da doğrulanır.
/// </summary>
public static class WarehouseLookupDefinitions
{
    public const string ZoneFacility = "warehouse.zone.facility";
    public const string LocationLevelFacility = "warehouse.location-level.facility";
    public const string LocationFacility = "warehouse.location.facility";
    public const string LocationParent = "warehouse.location.parent";
    public const string LocationZone = "warehouse.location.zone";

    private static readonly string[] FacilityLabel = ["code", "name"];
    private static readonly string[] ZoneLabel = ["code", "name"];
    private static readonly string[] LocationLabel = ["code"];
    private static readonly string[] LocationSecondary = ["name"];

    private static LookupDefinition Facility(string key, string source, string title) =>
        new(key, source, "facilityId", title, LookupTargetKind.Record, WarehouseModule.Entities.Facility)
        {
            LabelFields = FacilityLabel,
            SearchDescription = "Tesis kodu ve adı",
        };

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        Facility(ZoneFacility, WarehouseModule.Entities.Zone, "Tesis"),
        Facility(LocationLevelFacility, WarehouseModule.Entities.LocationLevel, "Tesis"),
        Facility(LocationFacility, WarehouseModule.Entities.Location, "Tesis"),
        new(LocationParent, WarehouseModule.Entities.Location, "parentId", "Üst lokasyon", LookupTargetKind.Record, WarehouseModule.Entities.Location)
        {
            LabelFields = LocationLabel,
            SecondaryFields = LocationSecondary,
            SearchDescription = "Lokasyon kodu ve adı",
            DependsOnField = "facilityId",
            DependsOnFilter = "facilityId",
            DependsOnHint = "Önce tesisi seçin; üst lokasyon o tesisin lokasyonlarıyla sınırlanır.",
        },
        new(LocationZone, WarehouseModule.Entities.Location, "zoneId", "Alan", LookupTargetKind.Record, WarehouseModule.Entities.Zone)
        {
            LabelFields = ZoneLabel,
            SearchDescription = "Alan kodu ve adı",
            DependsOnField = "facilityId",
            DependsOnFilter = "facilityId",
            DependsOnHint = "Önce tesisi seçin; alan o tesisin alanlarıyla sınırlanır.",
        },
    ];
}
