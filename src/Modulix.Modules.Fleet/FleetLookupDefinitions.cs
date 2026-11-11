using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Fleet;

/// <summary>
/// Filo modülünün lookup alanları (ADR-0016): taşeron aracın taşıyıcı firması, bakım kaydının aracı ve servis firması. Seçim "Taşıyıcı" ilişki rolüne göre süzülür (sabit süzgeç);
/// rol denetimi kayıt sırasında ayrıca yapılır (<c>VehicleRecordStore</c>).
/// </summary>
public static class FleetLookupDefinitions
{
    public const string VehicleCarrier = "fleet.vehicle.carrier";
    public const string MaintenanceVehicle = "fleet.maintenance.vehicle";
    public const string MaintenanceSupplier = "fleet.maintenance.supplier";

    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];
    private static readonly string[] VehicleLabel = ["plate"];
    private static readonly string[] VehicleSecondary = ["brand", "model"];

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        new(VehicleCarrier, FleetModule.Entities.Vehicle, "carrierPartyId", "Taşıyıcı firma", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"Taşıyıcı\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = FleetModule.RelationshipRoles.Carrier,
        },
        new(MaintenanceVehicle, FleetModule.Entities.Maintenance, "vehicleId", "Araç", LookupTargetKind.Record, FleetModule.Entities.Vehicle)
        {
            LabelFields = VehicleLabel,
            SecondaryFields = VehicleSecondary,
            SearchDescription = "Plaka, marka, model ve şasi numarası",
        },
        // Servis firması rol süzgeçsizdir: servis/tedarikçi ilişki rolleri katalogda netleşince süzgeç eklenir (ADR-0038 K4).
        new(MaintenanceSupplier, FleetModule.Entities.Maintenance, "supplierPartyId", "Servis firması", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
    ];
}
