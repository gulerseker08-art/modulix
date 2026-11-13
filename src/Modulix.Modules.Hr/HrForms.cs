using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Hr;

/// <summary>
/// İK formlarının varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar
/// için kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir. Kaydın kimliğini taşıyan alanlar sabittir.
/// Özlük sekmesindeki alanlar hassastır: izni olmayan kullanıcıya alan gönderilmez, editör o alanları çizmez.
/// </summary>
public static class HrForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(HrModule.Entities.Employee, "Çalışan",
        [new(General, "Genel bilgiler"), new("istihdam", "İstihdam"), new("ozluk", "Özlük bilgileri"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("kimlik", "Çalışan", "user"),
            new("gorev", "Görev ve organizasyon", "building"),
            new("istihdam", "İstihdam bilgileri", "briefcase", Tab: "istihdam"),
            new("kisisel", "Kimlik ve banka", "shield", Tab: "ozluk"),
            new("acil", "Adres ve acil durum", "home", Tab: "ozluk"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("personId", "Kişi", "kimlik", 8, Required: true, Fixed: true),
            new("employeeNumber", "Sicil numarası", "kimlik", 4),
            new("hireDate", "İşe giriş tarihi", "kimlik", 4, Required: true, Fixed: true),
            new("status", "Durum", "kimlik", 4, Fixed: true),
            new("userId", "Kullanıcı hesabı", "kimlik", 4),
            new("jobTitle", "Unvan", "gorev", 6),
            new("departmentKey", "Departman", "gorev", 6),
            new("managerEmployeeId", "Yönetici", "gorev", 6),
            new("workLocation", "Çalışma yeri", "gorev", 6),
            new("employmentType", "Çalışma şekli", "istihdam", 6),
            new("terminationDate", "Ayrılış tarihi", "istihdam", 6),
            new("nationalId", "T.C. kimlik numarası", "kisisel", 6),
            new("birthDate", "Doğum tarihi", "kisisel", 6),
            new("iban", "IBAN", "kisisel", 12),
            new("homeAddress", "Ev adresi", "acil", 12),
            new("emergencyContactName", "Acil durum kişisi", "acil", 6),
            new("emergencyContactPhone", "Acil durum telefonu", "acil", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),
    ];
}
