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
    ];
}
