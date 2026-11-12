using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Logistics;

/// <summary>
/// Lojistik modülünün lookup alanları (ADR-0016): sürücünün kişisi, taşeron sürücünün taşıyıcı firması ("Taşıyıcı" rolüyle süzülür) ve
/// varsayılan aracı (Filo'nun araç kaydı; erişim kayıt boru hattının kurallarıyla, Filo kapalıysa araç aranamaz).
/// </summary>
public static class LogisticsLookupDefinitions
{
    public const string DriverPerson = "logistics.driver.person";
    public const string DriverCarrier = "logistics.driver.carrier";
    public const string DriverVehicle = "logistics.driver.vehicle";

    /// <summary>Filo'nun araç kayıt türü (sözleşme metni; Filo projesine başvurulmaz).</summary>
    public const string VehicleEntityKey = "fleet.vehicle";

    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["phone", "email"];
    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];
    private static readonly string[] VehicleLabel = ["plate"];
    private static readonly string[] VehicleSecondary = ["brand", "model"];

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        new(DriverPerson, LogisticsModule.Entities.Driver, "personId", "Kişi", LookupTargetKind.Record, SharedEntities.Person)
        {
            LabelFields = PersonLabel,
            SecondaryFields = PersonSecondary,
            SearchDescription = "Ad, soyad ve e-posta",
        },
        new(DriverCarrier, LogisticsModule.Entities.Driver, "carrierPartyId", "Taşıyıcı firma", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir (yalnızca \"Taşıyıcı\" rolündeki firmalar)",
            FixedFilterName = "relationshipRole",
            FixedFilterValue = LogisticsModule.RelationshipRoles.Carrier,
        },
        new(DriverVehicle, LogisticsModule.Entities.Driver, "defaultVehicleId", "Varsayılan araç", LookupTargetKind.Record, VehicleEntityKey)
        {
            LabelFields = VehicleLabel,
            SecondaryFields = VehicleSecondary,
            SearchDescription = "Plaka, marka, model ve şasi numarası (Filo modülü)",
        },
    ];
}
