using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo formlarının varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar için
/// kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir. Kaydın kimliğini taşıyan alanlar sabittir (gizlenemez,
/// zorunluluğu değişmez). Yalnızca arayüzü olan formlar tanımlıdır.
/// </summary>
public static class WarehouseForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(WarehouseModule.Entities.Facility, "Tesis",
        [new(General, "Genel bilgiler"), new("gumruk", "Gümrük ve alanlar")],
        [
            new("temel", "Temel bilgiler", "warehouse"),
            new("aciklama", "Açıklama", "message-square"),
            new("gumruk", "Gümrük uzantısı ve alanlar", "shield", Tab: "gumruk"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "gumruk"),
        ],
        [
            new("code", "Tesis kodu", "temel", 4, Required: true, Fixed: true),
            new("name", "Tesis adı", "temel", 8, Required: true, Fixed: true),
            new("countryCode", "Ülke", "temel", 4),
            new("isActive", "Etkin", "temel", 4, Fixed: true),
            new("description", "Açıklama", "aciklama", 12),
            new("customsEnabled", "Gümrük uzantısı açık", "gumruk", 6, Fixed: true),
            new("allowMixedZones", "Serbest ve gümrük denetimli alanlar bir arada olabilir", "gumruk", 6, Fixed: true),
        ]),

        new(WarehouseModule.Entities.Zone, "Alan",
        [new(General, "Genel bilgiler"), new("ayrintilar", "Açıklama ve ek bilgiler")],
        [
            new("temel", "Temel bilgiler", "layers"),
            new("aciklama", "Açıklama", "message-square", Tab: "ayrintilar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("facilityId", "Tesis", "temel", 6, Required: true, Fixed: true),
            new("kindKey", "Alan türü", "temel", 6, Required: true, Fixed: true),
            new("code", "Alan kodu", "temel", 4, Required: true, Fixed: true),
            new("name", "Alan adı", "temel", 8, Required: true, Fixed: true),
            new("isActive", "Etkin", "temel", 4, Fixed: true),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(WarehouseModule.Entities.Location, "Lokasyon",
        [new(General, "Genel bilgiler"), new("ayrintilar", "Ek bilgiler")],
        [
            new("temel", "Yerleşim", "map-pin"),
            new("tanim", "Tanım", "message-square"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("facilityId", "Tesis", "temel", 6, Required: true, Fixed: true),
            new("parentId", "Üst lokasyon", "temel", 6, Fixed: true),
            new("value", "Değer", "temel", 4, Required: true, Fixed: true),
            new("zoneId", "Alan", "temel", 8),
            new("name", "Ad", "tanim", 8),
            new("isActive", "Etkin", "tanim", 4, Fixed: true),
        ]),

        new(WarehouseModule.Entities.PowerOfAttorney, "Vekâlet",
        [new(General, "Genel bilgiler"), new("ayrintilar", "Kapsam ve belge")],
        [
            new("taraflar", "Taraflar", "users"),
            new("gecerlilik", "Geçerlilik", "calendar"),
            new("kapsam", "Kapsam ve belge", "file-text", Tab: "ayrintilar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("importerPartyId", "İthalatçı", "taraflar", 6, Required: true, Fixed: true),
            new("brokerPartyId", "Gümrük müşaviri", "taraflar", 6, Required: true, Fixed: true),
            new("validFrom", "Başlangıç", "gecerlilik", 4, Required: true, Fixed: true),
            new("validTo", "Bitiş", "gecerlilik", 4),
            new("isActive", "Etkin", "gecerlilik", 4, Fixed: true),
            new("scope", "Kapsam", "kapsam", 12),
            new("documentReference", "Belge referansı", "kapsam", 12),
        ]),

        new(WarehouseModule.Entities.StockItem, "Stok kartı",
        [new(General, "Genel bilgiler"), new("gumruk", "Gümrük ve ayrıntılar")],
        [
            new("temel", "Temel bilgiler", "package"),
            new("gtip", "Gümrük tarife", "shield", Tab: "gumruk"),
            new("aciklama", "Açıklama", "message-square", Tab: "gumruk"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "gumruk"),
        ],
        [
            new("code", "Stok kodu", "temel", 4, Required: true, Fixed: true),
            new("name", "Ad", "temel", 8, Required: true, Fixed: true),
            new("unitKey", "Birim", "temel", 4, Required: true, Fixed: true),
            new("isActive", "Etkin", "temel", 4, Fixed: true),
            new("gtip", "GTİP (varsayılan)", "gtip", 6),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(WarehouseModule.Entities.Lot, "Parti",
        [new(General, "Genel bilgiler"), new("ayrintilar", "Açıklama ve ek bilgiler")],
        [
            new("kimlik", "Parti", "package"),
            new("taraflar", "Malik ve fatura", "users"),
            new("aciklama", "Açıklama", "message-square", Tab: "ayrintilar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("lotNo", "Parti no", "kimlik", 4, Required: true, Fixed: true),
            new("stockItemId", "Stok kartı", "kimlik", 8, Required: true, Fixed: true),
            new("isActive", "Etkin", "kimlik", 4, Fixed: true),
            new("ownerPartyId", "Malik", "taraflar", 6, Required: true, Fixed: true),
            new("billToPartyId", "Faturalanan taraf", "taraflar", 6),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(WarehouseModule.Entities.CustomsDeclaration, "Gümrük beyannamesi",
        [new(General, "Genel bilgiler"), new("ayrintilar", "Açıklama ve ek bilgiler")],
        [
            new("kimlik", "Beyanname", "file-text"),
            new("taraflar", "Taraflar", "users"),
            new("aciklama", "Açıklama ve dış referans", "message-square", Tab: "ayrintilar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("registrationNo", "Tescil no", "kimlik", 4, Required: true, Fixed: true),
            new("registrationDate", "Tescil tarihi", "kimlik", 4, Required: true, Fixed: true),
            new("statusKey", "Durum", "kimlik", 4, Required: true, Fixed: true),
            new("regimeKey", "Rejim", "kimlik", 6, Required: true, Fixed: true),
            new("customsOffice", "Gümrük idaresi", "kimlik", 6),
            new("importerPartyId", "İthalatçı", "taraflar", 6, Required: true, Fixed: true),
            new("brokerPartyId", "Gümrük müşaviri", "taraflar", 6),
            new("externalReference", "Dış referans", "aciklama", 6),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(WarehouseModule.Entities.CustomsDeclarationLine, "Beyanname satırı",
        [new(General, "Eşya ve miktar"), new("ayrintilar", "Ek bilgiler")],
        [
            new("esya", "Eşya", "file-text"),
            new("kap", "Kap ve ağırlık", "package"),
            new("miktar", "Menşe ve miktar", "globe"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ayrintilar"),
        ],
        [
            new("lineNo", "Satır no", "esya", 4, Required: true, Fixed: true),
            new("gtip", "GTİP", "esya", 8, Required: true, Fixed: true),
            new("description", "Eşya tanımı", "esya", 12, Required: true, Fixed: true),
            new("containerTypeKey", "Kap cinsi", "kap", 4, Required: true, Fixed: true),
            new("containerCount", "Kap adedi", "kap", 4, Required: true, Fixed: true),
            new("grossKg", "Brüt kg", "kap", 4, Required: true, Fixed: true),
            new("netKg", "Net kg", "kap", 4, Required: true, Fixed: true),
            new("originCountryKey", "Menşe ülke", "miktar", 4, Required: true, Fixed: true),
            new("quantity", "Miktar", "miktar", 4, Required: true, Fixed: true),
            new("unitKey", "Birim", "miktar", 4, Required: true, Fixed: true),
        ]),
    ];
}
