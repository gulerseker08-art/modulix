using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM formlarının varsayılan düzenleri (ADR-0021). Alan anahtarları kayıt alanlarının teknik adlarıdır; arayüzdeki
/// editörler her anahtar için kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir.
/// Kaydın kimliğini taşıyan alanlar ve yalnızca ekranda anlamı olan onay kutuları sabittir (gizlenemez, zorunluluğu değişmez).
/// </summary>
public static class CrmForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    // Her kayıt formu sekmelidir (arayüz kuralı, CLAUDE.md → Arayüz tasarım kuralları): bölümler anlamlı sekmelere dağıtılır.
    // Sekme anahtarları firmanın düzeninde saklanır; "finans" gibi anahtarlar firmaların eklediği sekmelere bırakılmıştır.
    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(CrmModule.Entities.Company, "Firma",
        [new(General, "Genel bilgiler"), new("iletisim", "İletişim"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("temel", "Temel bilgiler", "building"),
            new("iletisim", "Genel iletişim bilgileri", "phone", Tab: "iletisim"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("name", "Firma adı", "temel", 6, Required: true, Fixed: true),
            new("kind", "Tür", "temel", 3, Fixed: true),
            new("status", "Durum", "temel", 3, Fixed: true),
            new("ownerId", "Sorumlu", "temel", 4),
            new("industry", "Sektör", "temel", 4),
            new("source", "Kaynak", "temel", 4),
            new("taxOffice", "Vergi dairesi", "temel", 4),
            new("taxNumber", "Vergi no / T.C. kimlik no", "temel", 4),
            new("tags", "Etiketler", "temel", 4),
            new("address", "Adres", "iletisim", 12),
            new("phone", "Telefon", "iletisim", 4),
            new("email", "E-posta", "iletisim", 4),
            new("website", "Web sitesi", "iletisim", 4),
            new("city", "Şehir", "iletisim", 4),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(CrmModule.Entities.Contact, "Kişi",
        [new(General, "Kişi bilgileri"), new("iletisim", "İletişim"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("kisi", "Kişi bilgileri", "user"),
            new("iletisim", "İletişim", "phone", Tab: "iletisim"),
            new("notlar", "Notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("firstName", "Ad", "kisi", 6, Required: true, Fixed: true),
            new("lastName", "Soyad", "kisi", 6, Required: true, Fixed: true),
            new("title", "Unvan", "kisi", 4),
            new("companyId", "Firma", "kisi", 4),
            new("ownerId", "Sorumlu", "kisi", 4),
            new("email", "E-posta (ana)", "iletisim", 6),
            new("phone", "Telefon (ana)", "iletisim", 6),
            new("notes", "Notlar", "notlar", 12),
        ]),

        new(CrmModule.Entities.Opportunity, "Fırsat",
        [new(General, "Genel bilgiler"), new("tutar", "Tutar ve zamanlama"), new("detay", "Detaylar"), new("notlar", "Notlar ve ek bilgiler")],
        [
            new("temel", "Temel bilgiler", "target"),
            new("tutar", "Tutar ve zamanlama", "coins", Tab: "tutar"),
            new("detay", "Detaylar", "clipboard-list", Tab: "detay"),
            new("aciklama", "Açıklama ve notlar", "message-square", Tab: "notlar"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "notlar"),
        ],
        [
            new("title", "Fırsat adı", "temel", 12, Required: true, Fixed: true),
            new("companyId", "Müşteri (firma)", "temel", 6, Required: true, Fixed: true),
            new("contactId", "İlgili kişi", "temel", 6),
            new("stage", "Aşama", "temel", 4, Fixed: true),
            new("priority", "Öncelik", "temel", 4, Fixed: true),
            new("type", "Fırsat tipi", "temel", 4),
            new("source", "Kaynak", "temel", 6),
            new("ownerId", "Satış sorumlusu", "temel", 6),
            new("amount", "Tahmini tutar", "tutar", 4),
            new("probability", "Olasılık (%)", "tutar", 4),
            new("currency", "Para birimi", "tutar", 4, Fixed: true),
            new("expectedCloseDate", "Tahmini kapanış tarihi", "tutar", 4),
            new("targetDecisionDate", "Hedef karar tarihi", "tutar", 4),
            new("customerBudget", "Müşteri bütçesi", "tutar", 4),
            new("budgetStatus", "Bütçe durumu", "tutar", 4, Fixed: true),
            new("nextAction", "Sonraki aksiyon", "tutar", 4),
            new("nextActionDate", "Aksiyon tarihi", "tutar", 4),
            new("needDescription", "İhtiyaç", "detay", 6),
            new("currentProblem", "Mevcut sorun", "detay", 6),
            new("expectedBenefit", "Beklenen fayda", "detay", 6),
            new("scope", "Kapsam", "detay", 6),
            new("successCriteria", "Başarı ölçütleri", "detay", 6),
            new("risks", "Riskler ve engeller", "detay", 6),
            new("decisionProcess", "Karar süreci", "detay", 12),
            new("description", "Açıklama", "aciklama", 12),
            new("notes", "Notlar", "aciklama", 12),
        ]),

        new(CrmModule.Entities.Lead, "Lead",
        [new(General, "Talep ve sahip"), new("eslestirme", "Eşleştirme ve bütçe"), new("ihtiyac", "İhtiyaç ve ek bilgiler")],
        [
            new("talep", "Talep", "inbox"),
            new("sahip", "Talep sahibi", "user"),
            new("eslestirme", "Eşleştirme ve sahiplik", "user-check", Tab: "eslestirme"),
            new("butce", "Bütçe ve zamanlama", "coins", Tab: "eslestirme"),
            new("ihtiyac", "İhtiyaç ve açıklama", "message-square", Tab: "ihtiyac"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "ihtiyac"),
        ],
        [
            new("subject", "Konu", "talep", 12, Required: true, Fixed: true),
            new("priority", "Öncelik", "talep", 4, Fixed: true),
            new("source", "Kaynak", "talep", 4),
            new("sourceDetail", "Kaynak ayrıntısı", "talep", 4),
            new("productInterest", "İlgilenilen ürün/hizmet", "talep", 6),
            new("industry", "Sektör", "talep", 6),
            new("firstName", "Ad", "sahip", 4),
            new("lastName", "Soyad", "sahip", 4),
            new("jobTitle", "Unvan", "sahip", 4),
            new("companyName", "Firma adı", "sahip", 6),
            new("email", "E-posta", "sahip", 6),
            new("phone", "Telefon", "sahip", 4),
            new("website", "Web sitesi", "sahip", 4),
            new("city", "Şehir", "sahip", 3),
            new("country", "Ülke (2 harf)", "sahip", 3),
            new("ownerId", "Sorumlu", "eslestirme", 4, Fixed: true),
            new("companyId", "Mevcut müşteri firması", "eslestirme", 4),
            new("contactId", "Mevcut kişi", "eslestirme", 4),
            new("estimatedBudget", "Tahmini bütçe", "butce", 4),
            new("currency", "Para birimi", "butce", 4, Fixed: true),
            new("targetPurchaseDate", "Hedef satın alma tarihi", "butce", 4),
            new("needDescription", "İhtiyaç", "ihtiyac", 12),
            new("description", "Açıklama / talep metni", "ihtiyac", 12),
        ]),

        new(CrmModule.Entities.Activity, "Aktivite",
        [new(General, "Aktivite ve zaman"), new("iliskili", "İlişkili kayıtlar"), new("yer", "Yer ve ayrıntılar")],
        [
            new("aktivite", "Aktivite", "activity"),
            new("zaman", "Zaman", "calendar"),
            new("iliskili", "İlişkili kayıtlar", "users", Tab: "iliskili"),
            new("yer", "Yer ve ayrıntılar", "map-pin", Tab: "yer"),
            new(Extras, "Ek bilgiler", "square-pen", Tab: "yer"),
        ],
        [
            new("subject", "Konu", "aktivite", 12, Required: true, Fixed: true),
            new("type", "Aktivite türü", "aktivite", 4, Fixed: true),
            new("priority", "Öncelik", "aktivite", 4, Fixed: true),
            new("dueAt", "Son tarih", "aktivite", 4),
            new("isAllDay", "Tüm gün", "zaman", 12, Fixed: true),
            new("startAt", "Başlangıç", "zaman", 6),
            new("endAt", "Bitiş", "zaman", 6),
            new("estimatedMinutes", "Tahmini süre (dk)", "zaman", 4),
            new("ownerId", "Atanan kullanıcı", "iliskili", 6, Fixed: true),
            new("companyId", "İlgili firma", "iliskili", 6),
            new("contactId", "İlgili kişi", "iliskili", 6),
            new("opportunityId", "Fırsat", "iliskili", 6),
            new("location", "Konum", "yer", 6),
            new("onlineMeetingUrl", "Toplantı bağlantısı", "yer", 6),
            new("isPrivate", "Özel", "yer", 12, Fixed: true),
            new("logAsDone", "Geçmişte gerçekleşti", "yer", 12, Fixed: true),
            new("notes", "Açıklama", "yer", 12),
        ]),
    ];
}
