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
    ];

    /// <summary>Kural altyapısında kullanılan kayıt türleri (ADR-0027).</summary>
    public static class AutomationEntityKeys
    {
        public const string Shipment = "logistics.shipment";
    }

    /// <summary>
    /// Kural altyapısı yetenekleri (ADR-0027): belge eksikliği ve kapasite eşiği kuralları CRM olmadan, aynı sözleşmeyle
    /// yazılabilir. Lojistik henüz iskelet modüldür (sevkiyat kayıt deposu yok); bu beyan yeteneğin Core'a modül koduna
    /// dokunmadan eklenebildiğini gösterir ve kayıt depoları gelince kural çalıştırmak ek Core değişikliği gerektirmez. Aksiyon
    /// beyan edilmez: lojistik aksiyonları kayıt servisleriyle birlikte gelir. Araç beyanı Filo modülüne taşındı (`fleet.vehicle`, ADR-0038 K1).
    /// </summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(AutomationEntityKeys.Shipment, "Sevkiyat", Permissions.ShipmentsRead),
        ],
        Events =
        [
            new("logistics.shipment.created", AutomationEntityKeys.Shipment, DomainEventTypes.RecordCreated, "Sevkiyat oluşturuldu"),
            new("logistics.shipment.updated", AutomationEntityKeys.Shipment, DomainEventTypes.RecordUpdated, "Sevkiyat güncellendi"),
        ],
        Fields =
        [
            new(AutomationEntityKeys.Shipment, "documentStatus", "Belge durumu", AutomationFieldType.Choice)
            {
                Options = [new("complete", "Tam"), new("missing", "Eksik")],
                Hint = "Eksik belgesi olan sevkiyatı yakalamak için.",
            },
            new(AutomationEntityKeys.Shipment, "capacityUsagePercent", "Kapasite kullanımı (%)", AutomationFieldType.Number)
            {
                Hint = "Kapasite eşiği kuralları için.",
            },
        ],
    };

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "logistics.operations",
            "Operasyon",
            "Sevkiyatları, taşıyıcıları ve sürücü kartlarını görüntüler ve düzenler; maliyetleri görüntüler.",
            [
                Permissions.ShipmentsRead, Permissions.ShipmentsWrite,
                Permissions.CarriersRead, Permissions.CarriersWrite,
                Permissions.CostsRead,
                Permissions.DriversRead, Permissions.DriversWrite,
            ]),
    ];

    public static class Entities
    {
        public const string Driver = "logistics.driver";
        public const string CarrierTerms = "logistics.carrier-terms";
    }

    /// <summary>Platform ilişki rolleri (ADR-0029 K9): taşeron sürücünün çalıştığı firma bu roldedir.</summary>
    public static class RelationshipRoles
    {
        public const string Carrier = "tasiyici";
    }

    /// <summary>Filo araç türü kategorisi (Filo modülünün <c>VehicleKindCategories.Towed</c> değeri; sözleşme metnidir, proje başvurusu değil).</summary>
    public const string VehicleKindTowed = "towed";

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
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Driver, "Sürücü"),
        new(Entities.CarrierTerms, "Taşıyıcı anlaşması"),
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
    ];

    /// <summary>Kişinin ve firmanın nerede kullanıldığı (silme ve birleştirme koruması).</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Person, Entities.Driver, "personId", "sürücü kartı"),
        new(SharedEntities.Party, Entities.Driver, "carrierPartyId", "sürücü (taşıyıcı)"),
        new(SharedEntities.Party, Entities.CarrierTerms, "carrierPartyId", "taşıyıcı anlaşması"),
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
        services.AddScoped<LogisticsStoreContext>();
        services.AddScoped<IRecordStore, DriverRecordStore>();
        services.AddScoped<IRecordStore, CarrierTermsRecordStore>();
    }
}
