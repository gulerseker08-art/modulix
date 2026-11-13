using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo modülünün lookup alanları (ADR-0016): tesis, alan, üst lokasyon ve vekâletin tarafları (ithalatçı, gümrük müşaviri). Hedef kayıtlara erişim kayıt boru hattının kurallarıyla
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
    public const string AttorneyImporter = "warehouse.power-of-attorney.importer";
    public const string AttorneyBroker = "warehouse.power-of-attorney.broker";
    public const string DeclarationImporter = "warehouse.customs-declaration.importer";
    public const string DeclarationBroker = "warehouse.customs-declaration.broker";
    public const string LotStockItem = "warehouse.lot.stock-item";
    public const string LotOwner = "warehouse.lot.owner";
    public const string LotBillTo = "warehouse.lot.bill-to";
    public const string MovementLot = "warehouse.stock-movement.lot";
    public const string MovementFromLocation = "warehouse.stock-movement.from-location";
    public const string MovementToLocation = "warehouse.stock-movement.to-location";

    private static readonly string[] FacilityLabel = ["code", "name"];
    private static readonly string[] StockItemLabel = ["code", "name"];
    private static readonly string[] LotLabel = ["lotNo"];
    private static readonly string[] LotSecondary = ["description"];
    private static readonly string[] ZoneLabel = ["code", "name"];
    private static readonly string[] LocationLabel = ["code"];
    private static readonly string[] LocationSecondary = ["name"];
    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];

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
        // Firma seçimi ilişki rolüne göre süzülür (sabit süzgeç); rol denetimi kayıt sırasında ayrıca yapılır (PowerOfAttorneyRecordStore).
        new(AttorneyImporter, WarehouseModule.Entities.PowerOfAttorney, "importerPartyId", "İthalatçı", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"İthalatçı / malik\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = WarehouseModule.RelationshipRoles.Importer,
        },
        new(AttorneyBroker, WarehouseModule.Entities.PowerOfAttorney, "brokerPartyId", "Gümrük müşaviri", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"Gümrük müşaviri\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = WarehouseModule.RelationshipRoles.Broker,
        },
        new(DeclarationImporter, WarehouseModule.Entities.CustomsDeclaration, "importerPartyId", "İthalatçı", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"İthalatçı / malik\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = WarehouseModule.RelationshipRoles.Importer,
        },
        new(DeclarationBroker, WarehouseModule.Entities.CustomsDeclaration, "brokerPartyId", "Gümrük müşaviri", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"Gümrük müşaviri\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = WarehouseModule.RelationshipRoles.Broker,
        },
        // Parti (dilim 6b ekranları): stok kartı, malik ve faturalanan taraf. Malik herhangi bir firma olabilir (rol denetimi yok, LotRecordStore).
        new(LotStockItem, WarehouseModule.Entities.Lot, "stockItemId", "Stok kartı", LookupTargetKind.Record, WarehouseModule.Entities.StockItem)
        {
            LabelFields = StockItemLabel,
            SearchDescription = "Stok kodu, adı ve GTİP",
        },
        new(LotOwner, WarehouseModule.Entities.Lot, "ownerPartyId", "Malik", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
        new(LotBillTo, WarehouseModule.Entities.Lot, "billToPartyId", "Faturalanan taraf", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
        // Stok hareketi: parti ve kaynak/hedef lokasyon. Hareketin tesisi yoktur; lokasyon seçimi tesise bağlanmaz.
        new(MovementLot, WarehouseModule.Entities.StockMovement, "lotId", "Parti", LookupTargetKind.Record, WarehouseModule.Entities.Lot)
        {
            LabelFields = LotLabel,
            SecondaryFields = LotSecondary,
            SearchDescription = "Parti numarası ve açıklaması",
        },
        new(MovementFromLocation, WarehouseModule.Entities.StockMovement, "fromLocationId", "Kaynak lokasyon", LookupTargetKind.Record, WarehouseModule.Entities.Location)
        {
            LabelFields = LocationLabel,
            SecondaryFields = LocationSecondary,
            SearchDescription = "Lokasyon kodu ve adı",
        },
        new(MovementToLocation, WarehouseModule.Entities.StockMovement, "toLocationId", "Hedef lokasyon", LookupTargetKind.Record, WarehouseModule.Entities.Location)
        {
            LabelFields = LocationLabel,
            SecondaryFields = LocationSecondary,
            SearchDescription = "Lokasyon kodu ve adı",
        },
    ];
}
