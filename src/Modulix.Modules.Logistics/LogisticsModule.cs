using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics;

/// <summary>
/// Lojistik (ADR-0038): sürücü kartı (dilim 3), sonra taşıma talebi, sevkiyat, sefer, teslim ve masraf. Araç kimliği Filo modülündedir
/// (<c>IVehicleDirectory</c>); sürücü <c>core.Person</c> kaydının uzantısıdır.
/// </summary>
public sealed class LogisticsModule : IModule
{
    public const string ModuleKey = "logistics";

    public static class Permissions
    {
        public const string ShipmentsRead = "logistics.shipments.read";
        public const string ShipmentsWrite = "logistics.shipments.write";
        public const string CarriersRead = "logistics.carriers.read";
        public const string CarriersWrite = "logistics.carriers.write";
        public const string CostsRead = "logistics.costs.read";
        public const string CostsWrite = "logistics.costs.write";
        public const string DriversRead = "logistics.drivers.read";
        public const string DriversWrite = "logistics.drivers.write";
        public const string RequestsRead = "logistics.requests.read";
        public const string RequestsWrite = "logistics.requests.write";
        public const string RequestsApprove = "logistics.requests.approve";
        public const string TripsRead = "logistics.trips.read";
        public const string TripsWrite = "logistics.trips.write";
    }

    /// <summary>Seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string TransportMode = "logistics.transport-mode";
    }

    /// <summary>Taşıma türü kategorileri: kod türün adına değil kategorisine bakar (komple: araç tek müşteriye; parsiyel: araç paylaşılır).</summary>
    public static class TransportModeCategories
    {
        public const string Full = "full";
        public const string Partial = "partial";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Lojistik";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ShipmentsRead, "Sevkiyatları görüntüler", "Sevkiyatlar"),
        new(Permissions.ShipmentsWrite, "Sevkiyat ekler ve düzenler", "Sevkiyatlar"),
        new(Permissions.CarriersRead, "Taşıyıcı anlaşmalarını görüntüler", "Taşıyıcılar"),
        new(Permissions.CarriersWrite, "Taşıyıcı anlaşması ekler, düzenler ve siler", "Taşıyıcılar"),
        new(Permissions.CostsRead, "Maliyetleri görüntüler", "Maliyetler"),
        new(Permissions.CostsWrite, "Maliyet ekler ve düzenler", "Maliyetler"),
        new(Permissions.DriversRead, "Sürücü kartlarını görüntüler", "Sürücüler"),
        new(Permissions.DriversWrite, "Sürücü kartı ekler ve düzenler; sürücü için kişi kaydı açar", "Sürücüler"),
        new(Permissions.RequestsRead, "Taşıma taleplerini görüntüler", "Taşıma talepleri"),
        new(Permissions.RequestsWrite, "Taşıma talebi açar, düzenler ve iptal eder", "Taşıma talepleri"),
        new(Permissions.RequestsApprove, "Taşıma talebini onaylar (sevkiyat açılır) ya da reddeder", "Taşıma talepleri"),
        new(Permissions.TripsRead, "Seferleri ve duraklarını görüntüler", "Seferler"),
        new(Permissions.TripsWrite, "Sefer planlar, araç ve sürücü atar, sevkiyat ekler, seferi yürütür ve iptal eder", "Seferler"),
    ];

    /// <summary>Kural altyapısında kullanılan kayıt türleri (ADR-0027).</summary>
    public static class AutomationEntityKeys
    {
        public const string Shipment = Entities.Shipment;
        public const string TransportRequest = Entities.TransportRequest;
        public const string Trip = Entities.Trip;
    }

    /// <summary>
    /// Kural altyapısı yetenekleri (ADR-0027, ADR-0038 dilim 4): taşıma talebi ve sevkiyat kayıt olayları (durum geçişleri güncelleme olayıdır),
    /// zamanlanmış ve tarihe bağlı tarama (ör. "yükleme gününe 1 gün kala hâlâ planlanmamış sevkiyat"). Eski iskelet beyanındaki belge durumu ve
    /// kapasite alanları kayıtta olmadığı için kaldırıldı; belge ve kapasite uygunluğu sefer atamasında gelir (dilim 5). Araç beyanı Filo'dadır.
    /// </summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(AutomationEntityKeys.TransportRequest, "Taşıma talebi", Permissions.RequestsRead)
            {
                RecordSource = typeof(LogisticsAutomationRecordSource), LabelField = "cargoDescription",
            },
            new(AutomationEntityKeys.Shipment, "Sevkiyat", Permissions.ShipmentsRead)
            {
                RecordSource = typeof(LogisticsAutomationRecordSource), LabelField = "cargoDescription",
            },
            new(AutomationEntityKeys.Trip, "Sefer", Permissions.TripsRead)
            {
                RecordSource = typeof(LogisticsAutomationRecordSource), LabelField = "number",
            },
        ],
        Events =
        [
            new("logistics.transport-request.created", AutomationEntityKeys.TransportRequest, DomainEventTypes.RecordCreated, "Taşıma talebi açıldı"),
            new("logistics.transport-request.updated", AutomationEntityKeys.TransportRequest, DomainEventTypes.RecordUpdated, "Taşıma talebi güncellendi"),
            new("logistics.shipment.created", AutomationEntityKeys.Shipment, DomainEventTypes.RecordCreated, "Sevkiyat oluşturuldu"),
            new("logistics.shipment.updated", AutomationEntityKeys.Shipment, DomainEventTypes.RecordUpdated, "Sevkiyat güncellendi"),
            new("logistics.trip.created", AutomationEntityKeys.Trip, DomainEventTypes.RecordCreated, "Sefer açıldı"),
            new("logistics.trip.updated", AutomationEntityKeys.Trip, DomainEventTypes.RecordUpdated, "Sefer güncellendi"),
        ],
        Fields = [.. CargoAutomationFields(AutomationEntityKeys.TransportRequest, Domain.TransportRequest.Statuses.All, RequestStatusLabels),
            .. CargoAutomationFields(AutomationEntityKeys.Shipment, Domain.Shipment.Statuses.All, ShipmentStatusLabels),
            new(AutomationEntityKeys.Trip, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [.. Domain.Trip.Statuses.All.Select(s => new AutomationOption(s, TripStatusLabels[s]))],
            },
            new(AutomationEntityKeys.Trip, "number", "Sefer no", AutomationFieldType.Text),
            new(AutomationEntityKeys.Trip, "plannedStart", "Planlanan başlangıç", AutomationFieldType.Date)
            {
                Hint = "\"Sefere 1 gün kala hâlâ hazır olmayan sefer\" gibi uyarılar için tarihe bağlı tetikleyicide kullanın.",
            },
            new(AutomationEntityKeys.Trip, "plannedEnd", "Planlanan bitiş", AutomationFieldType.Date),
            new(AutomationEntityKeys.Trip, "vehicleId", "Araç (kimlik)", AutomationFieldType.Text),
            new(AutomationEntityKeys.Trip, "driverId", "Sürücü (kimlik)", AutomationFieldType.Text)],
    };

    public static IReadOnlyDictionary<string, string> TripStatusLabels { get; } = new Dictionary<string, string>
    {
        [Domain.Trip.Statuses.Planning] = "Planlanıyor",
        [Domain.Trip.Statuses.Ready] = "Hazır",
        [Domain.Trip.Statuses.InProgress] = "Yolda",
        [Domain.Trip.Statuses.Completed] = "Tamamlandı",
        [Domain.Trip.Statuses.Cancelled] = "İptal edildi",
    };

    public static IReadOnlyDictionary<string, string> StopTypeLabels { get; } = new Dictionary<string, string>
    {
        [Domain.TripStop.Types.Loading] = "Yükleme",
        [Domain.TripStop.Types.Unloading] = "Boşaltma",
        [Domain.TripStop.Types.Border] = "Sınır / gümrük",
        [Domain.TripStop.Types.Rest] = "Mola",
        [Domain.TripStop.Types.EmptyReturn] = "Boş dönüş",
    };

    /// <summary>Firma ayarları (Y6): sefer uygunluğunda süreli belge kuralı (ADR-0038 bölüm 9 madde 3: varsayılan uyarı, ayarla engel).</summary>
    public static class SettingKeys
    {
        public const string CredentialCheck = "logistics.trips.credential-check";
    }

    /// <summary>Belge kuralı değerleri: uyar (onay uyarıyla verilir) ya da engelle (onay verilmez).</summary>
    public static class CredentialCheckModes
    {
        public const string Warn = "warn";
        public const string Block = "block";
    }

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.CredentialCheck, "Sefer: süresi dolan belge", "Lojistik", SettingType.Choice, CredentialCheckModes.Warn)
        {
            Description = "Seferi hazır yaparken araç, dorse ya da sürücü belgesinin (muayene, sigorta, ehliyet, SRC…) süresi doluyorsa ne olsun? Uyar: onay uyarıyla verilir ve uyarı seferde saklanır. Engelle: belge yenilenmeden sefer hazır olmaz.",
            Options = [new SettingOption(CredentialCheckModes.Warn, "Uyar"), new SettingOption(CredentialCheckModes.Block, "Engelle")],
        },
    ];

    /// <summary>Durum anahtarı → görünen ad (kural editörü, denetim kaydı, arayüz).</summary>
    public static IReadOnlyDictionary<string, string> RequestStatusLabels { get; } = new Dictionary<string, string>
    {
        [Domain.TransportRequest.Statuses.New] = "Yeni",
        [Domain.TransportRequest.Statuses.Approved] = "Onaylandı",
        [Domain.TransportRequest.Statuses.Rejected] = "Reddedildi",
        [Domain.TransportRequest.Statuses.Cancelled] = "İptal edildi",
    };

    public static IReadOnlyDictionary<string, string> ShipmentStatusLabels { get; } = new Dictionary<string, string>
    {
        [Domain.Shipment.Statuses.Pending] = "Planlama bekliyor",
        [Domain.Shipment.Statuses.Planned] = "Planlandı",
        [Domain.Shipment.Statuses.Loading] = "Yükleniyor",
        [Domain.Shipment.Statuses.InTransit] = "Yolda",
        [Domain.Shipment.Statuses.Delivered] = "Teslim edildi",
        [Domain.Shipment.Statuses.Partial] = "Kısmi teslim",
        [Domain.Shipment.Statuses.Failed] = "Teslim edilemedi",
        [Domain.Shipment.Statuses.Cancelled] = "İptal edildi",
    };

    private static IEnumerable<AutomationFieldDefinition> CargoAutomationFields(string entity, IReadOnlyList<string> statuses, IReadOnlyDictionary<string, string> labels) =>
    [
        new(entity, "status", "Durum", AutomationFieldType.Choice) { Options = [.. statuses.Select(s => new AutomationOption(s, labels[s]))] },
        new(entity, "cargoDescription", "Yük tanımı", AutomationFieldType.Text),
        new(entity, "transportModeKey", "Taşıma türü (anahtar)", AutomationFieldType.Text),
        new(entity, "pickupCity", "Yükleme şehri", AutomationFieldType.Text),
        new(entity, "deliveryCity", "Teslim şehri", AutomationFieldType.Text),
        new(entity, "pickupDate", "Yükleme günü", AutomationFieldType.Date) { Hint = "\"Yükleme gününe 1 gün kala\" gibi uyarılar için tarihe bağlı tetikleyicide kullanın." },
        new(entity, "deliveryBy", "En geç teslim", AutomationFieldType.Date),
        new(entity, "weightKg", "Ağırlık (kg)", AutomationFieldType.Number),
        new(entity, "hazardous", "Tehlikeli madde (ADR)", AutomationFieldType.Boolean),
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "logistics.operations",
            "Operasyon",
            "Taşıma taleplerini açar; sevkiyatları, seferleri, taşıyıcıları ve sürücü kartlarını görüntüler ve düzenler; maliyetleri görüntüler. Talep onaylamaz.",
            [
                Permissions.RequestsRead, Permissions.RequestsWrite,
                Permissions.ShipmentsRead, Permissions.ShipmentsWrite,
                Permissions.TripsRead, Permissions.TripsWrite,
                Permissions.CarriersRead, Permissions.CarriersWrite,
                Permissions.CostsRead,
                Permissions.DriversRead, Permissions.DriversWrite,
            ]),
        new(
            "logistics.manager",
            "Operasyon müdürü",
            "Operasyonun bütün işleri ve taşıma talebinin onayı ya da reddi.",
            [
                Permissions.RequestsRead, Permissions.RequestsWrite, Permissions.RequestsApprove,
                Permissions.ShipmentsRead, Permissions.ShipmentsWrite,
                Permissions.TripsRead, Permissions.TripsWrite,
                Permissions.CarriersRead, Permissions.CarriersWrite,
                Permissions.CostsRead, Permissions.CostsWrite,
                Permissions.DriversRead, Permissions.DriversWrite,
            ]),
    ];

    public static class Entities
    {
        public const string Driver = "logistics.driver";
        public const string CarrierTerms = "logistics.carrier-terms";
        public const string TransportRequest = "logistics.transport-request";
        public const string Shipment = "logistics.shipment";
        public const string Trip = "logistics.trip";
        public const string TripStop = "logistics.trip-stop";
        public const string DeliveryProof = "logistics.delivery-proof";
    }

    /// <summary>
    /// Taşıma türü (Y9b): hazır değerler örnektir; tenant kendi türlerini ekler, yeni türde kategori seçilir (komple / parsiyel). Kod türün adına
    /// değil kategorisine bakar (ör. sefer planlamasında parsiyel yükler birleştirilir, dilim 5).
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.TransportMode,
            "Taşıma türü",
            Entities.TransportRequest,
            "transportModeKey",
            Required: true,
            [new(TransportModeCategories.Full, "Komple (araç tek müşteriye)"), new(TransportModeCategories.Partial, "Parsiyel (araç paylaşılır)")],
            [
                new("Ftl", "Komple (FTL)", TransportModeCategories.Full),
                new("Express", "Ekspres (özel araç)", TransportModeCategories.Full),
                new("Ltl", "Parsiyel (LTL)", TransportModeCategories.Partial),
                new("Groupage", "Grupaj", TransportModeCategories.Partial),
            ])
        {
            Default = "Ftl",
        },
    ];

    /// <summary>Platform ilişki rolleri (ADR-0029 K9): taşeron sürücünün çalıştığı firma bu roldedir.</summary>
    public static class RelationshipRoles
    {
        public const string Carrier = "tasiyici";
    }

    /// <summary>Filo araç türü kategorisi (Filo modülünün <c>VehicleKindCategories.Towed</c> değeri; sözleşme metnidir, proje başvurusu değil).</summary>
    public const string VehicleKindTowed = "towed";

    /// <summary>İş olayı türleri ve payload alanları (ADR-0039, ADR-0038 K8); yayıncı aynı listeleri kullanır.</summary>
    public static class Events
    {
        public const string ShipmentDelivered = "logistics.shipment.delivered";
        public const string ShipmentDeliveryFailed = "logistics.shipment.delivery-failed";

        /// <summary>Sevkiyat kaydının alanları (komutun döndürdüğü kayıttan alınır).</summary>
        public static IReadOnlyList<string> ShipmentFields { get; } =
            ["customerPartyId", "senderPartyId", "receiverPartyId", "tripId", "transportModeKey", "deliveryCity", "weightKg", "volumeM3", "palletCount", "status"];

        /// <summary>Teslim ve kısmi teslim: sevkiyat alanları + teslim kanıtından türetilen alanlar (kayıtta karşılığı yok, hassas sayılmaz).</summary>
        public static IReadOnlyList<string> ShipmentDeliveredFields { get; } =
            [.. ShipmentFields, "proofId", "outcome", "receivedAt", "receivedBy", "damaged", "shortage"];

        public static IReadOnlyList<string> ShipmentDeliveryFailedFields { get; } =
            [.. ShipmentFields, "proofId", "outcome", "receivedAt", "failureReason"];
    }

    /// <summary>
    /// İş olayı beyanları (ADR-0039): teslim ve kısmi teslim ADR-0037 "teslim → faturalanabilir kalem" köprüsünün ve kuralların tetikleyicisidir;
    /// başarısız teslim ayrı olaydır (faturalanabilir kalem üretmez).
    /// </summary>
    IReadOnlyList<BusinessEventDefinition> IModule.BusinessEvents { get; } =
    [
        new(Events.ShipmentDelivered, 1, Entities.Shipment, "Sevkiyat teslim edildi", Events.ShipmentDeliveredFields),
        new(Events.ShipmentDeliveryFailed, 1, Entities.Shipment, "Sevkiyat teslim edilemedi", Events.ShipmentDeliveryFailedFields),
    ];

    /// <summary>Denetim kaydında görünen kayıt türleri (Y5).</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Driver, "Sürücü", Permissions.DriversRead, new Dictionary<string, string>
        {
            ["personId"] = "Kişi", ["driverType"] = "Sürücü tipi", ["carrierPartyId"] = "Taşıyıcı firma",
            ["defaultVehicleId"] = "Varsayılan araç", ["status"] = "Durum", ["notes"] = "Notlar",
        }, "personId")
        {
            References = new Dictionary<string, string> { ["personId"] = SharedEntities.Person, ["carrierPartyId"] = SharedEntities.Party },
        },
        new(Entities.CarrierTerms, "Taşıyıcı anlaşması", Permissions.CarriersRead, new Dictionary<string, string>
        {
            ["carrierPartyId"] = "Taşıyıcı firma", ["title"] = "Anlaşma adı", ["agreementNo"] = "Anlaşma no", ["validFrom"] = "Başlangıç",
            ["validTo"] = "Bitiş", ["currency"] = "Para birimi", ["paymentTermDays"] = "Ödeme vadesi (gün)", ["rateNote"] = "Ücret anlaşması",
            ["performanceNote"] = "Performans notu", ["isActive"] = "Etkin",
        }, "title")
        {
            References = new Dictionary<string, string> { ["carrierPartyId"] = SharedEntities.Party },
        },
        CargoAudit(Entities.TransportRequest, "Taşıma talebi", Permissions.RequestsRead, RequestStatusLabels,
            new Dictionary<string, string> { ["source"] = "Kaynak", ["externalRef"] = "Dış referans", ["shipmentId"] = "Sevkiyat" }),
        CargoAudit(Entities.Shipment, "Sevkiyat", Permissions.ShipmentsRead, ShipmentStatusLabels,
            new Dictionary<string, string> { ["requestId"] = "Taşıma talebi", ["tripId"] = "Sefer" }),
        new(Entities.Trip, "Sefer", Permissions.TripsRead, new Dictionary<string, string>
        {
            ["number"] = "Sefer no", ["plannedStart"] = "Planlanan başlangıç", ["plannedEnd"] = "Planlanan bitiş", ["vehicleId"] = "Araç",
            ["trailerId"] = "Dorse", ["driverId"] = "Sürücü", ["coDriverId"] = "İkinci sürücü", ["carrierPartyId"] = "Taşıyıcı firma",
            ["notes"] = "Notlar", ["status"] = "Durum", ["statusReason"] = "Not / gerekçe",
        }, "number")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["status"] = TripStatusLabels },
            References = new Dictionary<string, string> { ["carrierPartyId"] = SharedEntities.Party },
        },
        new(Entities.TripStop, "Sefer durağı", Permissions.TripsRead, new Dictionary<string, string>
        {
            ["tripId"] = "Sefer", ["sequence"] = "Sıra", ["stopType"] = "Tür", ["city"] = "Şehir", ["address"] = "Adres",
            ["plannedAt"] = "Planlanan varış", ["arrivedAt"] = "Gerçekleşen varış", ["notes"] = "Notlar", ["shipmentId"] = "Sevkiyat",
        }, "city")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["stopType"] = StopTypeLabels },
        },
        new(Entities.DeliveryProof, "Teslim kanıtı", Permissions.ShipmentsRead, new Dictionary<string, string>
        {
            ["shipmentId"] = "Sevkiyat", ["outcome"] = "Sonuç", ["receivedBy"] = "Teslim alan", ["receivedAt"] = "Teslim zamanı",
            ["damaged"] = "Hasarlı", ["shortage"] = "Eksik", ["failureReason"] = "Başarısız teslim nedeni", ["notes"] = "Notlar",
        }, "receivedBy")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["outcome"] = DeliveryOutcomeLabels },
        },
    ];

    public static IReadOnlyDictionary<string, string> DeliveryOutcomeLabels { get; } = new Dictionary<string, string>
    {
        [Domain.DeliveryProof.Outcomes.Delivered] = "Teslim edildi",
        [Domain.DeliveryProof.Outcomes.Partial] = "Kısmi teslim",
        [Domain.DeliveryProof.Outcomes.Failed] = "Teslim edilemedi",
    };

    private static AuditedEntity CargoAudit(
        string entity, string name, string permission, IReadOnlyDictionary<string, string> statuses, IReadOnlyDictionary<string, string> extra) =>
        new(entity, name, permission, new Dictionary<string, string>(extra)
        {
            ["customerPartyId"] = "Müşteri", ["senderPartyId"] = "Gönderen", ["receiverPartyId"] = "Alıcı", ["transportModeKey"] = "Taşıma türü",
            ["pickupCity"] = "Yükleme şehri", ["pickupAddress"] = "Yükleme adresi", ["deliveryCity"] = "Teslim şehri", ["deliveryAddress"] = "Teslim adresi",
            ["pickupDate"] = "Yükleme günü", ["deliveryBy"] = "En geç teslim", ["cargoDescription"] = "Yük tanımı", ["weightKg"] = "Ağırlık (kg)",
            ["volumeM3"] = "Hacim (m³)", ["palletCount"] = "Palet", ["loadingMeters"] = "Yükleme metresi (LDM)", ["hazardous"] = "Tehlikeli madde",
            ["notes"] = "Notlar", ["status"] = "Durum", ["statusReason"] = "Gerekçe",
        }, "cargoDescription")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["status"] = statuses },
            References = new Dictionary<string, string>
            {
                ["customerPartyId"] = SharedEntities.Party, ["senderPartyId"] = SharedEntities.Party, ["receiverPartyId"] = SharedEntities.Party,
            },
        };

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Driver, "Sürücü"),
        new(Entities.CarrierTerms, "Taşıyıcı anlaşması"),
        new(Entities.TransportRequest, "Taşıma talebi"),
        new(Entities.Shipment, "Sevkiyat"),
        new(Entities.Trip, "Sefer"),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => LogisticsLookupDefinitions.All;

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): sürücü bir kişidir; sürücü yazma izni olan, sürücü için kişi kaydı da açabilir (ad, telefon). Taşeron
    /// sürücünün taşıyıcısı firmadır, okunur. Lojistik firma oluşturmaz.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Person, Permissions.DriversRead, Permissions.DriversWrite),
        new(SharedEntities.Party, Permissions.DriversRead),
        new(SharedEntities.Party, Permissions.CarriersRead),
        new(SharedEntities.Party, Permissions.RequestsRead),
        new(SharedEntities.Party, Permissions.ShipmentsRead),
        new(SharedEntities.Party, Permissions.TripsRead),
    ];

    /// <summary>Kişinin ve firmanın nerede kullanıldığı (silme ve birleştirme koruması).</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Person, Entities.Driver, "personId", "sürücü kartı"),
        new(SharedEntities.Party, Entities.Driver, "carrierPartyId", "sürücü (taşıyıcı)"),
        new(SharedEntities.Party, Entities.CarrierTerms, "carrierPartyId", "taşıyıcı anlaşması"),
        new(SharedEntities.Party, Entities.TransportRequest, "customerPartyId", "taşıma talebi (müşteri)"),
        new(SharedEntities.Party, Entities.TransportRequest, "senderPartyId", "taşıma talebi (gönderen)"),
        new(SharedEntities.Party, Entities.TransportRequest, "receiverPartyId", "taşıma talebi (alıcı)"),
        new(SharedEntities.Party, Entities.Shipment, "customerPartyId", "sevkiyat (müşteri)"),
        new(SharedEntities.Party, Entities.Shipment, "senderPartyId", "sevkiyat (gönderen)"),
        new(SharedEntities.Party, Entities.Shipment, "receiverPartyId", "sevkiyat (alıcı)"),
        new(SharedEntities.Party, Entities.Trip, "carrierPartyId", "sefer (taşıyıcı)"),
    ];

    /// <summary>Sürücü yetkinlikleri (ehliyet, SRC, psikoteknik...) Core süreli belgesidir (ADR-0038 K2, K3); sürücü kartının "Belgeler" bölümünde yönetilir.</summary>
    IReadOnlyList<CredentialOwner> IModule.CredentialOwners { get; } =
    [
        new(Entities.Driver, CredentialCategories.Driver, "Sürücü", Permissions.DriversRead),
    ];

    /// <summary>Sürücü formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => LogisticsForms.All;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<LogisticsWorkflowGate>();
        services.AddScoped<LogisticsStoreContext>();
        services.AddScoped<IRecordStore, DriverRecordStore>();
        services.AddScoped<IRecordStore, CarrierTermsRecordStore>();
        services.AddScoped<IRecordStore, TransportRequestRecordStore>();
        services.AddScoped<IRecordStore, ShipmentRecordStore>();
        services.AddScoped<IRecordStore, TripRecordStore>();
        services.AddScoped<IRecordStore, TripStopRecordStore>();
        services.AddScoped<IRecordStore, DeliveryProofRecordStore>();
        services.AddScoped<Lifecycle.IShipmentDelivery, Lifecycle.ShipmentDelivery>();
        services.AddScoped<Lifecycle.ITripPlanner, Lifecycle.TripPlanner>();
        services.AddScoped<Lifecycle.ILogisticsWorkflow, Lifecycle.LogisticsWorkflow>();
        services.AddScoped<LogisticsAutomationRecordSource>();
    }
}
