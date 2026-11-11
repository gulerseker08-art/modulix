using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Fleet;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Fleet.Records;

namespace Modulix.Modules.Fleet;

/// <summary>
/// Filo: araç kimliği (ADR-0038 K1). Lojistik ve araç kiralama aynı aracı kullanır; araç, dorse/römork (çekilen araç türü), mülkiyet ve
/// kapasite burada tutulur, diğer modüller <see cref="IVehicleDirectory"/> ile okur. Dilim 1a: araç kaydı; bakım kaydı ve planı dilim 1b,
/// araç belgeleri Core süreli belgesiyle (dilim 2) gelir.
/// </summary>
public sealed class FleetModule : IModule
{
    public const string ModuleKey = "fleet";

    public static class Permissions
    {
        public const string VehiclesRead = "fleet.vehicles.read";
        public const string VehiclesWrite = "fleet.vehicles.write";
        public const string MaintenanceRead = "fleet.maintenance.read";
        public const string MaintenanceWrite = "fleet.maintenance.write";
    }

    public static class Entities
    {
        public const string Vehicle = "fleet.vehicle";
        public const string Maintenance = "fleet.maintenance";
    }

    public static class Lists
    {
        public const string VehicleKind = "fleet.vehicle-kind";
        public const string MaintenanceKind = "fleet.maintenance-kind";
    }

    /// <summary>Araç türü kategorileri: kod türün adına değil kategorisine bakar (ör. dorse kapasite taşır ama sürücü atanmaz).</summary>
    public static class VehicleKindCategories
    {
        public const string Powered = "powered";
        public const string Towed = "towed";
    }

    /// <summary>Bakım türü kategorileri: planlı (periyodik, lastik) ve plansız (arıza onarımı); raporlar kategoriye bakar.</summary>
    public static class MaintenanceKindCategories
    {
        public const string Planned = "planned";
        public const string Unplanned = "unplanned";
    }

    /// <summary>Kural alanı adları (tarama kaynağının da tanıdığı).</summary>
    public static class AutomationFields
    {
        public const string NextMaintenanceDate = "nextMaintenanceDate";
    }

    /// <summary>Platform ilişki rolleri (ADR-0029 K9): taşeron aracın sahibi bu roldeki firmadır.</summary>
    public static class RelationshipRoles
    {
        public const string Carrier = "tasiyici";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Filo";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.VehiclesRead, "Araçları görüntüler", "Araçlar"),
        new(Permissions.VehiclesWrite, "Araç ekler ve düzenler", "Araçlar"),
        new(Permissions.MaintenanceRead, "Bakım kayıtlarını görüntüler", "Bakım kayıtları"),
        new(Permissions.MaintenanceWrite, "Bakım kaydı ekler, düzenler ve siler", "Bakım kayıtları"),
    ];

    /// <summary>Denetim kaydında görünen kayıt türleri (Y5).</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Vehicle, "Araç", Permissions.VehiclesRead, new Dictionary<string, string>
        {
            ["plate"] = "Plaka", ["kindKey"] = "Araç türü", ["ownership"] = "Mülkiyet", ["carrierPartyId"] = "Taşıyıcı firma",
            ["brand"] = "Marka", ["model"] = "Model", ["modelYear"] = "Model yılı", ["vin"] = "Şasi numarası",
            ["payloadKg"] = "Taşıma kapasitesi (kg)", ["volumeM3"] = "Hacim (m³)", ["loadingMeters"] = "Yükleme metresi (LDM)",
            ["palletCapacity"] = "Palet kapasitesi", ["status"] = "Durum", ["notes"] = "Notlar",
        }, "plate")
        {
            References = new Dictionary<string, string> { ["carrierPartyId"] = SharedEntities.Party },
        },
        new(Entities.Maintenance, "Bakım kaydı", Permissions.MaintenanceRead, new Dictionary<string, string>
        {
            ["vehicleId"] = "Araç", ["kindKey"] = "Bakım türü", ["performedOn"] = "Bakım tarihi", ["odometerKm"] = "Kilometre",
            ["supplierPartyId"] = "Servis firması", ["cost"] = "Tutar", ["currency"] = "Para birimi", ["nextDueDate"] = "Sonraki bakım tarihi",
            ["nextDueKm"] = "Sonraki bakım kilometresi", ["description"] = "Açıklama",
        }, "performedOn")
        {
            References = new Dictionary<string, string> { ["vehicleId"] = Entities.Vehicle, ["supplierPartyId"] = SharedEntities.Party },
        },
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Vehicle, "Araç"),
        new(Entities.Maintenance, "Bakım kaydı"),
    ];

    /// <summary>
    /// Araç türü (Y9b): hazır değerler örnektir; tenant kendi türlerini ekler. Kod türün adına değil <see cref="VehicleKindCategories">kategorisine</see>
    /// bakar (motorlu / çekilen); yeni türde kategori seçilir.
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.VehicleKind,
            "Araç türü",
            Entities.Vehicle,
            "kindKey",
            Required: true,
            [new(VehicleKindCategories.Powered, "Motorlu araç"), new(VehicleKindCategories.Towed, "Çekilen araç (dorse, römork)")],
            [
                new("Tractor", "Çekici", VehicleKindCategories.Powered),
                new("Truck", "Kamyon", VehicleKindCategories.Powered),
                new("LightTruck", "Kamyonet", VehicleKindCategories.Powered),
                new("Van", "Panelvan", VehicleKindCategories.Powered),
                new("SemiTrailer", "Dorse", VehicleKindCategories.Towed),
                new("Trailer", "Römork", VehicleKindCategories.Towed),
            ])
        {
            Default = "Truck",
        },

        // Bakım türü (Y9b): hazır değerler örnektir; tenant kendi türlerini ekler, yeni türde kategori seçilir (planlı / plansız).
        new(
            Lists.MaintenanceKind,
            "Bakım türü",
            Entities.Maintenance,
            "kindKey",
            Required: true,
            [new(MaintenanceKindCategories.Planned, "Planlı bakım"), new(MaintenanceKindCategories.Unplanned, "Plansız onarım")],
            [
                new("Periodic", "Periyodik bakım", MaintenanceKindCategories.Planned),
                new("Tires", "Lastik değişimi", MaintenanceKindCategories.Planned),
                new("Inspection", "Muayene hazırlığı", MaintenanceKindCategories.Planned),
                new("Repair", "Arıza onarımı", MaintenanceKindCategories.Unplanned),
                new("Accident", "Hasar onarımı", MaintenanceKindCategories.Unplanned),
            ])
        {
            Default = "Periodic",
        },
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => FleetLookupDefinitions.All;

    /// <summary>Ortak kayıtlar (ADR-0006): taşeron aracın taşıyıcısı firmadır; araç okuma izniyle okunur. Filo modülü firma oluşturmaz.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.VehiclesRead),
        new(SharedEntities.Party, Permissions.MaintenanceRead),
    ];

    /// <summary>Firmanın nerede kullanıldığı (silme ve birleştirme koruması).</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Vehicle, "carrierPartyId", "araç (taşıyıcı)"),
        new(SharedEntities.Party, Entities.Maintenance, "supplierPartyId", "bakım kaydı (servis)"),
    ];

    /// <summary>Araç formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => FleetForms.All;

    /// <summary>
    /// Kural altyapısı yetenekleri (ADR-0027): araç kaydı ve değişikliği olayları, durum ve mülkiyet alanları. Lojistiğin eski
    /// <c>logistics.vehicle</c> beyanı buraya taşındı (ADR-0038 K1); bakım tarihi alanı bakım kaydıyla (dilim 1b) gelir.
    /// </summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(Entities.Vehicle, "Araç", Permissions.VehiclesRead) { RecordSource = typeof(FleetAutomationRecordSource), LabelField = "plate" },
        ],
        Events =
        [
            new("fleet.vehicle.created", Entities.Vehicle, DomainEventTypes.RecordCreated, "Araç eklendi"),
            new("fleet.vehicle.updated", Entities.Vehicle, DomainEventTypes.RecordUpdated, "Araç güncellendi"),
        ],
        Fields =
        [
            new(Entities.Vehicle, "plate", "Plaka", AutomationFieldType.Text),
            new(Entities.Vehicle, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new("active", "Etkin"), new("maintenance", "Bakımda"), new("passive", "Pasif")],
            },
            new(Entities.Vehicle, "ownership", "Mülkiyet", AutomationFieldType.Choice)
            {
                Options = [new("owned", "Özmal"), new("leased", "Kiralık"), new("subcontracted", "Taşeron")],
            },
            new(Entities.Vehicle, "modelYear", "Model yılı", AutomationFieldType.Number),
            new(Entities.Vehicle, AutomationFields.NextMaintenanceDate, "Sonraki bakım tarihi", AutomationFieldType.Date)
            {
                Hint = "Bakımı yaklaşan araçlar için (tarihe kala tetikleyicisi); en son bakım kaydından gelir.",
            },
            new(Entities.Vehicle, "odometerKm", "Kilometre", AutomationFieldType.Number),
        ],
    };

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "fleet.manager",
            "Filo sorumlusu",
            "Araçları, dorseleri ve bakım kayıtlarını görüntüler, ekler ve düzenler.",
            [Permissions.VehiclesRead, Permissions.VehiclesWrite, Permissions.MaintenanceRead, Permissions.MaintenanceWrite]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<FleetStoreContext>();
        services.AddScoped<IRecordStore, VehicleRecordStore>();
        services.AddScoped<IRecordStore, MaintenanceRecordStore>();
        services.AddScoped<FleetAutomationRecordSource>();
        services.AddScoped<IVehicleDirectory, VehicleDirectory>();
    }
}
