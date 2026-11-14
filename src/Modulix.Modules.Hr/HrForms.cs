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

        new(HrModule.Entities.LeaveRequest, "İzin talebi",
        [new(General, "Talep"), new("aciklama", "Açıklama ve ek bilgiler")],
        [
            new("talep", "İzin", "calendar"),
            new("aciklama", "Açıklama", "message-square", Tab: "aciklama"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "aciklama"),
        ],
        [
            new("employeeId", "Çalışan", "talep", 6, Required: true, Fixed: true),
            new("leaveTypeKey", "İzin türü", "talep", 6, Required: true, Fixed: true),
            new("startDate", "Başlangıç", "talep", 4, Required: true, Fixed: true),
            new("endDate", "Bitiş", "talep", 4, Required: true, Fixed: true),
            new("halfDay", "Yarım gün", "talep", 4),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(HrModule.Entities.LeaveBalance, "İzin hakkı",
        [new(General, "Hak"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("hak", "Yıllık izin hakkı", "calendar"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("employeeId", "Çalışan", "hak", 8, Required: true, Fixed: true),
            new("year", "Yıl", "hak", 4, Required: true, Fixed: true),
            new("entitledDays", "Hak (gün)", "hak", 6, Required: true, Fixed: true),
            new("carriedOverDays", "Devreden (gün)", "hak", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(HrModule.Entities.ExpenseClaim, "Masraf beyanı",
        [new(General, "Beyan"), new("aciklama", "Açıklama ve ek bilgiler")],
        [
            new("beyan", "Masraf beyanı", "wallet"),
            new("aciklama", "Açıklama", "message-square", Tab: "aciklama"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "aciklama"),
        ],
        [
            new("employeeId", "Çalışan", "beyan", 6, Required: true, Fixed: true),
            new("title", "Başlık", "beyan", 3, Required: true, Fixed: true),
            new("currency", "Para birimi", "beyan", 3),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(HrModule.Entities.ExpenseLine, "Masraf satırı",
        [new(General, "Masraf"), new("aciklama", "Açıklama ve ek bilgiler")],
        [
            new("masraf", "Masraf", "coins"),
            new("aciklama", "Açıklama", "message-square", Tab: "aciklama"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "aciklama"),
        ],
        [
            new("expenseDate", "Tarih", "masraf", 3, Required: true, Fixed: true),
            new("expenseTypeKey", "Masraf türü", "masraf", 3, Required: true, Fixed: true),
            new("amount", "Tutar (KDV dahil)", "masraf", 3, Required: true, Fixed: true),
            new("vatAmount", "KDV tutarı", "masraf", 3),
            new("description", "Açıklama", "aciklama", 12),
        ]),

        new(HrModule.Entities.Asset, "Demirbaş",
        [new(General, "Genel bilgiler"), new("satinalma", "Satın alma"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("kimlik", "Demirbaş", "package"),
            new("teknik", "Marka ve seri", "wrench"),
            new("satinalma", "Satın alma", "wallet", Tab: "satinalma"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("assetTag", "Envanter numarası", "kimlik", 4, Required: true, Fixed: true),
            new("name", "Ad", "kimlik", 8, Required: true, Fixed: true),
            new("categoryKey", "Kategori", "kimlik", 6, Required: true, Fixed: true),
            new("status", "Durum", "kimlik", 6, Fixed: true),
            new("brand", "Marka", "teknik", 4),
            new("model", "Model", "teknik", 4),
            new("serialNumber", "Seri numarası", "teknik", 4),
            new("purchaseDate", "Alış tarihi", "satinalma", 4),
            new("purchaseCost", "Alış tutarı", "satinalma", 4),
            new("currency", "Para birimi", "satinalma", 4),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(HrModule.Entities.AssetAssignment, "Zimmet",
        [new(General, "Teslim"), new("ek", "Ek bilgiler")],
        [
            new("teslim", "Zimmet", "clipboard-list"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("assetId", "Demirbaş", "teslim", 6, Required: true, Fixed: true),
            new("employeeId", "Çalışan", "teslim", 6, Required: true, Fixed: true),
            new("assignedOn", "Teslim tarihi", "teslim", 6, Required: true, Fixed: true),
            new("expectedReturnOn", "Beklenen iade", "teslim", 6),
            new("conditionOut", "Teslimdeki durum ve aksesuarlar", "teslim", 12),
        ]),

        new(HrModule.Entities.ChecklistTemplate, "Süreç şablonu",
        [new(General, "Şablon"), new("ek", "Ek bilgiler")],
        [
            new("sablon", "Şablon", "clipboard-list"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("name", "Ad", "sablon", 6, Required: true, Fixed: true),
            new("kind", "Tür", "sablon", 3, Required: true, Fixed: true),
            new("isActive", "Etkin", "sablon", 3),
            new("description", "Açıklama", "sablon", 12),
        ]),

        new(HrModule.Entities.ChecklistTemplateItem, "Şablon adımı",
        [new(General, "Adım"), new("ek", "Ek bilgiler")],
        [
            new("adim", "Adım", "check-circle"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("title", "Başlık", "adim", 8, Required: true, Fixed: true),
            new("sequence", "Sıra", "adim", 4),
            new("responsibleRole", "Sorumlu", "adim", 4),
            new("dueOffsetDays", "Gün farkı", "adim", 4),
            new("taskType", "Görev türü", "adim", 4),
            new("description", "Açıklama", "adim", 12),
        ]),

        new(HrModule.Entities.Announcement, "Duyuru",
        [new(General, "Duyuru"), new("yayin", "Yayın ve hedef"), new("ek", "Ek bilgiler")],
        [
            new("duyuru", "Duyuru", "bell"),
            new("yayin", "Yayın", "calendar", Tab: "yayin"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("title", "Başlık", "duyuru", 12, Required: true, Fixed: true),
            new("body", "Metin", "duyuru", 12, Required: true, Fixed: true),
            new("isPublished", "Yayında", "yayin", 4),
            new("isPinned", "Sabitlenmiş", "yayin", 4),
            new("audienceDepartmentKey", "Hedef departman", "yayin", 4),
            new("publishOn", "Başlangıç", "yayin", 6),
            new("expiresOn", "Bitiş", "yayin", 6),
        ]),
        new(HrModule.Entities.Survey, "Anket",
        [new(General, "Anket"), new("hedef", "Hedef ve süre"), new("ek", "Ek bilgiler")],
        [
            new("anket", "Anket", "clipboard-list"),
            new("hedef", "Hedef ve süre", "users", Tab: "hedef"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("title", "Başlık", "anket", 12, Required: true, Fixed: true),
            new("description", "Açıklama", "anket", 12),
            new("isAnonymous", "Kimliği gizli", "hedef", 4),
            new("audienceDepartmentKey", "Hedef departman", "hedef", 4),
            new("closesOn", "Kapanış günü", "hedef", 4),
        ]),
        new(HrModule.Entities.SurveyQuestion, "Anket sorusu",
        [new(General, "Soru"), new("secenek", "Cevap biçimi"), new("ek", "Ek bilgiler")],
        [
            new("soru", "Soru", "message-square"),
            new("bicim", "Cevap biçimi", "list", Tab: "secenek"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("text", "Soru", "soru", 12, Required: true, Fixed: true),
            new("sequence", "Sıra", "bicim", 6),
            new("isRequired", "Zorunlu", "bicim", 6),
            new("questionType", "Tür", "soru", 6, Required: true, Fixed: true),
            new("options", "Seçenekler (satır başına bir tane)", "bicim", 12),
        ]),
        new(HrModule.Entities.BlogPost, "Blog yazısı",
        [new(General, "Yazı"), new("yayin", "Yayın"), new("ek", "Ek bilgiler")],
        [
            new("yazi", "Yazı", "file-text"),
            new("yayin", "Yayın", "calendar", Tab: "yayin"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("title", "Başlık", "yazi", 12, Required: true, Fixed: true),
            new("summary", "Özet", "yazi", 12),
            new("body", "Metin", "yazi", 12, Required: true, Fixed: true),
            new("isPublished", "Yayında", "yayin", 6),
        ]),
        new(HrModule.Entities.PhotoAlbum, "Fotoğraf albümü",
        [new(General, "Albüm"), new("yayin", "Yayın"), new("ek", "Ek bilgiler")],
        [
            new("album", "Albüm", "image"),
            new("yayin", "Yayın", "calendar", Tab: "yayin"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ek"),
        ],
        [
            new("title", "Başlık", "album", 12, Required: true, Fixed: true),
            new("description", "Açıklama", "album", 12),
            new("eventDate", "Etkinlik günü", "yayin", 6),
            new("isPublished", "Yayında", "yayin", 6),
        ]),
    ];
}
