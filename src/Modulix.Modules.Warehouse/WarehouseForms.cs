using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Warehouse;

/// <summary>
/// Depo formlarının varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar için
/// kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir. Kaydın kimliğini taşıyan alanlar sabittir (gizlenemez,
/// zorunluluğu değişmez). Yalnızca bu adımda arayüzü olan formlar tanımlıdır; alan ve lokasyon formları kendi ekranlarıyla eklenir.
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
    ];
}
