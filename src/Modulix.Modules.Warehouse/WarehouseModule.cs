using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
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
        public const string AttorneysRead = "warehouse.attorneys.read";
        public const string AttorneysWrite = "warehouse.attorneys.write";
        public const string StockItemsRead = "warehouse.stockitems.read";
        public const string StockItemsWrite = "warehouse.stockitems.write";
        public const string DeclarationsRead = "warehouse.customs.declarations.read";
        public const string DeclarationsWrite = "warehouse.customs.declarations.write";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Facility = "warehouse.facility";
        public const string Zone = "warehouse.zone";
        public const string LocationLevel = "warehouse.location-level";
        public const string Location = "warehouse.location";
        public const string PowerOfAttorney = "warehouse.power-of-attorney";
        public const string StockItem = "warehouse.stock-item";
        public const string CustomsDeclaration = "warehouse.customs-declaration";
        public const string CustomsDeclarationLine = "warehouse.customs-declaration-line";
    }

    /// <summary>Firma ilişki rolü kodları (platform kataloğu, ADR-0029 K9; K2): vekâletin tarafları bu rollere sahip firmalardır.</summary>
    public static class RelationshipRoles
    {
        public const string Importer = "ithalatci-malik";
        public const string Broker = "gumruk-musaviri";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string ZoneKind = "warehouse.zone-kind";
        public const string StockUnit = "warehouse.stock-unit";
        public const string CustomsRegime = "warehouse.customs-regime";
        public const string DeclarationStatus = "warehouse.declaration-status";
        public const string ContainerType = "warehouse.container-type";
        public const string OriginCountry = "warehouse.origin-country";
        public const string DeclarationLineUnit = "warehouse.declaration-line-unit";
    }

    /// <summary>Beyanname durumu listesinin kategorileri: kodun anladığı anlam. Tenant durumları adlandırır, kategoriyi seçer (K18).</summary>
    public static class DeclarationStatusCategories
    {
        public const string Draft = "draft";
        public const string Registered = "registered";
        public const string Cancelled = "cancelled";
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
        new(Permissions.AttorneysRead, "Vekâletleri görüntüler", "Vekâletler"),
        new(Permissions.AttorneysWrite, "Vekâlet ekler, düzenler ve iptal eder", "Vekâletler"),
        new(Permissions.StockItemsRead, "Stok kartlarını görüntüler", "Stok kartları"),
        new(Permissions.StockItemsWrite, "Stok kartı ekler ve düzenler", "Stok kartları"),
        new(Permissions.DeclarationsRead, "Gümrük beyannamelerini ve satırlarını görüntüler", "Gümrük beyannameleri"),
        new(Permissions.DeclarationsWrite, "Gümrük beyannamesi ve satırı ekler, düzenler ve siler", "Gümrük beyannameleri"),
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
        new(Entities.PowerOfAttorney, "Vekâlet", Permissions.AttorneysRead, new Dictionary<string, string>
        {
            ["importerPartyId"] = "İthalatçı", ["brokerPartyId"] = "Gümrük müşaviri", ["validFrom"] = "Başlangıç", ["validTo"] = "Bitiş",
            ["scope"] = "Kapsam", ["documentReference"] = "Belge", ["isActive"] = "Etkin",
        }, "documentReference")
        {
            References = new Dictionary<string, string>
            {
                ["importerPartyId"] = SharedEntities.Party, ["brokerPartyId"] = SharedEntities.Party,
            },
        },
        new(Entities.StockItem, "Stok kartı", Permissions.StockItemsRead, new Dictionary<string, string>
        {
            ["code"] = "Stok kodu", ["name"] = "Ad", ["unitKey"] = "Birim", ["gtip"] = "GTİP", ["description"] = "Açıklama", ["isActive"] = "Etkin",
        }, "code"),
        new(Entities.CustomsDeclaration, "Gümrük beyannamesi", Permissions.DeclarationsRead, new Dictionary<string, string>
        {
            ["registrationNo"] = "Tescil no", ["registrationDate"] = "Tescil tarihi", ["customsOffice"] = "Gümrük idaresi", ["regimeKey"] = "Rejim",
            ["statusKey"] = "Durum", ["importerPartyId"] = "İthalatçı", ["brokerPartyId"] = "Gümrük müşaviri",
            ["externalReference"] = "Dış referans", ["description"] = "Açıklama",
        }, "registrationNo")
        {
            References = new Dictionary<string, string>
            {
                ["importerPartyId"] = SharedEntities.Party, ["brokerPartyId"] = SharedEntities.Party,
            },
        },
        new(Entities.CustomsDeclarationLine, "Beyanname satırı", Permissions.DeclarationsRead, new Dictionary<string, string>
        {
            ["declarationId"] = "Beyanname", ["lineNo"] = "Satır no", ["gtip"] = "GTİP", ["description"] = "Eşya tanımı",
            ["containerTypeKey"] = "Kap cinsi", ["containerCount"] = "Kap adedi", ["grossKg"] = "Brüt kg", ["netKg"] = "Net kg",
            ["originCountryKey"] = "Menşe", ["quantity"] = "Miktar", ["unitKey"] = "Birim",
        }, "lineNo")
        {
            References = new Dictionary<string, string> { ["declarationId"] = Entities.CustomsDeclaration },
        },
    ];

    /// <summary>Firmanın özel alan ekleyebildiği kayıt türleri (Y9a).</summary>
    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Facility, "Tesis"),
        new(Entities.Zone, "Alan"),
        new(Entities.Location, "Lokasyon"),
        new(Entities.PowerOfAttorney, "Vekâlet"),
        new(Entities.StockItem, "Stok kartı"),
        new(Entities.CustomsDeclaration, "Gümrük beyannamesi"),
        new(Entities.CustomsDeclarationLine, "Beyanname satırı"),
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

        // Ölçü birimi (Y9b): hazır değerler örnektir, kategorisizdir (kod birimin adına bakmaz); tenant kendi birimlerini yönetir.
        new(
            Lists.StockUnit,
            "Stok birimi",
            Entities.StockItem,
            "unitKey",
            Required: true,
            [],
            [
                new("Piece", "Adet"),
                new("Kilogram", "Kilogram"),
                new("Box", "Koli"),
                new("Pallet", "Palet"),
            ])
        {
            Default = "Piece",
        },

        // Rejim kodu (K6): hazır değer yalnızca örnektir ve uzman doğrulaması bekler (ADR bölüm 9); tenant kendi rejimlerini yönetir.
        new(
            Lists.CustomsRegime,
            "Gümrük rejimi",
            Entities.CustomsDeclaration,
            "regimeKey",
            Required: true,
            [],
            [new("R7100", "7100 — Antrepo rejimi")])
        {
            Default = "R7100",
        },

        // Beyanname durumu (K18): kod kategoriye bakar (taslak, tescilli, iptal); iptal kategorisindeki beyannamenin satırları değiştirilemez.
        new(
            Lists.DeclarationStatus,
            "Beyanname durumu",
            Entities.CustomsDeclaration,
            "statusKey",
            Required: true,
            [
                new(DeclarationStatusCategories.Draft, "Taslak"),
                new(DeclarationStatusCategories.Registered, "Tescilli"),
                new(DeclarationStatusCategories.Cancelled, "İptal"),
            ],
            [
                new("Draft", "Taslak", DeclarationStatusCategories.Draft),
                new("Registered", "Tescilli", DeclarationStatusCategories.Registered),
                new("Cancelled", "İptal edildi", DeclarationStatusCategories.Cancelled),
            ])
        {
            Default = "Draft",
        },

        // Kap cinsi ve menşe (K10): yasal çekirdek alanlar; hazır değerler örnektir, kategorisizdir, tenant yönetir. Satırda açıkça seçilir.
        new(
            Lists.ContainerType,
            "Kap cinsi",
            Entities.CustomsDeclarationLine,
            "containerTypeKey",
            Required: true,
            [],
            [
                new("Pallet", "Palet"),
                new("Box", "Koli"),
                new("Sack", "Çuval"),
                new("Drum", "Varil"),
                new("Bulk", "Dökme"),
            ]),
        new(
            Lists.OriginCountry,
            "Menşe ülke",
            Entities.CustomsDeclarationLine,
            "originCountryKey",
            Required: true,
            [],
            [
                new("TR", "Türkiye"), new("CN", "Çin"), new("DE", "Almanya"), new("IT", "İtalya"), new("FR", "Fransa"),
                new("ES", "İspanya"), new("US", "ABD"), new("GB", "Birleşik Krallık"), new("IN", "Hindistan"), new("JP", "Japonya"),
                new("KR", "Güney Kore"), new("NL", "Hollanda"), new("RU", "Rusya"), new("EG", "Mısır"), new("BR", "Brezilya"),
            ]),
        new(
            Lists.DeclarationLineUnit,
            "Beyanname satırı birimi",
            Entities.CustomsDeclarationLine,
            "unitKey",
            Required: true,
            [],
            [
                new("Piece", "Adet"),
                new("Kilogram", "Kilogram"),
                new("Box", "Koli"),
                new("Pallet", "Palet"),
            ]),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => WarehouseLookupDefinitions.All;

    /// <summary>Ortak kayıtlar (ADR-0006): vekâletin tarafları firmadır; vekâlet okuma izniyle okunur. Depo modülü firma oluşturmaz.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.AttorneysRead),
        new(SharedEntities.Party, Permissions.DeclarationsRead),
    ];

    /// <summary>Firmanın nerede kullanıldığı (silme ve birleştirme koruması).</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.PowerOfAttorney, "importerPartyId", "vekâlet (ithalatçı)"),
        new(SharedEntities.Party, Entities.PowerOfAttorney, "brokerPartyId", "vekâlet (müşavir)"),
        new(SharedEntities.Party, Entities.CustomsDeclaration, "importerPartyId", "gümrük beyannamesi (ithalatçı)"),
        new(SharedEntities.Party, Entities.CustomsDeclaration, "brokerPartyId", "gümrük beyannamesi (müşavir)"),
    ];

    /// <summary>Tesis formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => WarehouseForms.All;

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "warehouse.manager",
            "Depo sorumlusu",
            "Tesisleri, alanları, lokasyonları, vekâletleri ve stok kartlarını görüntüler ve düzenler.",
            [
                Permissions.FacilitiesRead, Permissions.FacilitiesWrite,
                Permissions.ZonesRead, Permissions.ZonesWrite,
                Permissions.LocationLevelsRead, Permissions.LocationLevelsWrite,
                Permissions.LocationsRead, Permissions.LocationsWrite,
                Permissions.AttorneysRead, Permissions.AttorneysWrite,
                Permissions.StockItemsRead, Permissions.StockItemsWrite,
            ]),
        new(
            "warehouse.customs",
            "Gümrük sorumlusu",
            "Gümrük beyannamelerini ve satırlarını yönetir; vekâletleri ve stok kartlarını görüntüler.",
            [
                Permissions.DeclarationsRead, Permissions.DeclarationsWrite,
                Permissions.AttorneysRead,
                Permissions.StockItemsRead,
            ]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<WarehouseStoreContext>();
        services.AddScoped<IRecordStore, FacilityRecordStore>();
        services.AddScoped<IRecordStore, ZoneRecordStore>();
        services.AddScoped<IRecordStore, LocationLevelRecordStore>();
        services.AddScoped<IRecordStore, LocationRecordStore>();
        services.AddScoped<IRecordStore, PowerOfAttorneyRecordStore>();
        services.AddScoped<IRecordStore, StockItemRecordStore>();
        services.AddScoped<IRecordStore, CustomsDeclarationRecordStore>();
        services.AddScoped<IRecordStore, CustomsDeclarationLineRecordStore>();
    }
}
