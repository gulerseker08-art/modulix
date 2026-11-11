using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Fleet;

/// <summary>
/// Filo formlarının varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar
/// için kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir. Kaydın kimliğini taşıyan alanlar sabittir.
/// </summary>
public static class FleetForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(FleetModule.Entities.Vehicle, "Araç",
        [new(General, "Genel bilgiler"), new("kapasite", "Kapasite"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("kimlik", "Araç kimliği", "truck"),
            new("mulkiyet", "Mülkiyet", "building"),
            new("teknik", "Teknik bilgiler", "wrench", Tab: "kapasite"),
            new("yuk", "Yük kapasitesi", "package", Tab: "kapasite"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("plate", "Plaka", "kimlik", 4, Required: true, Fixed: true),
            new("kindKey", "Araç türü", "kimlik", 4, Required: true, Fixed: true),
            new("status", "Durum", "kimlik", 4, Fixed: true),
            new("ownership", "Mülkiyet", "mulkiyet", 6, Fixed: true),
            new("carrierPartyId", "Taşıyıcı firma", "mulkiyet", 6, Fixed: true),
            new("brand", "Marka", "teknik", 4),
            new("model", "Model", "teknik", 4),
            new("modelYear", "Model yılı", "teknik", 4),
            new("vin", "Şasi numarası", "teknik", 12),
            new("payloadKg", "Taşıma kapasitesi (kg)", "yuk", 3),
            new("volumeM3", "Hacim (m³)", "yuk", 3),
            new("loadingMeters", "Yükleme metresi (LDM)", "yuk", 3),
            new("palletCapacity", "Palet kapasitesi", "yuk", 3),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(FleetModule.Entities.Maintenance, "Bakım kaydı",
        [new(General, "Genel bilgiler"), new("plan", "Sonraki bakım ve notlar")],
        [
            new("bakim", "Bakım", "wrench"),
            new("maliyet", "Servis ve maliyet", "wallet"),
            new("sonraki", "Sonraki bakım", "calendar", Tab: "plan"),
            new("aciklama", "Açıklama", "message-square", Tab: "plan"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "plan"),
        ],
        [
            new("vehicleId", "Araç", "bakim", 6, Required: true, Fixed: true),
            new("kindKey", "Bakım türü", "bakim", 6, Required: true, Fixed: true),
            new("performedOn", "Bakım tarihi", "bakim", 6, Required: true, Fixed: true),
            new("odometerKm", "Kilometre", "bakim", 6),
            new("supplierPartyId", "Servis firması", "maliyet", 12),
            new("cost", "Tutar", "maliyet", 8),
            new("currency", "Para birimi", "maliyet", 4),
            new("nextDueDate", "Sonraki bakım tarihi", "sonraki", 6),
            new("nextDueKm", "Sonraki bakım kilometresi", "sonraki", 6),
            new("description", "Açıklama", "aciklama", 12),
        ]),
    ];
}
