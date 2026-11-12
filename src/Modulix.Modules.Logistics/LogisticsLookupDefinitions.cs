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
    public const string TermsCarrier = "logistics.carrier-terms.carrier";

    /// <summary>Filo'nun araç kayıt türü (sözleşme metni; Filo projesine başvurulmaz).</summary>
    public const string VehicleEntityKey = "fleet.vehicle";

    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["phone", "email"];
    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];
    private static readonly string[] VehicleLabel = ["plate"];
    private static readonly string[] VehicleSecondary = ["brand", "model"];

    public const string RequestCustomer = "logistics.transport-request.customer";
    public const string RequestSender = "logistics.transport-request.sender";
    public const string RequestReceiver = "logistics.transport-request.receiver";
    public const string ShipmentCustomer = "logistics.shipment.customer";
    public const string ShipmentSender = "logistics.shipment.sender";
    public const string ShipmentReceiver = "logistics.shipment.receiver";

    /// <summary>Talep ve sevkiyatın taraf lookup'ları: müşteri, gönderen, alıcı firma (rol süzgeci yok; her firma taraf olabilir).</summary>
    private static IEnumerable<LookupDefinition> CargoParties(string entity, string customer, string sender, string receiver) =>
    [
        Party(customer, entity, "customerPartyId", "Müşteri"),
        Party(sender, entity, "senderPartyId", "Gönderen"),
        Party(receiver, entity, "receiverPartyId", "Alıcı"),
    ];

    private static LookupDefinition Party(string key, string entity, string field, string title) =>
        new(key, entity, field, title, LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        };

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        .. CargoParties(LogisticsModule.Entities.TransportRequest, RequestCustomer, RequestSender, RequestReceiver),
        .. CargoParties(LogisticsModule.Entities.Shipment, ShipmentCustomer, ShipmentSender, ShipmentReceiver),
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
        new(TermsCarrier, LogisticsModule.Entities.CarrierTerms, "carrierPartyId", "Taşıyıcı firma", LookupTargetKind.Record, SharedEntities.Party)
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
