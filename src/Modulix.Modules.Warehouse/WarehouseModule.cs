using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Records;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo: tesis, alan, lokasyon, stok kartı, parti, hareket defteri ve gümrük uzantısı (ADR-0032). Bu ilk adım yalnızca modülü
/// kaydeder (anahtar, izinler, rol önerisi); varlıklar, ayarlar, seçim listeleri ve formlar kullanıcılarıyla birlikte dilimlerle
/// gelir (tesis ve lokasyon: dilim 2). Gümrük uzantısı (<c>warehouse.customs.*</c> izinleri) dilim 4'ten itibaren eklenir.
/// </summary>
public sealed class WarehouseModule : IModule
{
    public const string ModuleKey = "warehouse";

    public static class Permissions
    {
        public const string FacilitiesRead = "warehouse.facilities.read";
        public const string FacilitiesWrite = "warehouse.facilities.write";
        public const string ZonesRead = "warehouse.zones.read";
        public const string ZonesWrite = "warehouse.zones.write";
        public const string LocationLevelsRead = "warehouse.locationlevels.read";
        public const string LocationLevelsWrite = "warehouse.locationlevels.write";
        public const string LocationsRead = "warehouse.locations.read";
        public const string LocationsWrite = "warehouse.locations.write";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Facility = "warehouse.facility";
        public const string Zone = "warehouse.zone";
        public const string LocationLevel = "warehouse.location-level";
        public const string Location = "warehouse.location";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string ZoneKind = "warehouse.zone-kind";
    }

    /// <summary>Alan türü listesinin kategorileri: kodun anladığı anlam. Tenant türleri adlandırır, kategoriyi seçer.</summary>
    public static class ZoneKindCategories
    {
        public const string Controlled = "controlled";
        public const string Free = "free";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Depo";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.FacilitiesRead, "Tesisleri görüntüler", "Tesisler"),
        new(Permissions.FacilitiesWrite, "Tesis ekler ve düzenler", "Tesisler"),
        new(Permissions.ZonesRead, "Alanları görüntüler", "Alanlar"),
        new(Permissions.ZonesWrite, "Alan ekler ve düzenler", "Alanlar"),
        new(Permissions.LocationLevelsRead, "Lokasyon seviye tanımlarını görüntüler", "Lokasyon seviyeleri"),
        new(Permissions.LocationLevelsWrite, "Lokasyon seviye tanımı ekler ve düzenler", "Lokasyon seviyeleri"),
        new(Permissions.LocationsRead, "Lokasyonları görüntüler", "Lokasyonlar"),
        new(Permissions.LocationsWrite, "Lokasyon ekler ve düzenler", "Lokasyonlar"),
    ];

    /// <summary>Denetim kaydında görünen kayıt türleri (Y5): tesis değişiklikleri kim, ne zaman, neyi değiştirdi olarak izlenir.</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Facility, "Tesis", Permissions.FacilitiesRead, new Dictionary<string, string>
        {
            ["code"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama", ["countryCode"] = "Ülke",
            ["customsEnabled"] = "Gümrük uzantısı", ["allowMixedZones"] = "Karma alanlara izin", ["isActive"] = "Etkin",
        }, "name"),
        new(Entities.Zone, "Alan", Permissions.ZonesRead, new Dictionary<string, string>
        {
            ["facilityId"] = "Tesis", ["code"] = "Kod", ["name"] = "Ad", ["kindKey"] = "Alan türü", ["description"] = "Açıklama", ["isActive"] = "Etkin",
        }, "name")
        {
            References = new Dictionary<string, string> { ["facilityId"] = Entities.Facility },
        },
        new(Entities.Location, "Lokasyon", Permissions.LocationsRead, new Dictionary<string, string>
        {
            ["facilityId"] = "Tesis", ["parentId"] = "Üst lokasyon", ["zoneId"] = "Alan", ["value"] = "Değer", ["depth"] = "Derinlik",
            ["code"] = "Kod", ["name"] = "Ad", ["isActive"] = "Etkin",
        }, "code")
        {
            References = new Dictionary<string, string>
            {
                ["facilityId"] = Entities.Facility, ["parentId"] = Entities.Location, ["zoneId"] = Entities.Zone,
            },
        },
        new(Entities.LocationLevel, "Lokasyon seviyesi", Permissions.LocationLevelsRead, new Dictionary<string, string>
        {
            ["facilityId"] = "Tesis", ["position"] = "Sıra", ["key"] = "Anahtar", ["name"] = "Ad", ["characterSet"] = "Karakterler",
            ["minLength"] = "En az uzunluk", ["maxLength"] = "En çok uzunluk", ["separator"] = "Ayraç",
        }, "name")
        {
            References = new Dictionary<string, string> { ["facilityId"] = Entities.Facility },
        },
    ];

    /// <summary>Firmanın özel alan ekleyebildiği kayıt türleri (Y9a).</summary>
    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Facility, "Tesis"),
        new(Entities.Zone, "Alan"),
        new(Entities.Location, "Lokasyon"),
    ];

    /// <summary>
    /// Alan türü (Y9b): hazır değerler örnektir; tenant kendi türlerini ekler. Kod türün adına bakmaz, <see cref="ZoneKindCategories">kategorisine</see>
    /// bakar (gümrük denetimli / serbest); yeni türde kategori seçilir (K18).
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.ZoneKind,
            "Alan türü",
            Entities.Zone,
            "kindKey",
            Required: true,
            [new(ZoneKindCategories.Controlled, "Gümrük denetimli"), new(ZoneKindCategories.Free, "Serbest")],
            [
                new("Controlled", "Gümrük denetimli alan", ZoneKindCategories.Controlled),
                new("Free", "Serbest alan", ZoneKindCategories.Free),
            ])
        {
            Default = "Free",
        },
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => WarehouseLookupDefinitions.All;

    /// <summary>Tesis formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => WarehouseForms.All;

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "warehouse.manager",
            "Depo sorumlusu",
            "Tesisleri, alanları ve lokasyonları görüntüler ve düzenler.",
            [
                Permissions.FacilitiesRead, Permissions.FacilitiesWrite,
                Permissions.ZonesRead, Permissions.ZonesWrite,
                Permissions.LocationLevelsRead, Permissions.LocationLevelsWrite,
                Permissions.LocationsRead, Permissions.LocationsWrite,
            ]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<WarehouseStoreContext>();
        services.AddScoped<IRecordStore, FacilityRecordStore>();
        services.AddScoped<IRecordStore, ZoneRecordStore>();
        services.AddScoped<IRecordStore, LocationLevelRecordStore>();
        services.AddScoped<IRecordStore, LocationRecordStore>();
    }
}
