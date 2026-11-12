using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Logistics;

/// <summary>
/// Lojistik formlarının varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar
/// için kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir.
/// </summary>
public static class LogisticsForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(LogisticsModule.Entities.Driver, "Sürücü",
        [new(General, "Genel bilgiler"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("kimlik", "Sürücü", "user"),
            new("calisma", "Çalışma şekli", "briefcase"),
            new("arac", "Araç", "truck"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("personId", "Kişi", "kimlik", 8, Required: true, Fixed: true),
            new("status", "Durum", "kimlik", 4, Fixed: true),
            new("driverType", "Sürücü tipi", "calisma", 6, Fixed: true),
            new("carrierPartyId", "Taşıyıcı firma", "calisma", 6, Fixed: true),
            new("defaultVehicleId", "Varsayılan araç", "arac", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(LogisticsModule.Entities.CarrierTerms, "Taşıyıcı anlaşması",
        [new(General, "Genel bilgiler"), new("kosullar", "Ücret ve performans")],
        [
            new("anlasma", "Anlaşma", "file-text"),
            new("gecerlilik", "Geçerlilik ve ödeme", "calendar"),
            new("ucret", "Ücret anlaşması", "wallet", Tab: "kosullar"),
            new("performans", "Performans", "trending-up", Tab: "kosullar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "kosullar"),
        ],
        [
            new("carrierPartyId", "Taşıyıcı firma", "anlasma", 6, Required: true, Fixed: true),
            new("title", "Anlaşma adı", "anlasma", 6, Required: true, Fixed: true),
            new("agreementNo", "Anlaşma no", "anlasma", 6),
            new("isActive", "Etkin", "anlasma", 6, Fixed: true),
            new("validFrom", "Başlangıç", "gecerlilik", 4, Required: true, Fixed: true),
            new("validTo", "Bitiş", "gecerlilik", 4),
            new("currency", "Para birimi", "gecerlilik", 4, Required: true, Fixed: true),
            new("paymentTermDays", "Ödeme vadesi (gün)", "gecerlilik", 4),
            new("rateNote", "Ücret anlaşması", "ucret", 12),
            new("performanceNote", "Performans notu", "performans", 12),
        ]),

        Cargo(LogisticsModule.Entities.TransportRequest, "Taşıma talebi", [new("externalRef", "Dış referans", "taraflar", 6)]),
        Cargo(LogisticsModule.Entities.Shipment, "Sevkiyat", [], freight: true),

        // Sefer: plan ve atama (araç, dorse, sürücüler ya da taşeron), notlar. Duraklar ve sevkiyatlar formun altındaki durak panelindedir.
        new(LogisticsModule.Entities.Trip, "Sefer",
        [new(General, "Plan ve atama"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("plan", "Plan", "calendar"),
            new("arac", "Araç", "truck"),
            new("surucu", "Sürücüler", "user"),
            new("tasiyici", "Taşeron", "building"),
            new("hakedis", "Hakediş", "wallet"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("plannedStart", "Planlanan başlangıç", "plan", 6, Required: true, Fixed: true),
            new("plannedEnd", "Planlanan bitiş", "plan", 6),
            new("vehicleId", "Araç (çekici / kamyon)", "arac", 6, Fixed: true),
            new("trailerId", "Dorse", "arac", 6),
            new("driverId", "Sürücü", "surucu", 6, Fixed: true),
            new("coDriverId", "İkinci sürücü", "surucu", 6),
            new("carrierPartyId", "Taşıyıcı firma", "tasiyici", 12),
            new("settlementAmount", "Hakediş tutarı", "hakedis", 6),
            new("settlementCurrency", "Para birimi", "hakedis", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),
    ];

    /// <summary>Talep ve sevkiyatın ortak düzeni: taraflar ve güzergâh, yük, notlar (üç sekme).</summary>
    private static FormDefinition Cargo(string entity, string name, IReadOnlyList<FormFieldDefinition> extra, bool freight = false) =>
        new(entity, name,
        [new(General, "Taraflar ve güzergâh"), new("yuk", "Yük"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("taraflar", "Taraflar", "building"),
            new("guzergah", "Güzergâh ve tarih", "map-pin"),
            new("yuk", "Yük", "package", Tab: "yuk"),
            .. (freight ? (FormSectionDefinition[])[new("navlun", "Navlun", "wallet", Tab: "yuk")] : []),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("customerPartyId", "Müşteri", "taraflar", 6, Required: true, Fixed: true),
            new("transportModeKey", "Taşıma türü", "taraflar", 6, Required: true, Fixed: true),
            new("senderPartyId", "Gönderen", "taraflar", 6),
            new("receiverPartyId", "Alıcı", "taraflar", 6),
            .. extra,
            new("pickupCity", "Yükleme şehri", "guzergah", 6, Required: true, Fixed: true),
            new("deliveryCity", "Teslim şehri", "guzergah", 6, Required: true, Fixed: true),
            new("pickupAddress", "Yükleme adresi", "guzergah", 6),
            new("deliveryAddress", "Teslim adresi", "guzergah", 6),
            new("pickupDate", "Yükleme günü", "guzergah", 6, Required: true, Fixed: true),
            new("deliveryBy", "En geç teslim", "guzergah", 6),
            new("cargoDescription", "Yük tanımı", "yuk", 12, Required: true, Fixed: true),
            new("weightKg", "Ağırlık (kg)", "yuk", 3),
            new("volumeM3", "Hacim (m³)", "yuk", 3),
            new("palletCount", "Palet", "yuk", 3),
            new("loadingMeters", "Yükleme metresi (LDM)", "yuk", 3),
            new("hazardous", "Tehlikeli madde (ADR)", "yuk", 6),
            .. (freight ? (FormFieldDefinition[])[new("freightAmount", "Navlun tutarı", "navlun", 6), new("freightCurrency", "Para birimi", "navlun", 6)] : []),
            new("notes", "Notlar", "notlar", 12),
        ]);
}
