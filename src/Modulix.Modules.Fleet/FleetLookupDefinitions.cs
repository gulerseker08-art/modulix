using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Fleet;

/// <summary>
/// Filo modülünün lookup alanları (ADR-0016): taşeron aracın taşıyıcı firması. Seçim "Taşıyıcı" ilişki rolüne göre süzülür (sabit süzgeç);
/// rol denetimi kayıt sırasında ayrıca yapılır (<c>VehicleRecordStore</c>).
/// </summary>
public static class FleetLookupDefinitions
{
    public const string VehicleCarrier = "fleet.vehicle.carrier";

    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];

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
    ];
}
