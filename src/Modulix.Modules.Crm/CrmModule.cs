using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Leads;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM: firma, kişi, fırsat, aktivite. İlk sektör modülü ve diğerlerinin şablonu:
/// <list type="bullet">
///   <item>Entity'ler <c>Domain</c>, tablo yapılandırması <c>Persistence</c> (şema = modül anahtarı).</item>
///   <item>Her entity için bir kayıt deposu <c>Records</c>; erişim yalnızca kayıt boru hattından geçer.</item>
///   <item>Uç noktalar ve ekranlar modülü değil, yalnızca bu sınıftaki anahtarları ve izinleri bilir.</item>
/// </list>
/// </summary>
public sealed class CrmModule : IModule
{
    public const string ModuleKey = "crm";

    public static class Permissions
    {
        public const string CompaniesRead = "crm.companies.read";
        public const string CompaniesWrite = "crm.companies.write";
        public const string ContactsRead = "crm.contacts.read";
        public const string ContactsWrite = "crm.contacts.write";

        /// <summary>Firmaları / kişileri birleştirir (ADR-0013). Yazma izninden ayrıdır; hiçbir hazır role verilmez.</summary>
        public const string CompaniesMerge = "crm.companies.merge";
        public const string ContactsMerge = "crm.contacts.merge";
        public const string OpportunitiesRead = "crm.opportunities.read";
        public const string OpportunitiesWrite = "crm.opportunities.write";

        /// <summary>Fırsat tutarı ve olasılığı (Y8b, hassas alan). Olmayan kişi fırsatı görür, bu alanları görmez ve yazamaz.</summary>
        public const string OpportunityAmountsRead = "crm.opportunity-amounts.read";
        public const string ActivitiesRead = "crm.activities.read";
        public const string ActivitiesWrite = "crm.activities.write";

        // Aktivite yaşam döngüsü (ADR-0015). Oluşturma/düzenleme/tamamlama/iptal/silme `write` ile birlikte kalır (mevcut rollerin davranışı
        // değişmesin diye); aşağıdakiler ayrı izinlerdir.

        /// <summary>Aktiviteyi başkasına atar (kendine almak `write` yeter).</summary>
        public const string ActivitiesAssign = "crm.activities.assign";

        /// <summary>Kapanmış (tamamlanmış, iptal, ulaşılamadı, katılım olmadı) aktiviteyi yeniden açar.</summary>
        public const string ActivitiesReopen = "crm.activities.reopen";

        /// <summary>Başkalarının takvimini ve ekip görünümünü görür (yalnız meşguliyet; özel aktivite ayrıntısı değil).</summary>
        public const string ActivitiesManageCalendar = "crm.activities.manage-calendar";

        /// <summary>Özel aktivitelerin ayrıntısını (başlık, açıklama, ilişkiler) kapsamındaysa görür. Hiçbir hazır role verilmez.</summary>
        public const string ActivitiesViewPrivate = "crm.activities.view-private";

        // Lead yönetimi. Atama, nitelendirme, eleme, dönüşüm ve dışa aktarma ayrı izinlerdir; alan düzenleme izninden bağımsızdır.
        public const string LeadsRead = "crm.leads.read";
        public const string LeadsCreate = "crm.leads.create";
        public const string LeadsUpdate = "crm.leads.update";
        public const string LeadsDelete = "crm.leads.delete";
        public const string LeadsAssign = "crm.leads.assign";
        public const string LeadsQualify = "crm.leads.qualify";
        public const string LeadsDisqualify = "crm.leads.disqualify";
        public const string LeadsConvert = "crm.leads.convert";
        public const string LeadsExport = "crm.leads.export";

        /// <summary>Lead toplama formlarını/entegrasyonlarını yönetir (sır yenileme dahil). Hiçbir hazır role verilmez.</summary>
        public const string LeadFormsManage = "crm.lead-forms.manage";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Company = "crm.company";
        public const string Contact = "crm.contact";
        public const string CompanyContact = "crm.company-contact";
        public const string Location = "crm.location";
        public const string Address = "crm.address";
        public const string ContactPoint = "crm.contact-point";
        public const string PersonContactPoint = "crm.person-contact-point";
        public const string CompanyRelation = "crm.company-relation";
        public const string Opportunity = "crm.opportunity";
        public const string Activity = "crm.activity";
        public const string ActivityLink = "crm.activity-link";
        public const string ActivityParticipant = "crm.activity-participant";
        public const string Lead = "crm.lead";
        public const string LeadForm = "crm.lead-form";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string OpportunityStage = "crm.opportunity-stage";
        public const string CompanyStatus = "crm.company-status";
        public const string CompanySource = "crm.company-source";
        public const string ActivityType = "crm.activity-type";
        public const string LeadSource = "crm.lead-source";
        public const string LeadDisqualificationReason = "crm.lead-disqualification-reason";
    }

    /// <summary>
    /// Aktivite sonucu. Lead'in "ilk temas"ı yalnızca gerçekleşen temaslardan türetilir: tamamlanmış Toplantı; sonucu
    /// <c>Reached</c> olan tamamlanmış Arama; sonucu <c>Reached</c> ya da <c>Sent</c> olan tamamlanmış E-posta. Görev, Not ve
    /// sonucu boş/<c>NoAnswer</c> olan aramalar temas sayılmaz; "arama planlandı" temas değildir.
    /// </summary>
    public static class ActivityOutcomes
    {
        public const string Reached = "Reached";
        public const string NoAnswer = "NoAnswer";
        public const string Sent = "Sent";

        public static IReadOnlyList<string> All { get; } = [Reached, NoAnswer, Sent];
    }

    /// <summary>Fırsat aşamasının kategorileri: kazanılan ve kaybedilen fırsat kapanmıştır (devir, açık fırsat süzgeci).</summary>
    public static class StageCategories
    {
        public const string Open = "open";
        public const string Won = "won";
        public const string Lost = "lost";
    }

    /// <summary>İlişki rolü anahtarları (ADR-0011); adlar Shared'daki <c>CrmRelationshipRoles</c> ile aynıdır.</summary>
    public static IReadOnlyList<string> RelationshipRoles { get; } = ["Purchasing", "Finance", "Technical", "Management", "Other"];

    /// <summary>Lokasyon türleri (ADR-0012); adlar Shared'daki <c>CrmLocationTypes</c> ile aynıdır.</summary>
    public static IReadOnlyList<string> LocationTypes { get; } = ["HeadOffice", "Branch", "Plant", "Warehouse", "Store", "Delivery", "Billing", "Other"];

    public static IReadOnlyList<string> AddressTypes { get; } = ["Registered", "Billing", "Shipping", "Office", "Other"];

    public static IReadOnlyList<string> ContactPointTypes { get; } = ["Work", "Mobile", "Switchboard", "Billing", "Support", "Personal", "Other"];

    public static IReadOnlyList<string> ContactChannels { get; } = ["Phone", "Email"];

    public static IReadOnlyList<string> RelationTypes { get; } = ["Subsidiary", "Affiliate", "Other"];

    /// <summary>Firma durumunun kategorileri.</summary>
    public static class CompanyStatusCategories
    {
        public const string Prospect = "prospect";
        public const string Customer = "customer";
        public const string Inactive = "inactive";
    }

    public string Key => ModuleKey;

    public string DisplayName => "CRM";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.CompaniesRead, "Firmaları görüntüler", "Firmalar"),
        new(Permissions.CompaniesWrite, "Firma ekler, düzenler ve siler", "Firmalar"),
        new(Permissions.ContactsRead, "Kişileri görüntüler", "Kişiler"),
        new(Permissions.ContactsWrite, "Kişi ekler, düzenler ve siler", "Kişiler"),
        new(Permissions.CompaniesMerge, "Mükerrer firmaları birleştirir", "Firmalar"),
        new(Permissions.ContactsMerge, "Mükerrer kişileri birleştirir", "Kişiler"),
        new(Permissions.OpportunitiesRead, "Fırsatları görüntüler", "Fırsatlar"),
        new(Permissions.OpportunitiesWrite, "Fırsat ekler, düzenler ve siler", "Fırsatlar"),
        new(Permissions.OpportunityAmountsRead, "Fırsat tutarını ve olasılığını görür", "Fırsat tutarları"),
        new(Permissions.ActivitiesRead, "Aktiviteleri görüntüler", "Aktiviteler"),
        new(Permissions.ActivitiesWrite, "Aktivite ekler, düzenler, tamamlar, iptal eder ve siler", "Aktiviteler"),
        new(Permissions.ActivitiesAssign, "Aktiviteyi başka kullanıcıya atar", "Aktiviteler"),
        new(Permissions.ActivitiesReopen, "Kapanmış aktiviteyi yeniden açar", "Aktiviteler"),
        new(Permissions.ActivitiesManageCalendar, "Ekip takvimini ve başkalarının meşguliyetini görür", "Aktiviteler"),
        new(Permissions.ActivitiesViewPrivate, "Özel aktivitelerin ayrıntısını görür", "Aktiviteler"),
        new(Permissions.LeadsRead, "Lead'leri görüntüler", "Lead'ler"),
        new(Permissions.LeadsCreate, "Lead oluşturur", "Lead'ler"),
        new(Permissions.LeadsUpdate, "Lead alanlarını düzenler", "Lead'ler"),
        new(Permissions.LeadsDelete, "Lead siler", "Lead'ler"),
        new(Permissions.LeadsAssign, "Lead atar ve atama bekleyen Lead'leri görür", "Lead'ler"),
        new(Permissions.LeadsQualify, "Lead'i nitelendirir ve daha sonra takibe alır", "Lead'ler"),
        new(Permissions.LeadsDisqualify, "Lead'i eler ve elenen Lead'i yeniden açar", "Lead'ler"),
        new(Permissions.LeadsConvert, "Nitelikli Lead'i fırsata dönüştürür", "Lead'ler"),
        new(Permissions.LeadsExport, "Lead listesini dışa aktarır", "Lead'ler"),
        new(Permissions.LeadFormsManage, "Lead toplama formlarını ve entegrasyon sırlarını yönetir", "Lead formları"),
    ];

    /// <summary>Devre dışı bırakılan kişinin firmaları, açık fırsatları ve açık aktiviteleri devredilir.</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Company, "firma") { Path = "crm/firmalar/{id}" },
        new(Entities.Opportunity, "açık fırsat", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/firsatlar" },
        new(Entities.Activity, "açık aktivite", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/aktiviteler" },
        new(Entities.Lead, "açık lead", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/leadler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "crm.sales-rep",
            "Satış temsilcisi",
            "Firmaları, kişileri, fırsatları ve aktiviteleri görüntüler ve düzenler.",
            [
                Permissions.CompaniesRead, Permissions.CompaniesWrite,
                Permissions.ContactsRead, Permissions.ContactsWrite,
                Permissions.OpportunitiesRead, Permissions.OpportunitiesWrite, Permissions.OpportunityAmountsRead,
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite, Permissions.ActivitiesReopen,
                Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadsUpdate,
                Permissions.LeadsQualify, Permissions.LeadsDisqualify, Permissions.LeadsConvert,
            ])
        {
            // Y8: temsilci yalnızca sorumlusu olduğu kayıtları görür; ekip lideri için kapsam rolde genişletilir.
            Scope = RecordScope.Own,
        },
        // Lead dağıtımı: atama bekleyen havuzu görür, Lead açar ve atar; nitelendirme/dönüşüm satış tarafındadır.
        new(
            "crm.lead-coordinator",
            "Lead koordinatörü",
            "Lead'leri toplar, atama bekleyenleri sorumlulara dağıtır ve raporlar.",
            [
                Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadsUpdate, Permissions.LeadsAssign, Permissions.LeadsExport,
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite, Permissions.ActivitiesAssign, Permissions.ActivitiesReopen,
                Permissions.ActivitiesManageCalendar,
            ])
        {
            Scope = RecordScope.All,
        },
    ];

    /// <summary>
    /// Hassas alanlar (Y8b): tutar ve olasılık. Para birimi tek başına bir şey söylemediği için hassas değildir (ekran onu
    /// her zaman gönderir).
    /// </summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Opportunity, Permissions.OpportunityAmountsRead, ["amount", "probability"]),
    ];

    /// <summary>Özel alanlar (Y9a): firma dört kayıt türüne de kendi alanlarını ekleyebilir.</summary>
    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Company, "Firma"),
        new(Entities.Contact, "Kişi"),
        new(Entities.Opportunity, "Fırsat"),
        new(Entities.Activity, "Aktivite"),
        new(Entities.Lead, "Lead"),
    ];

    /// <summary>Lookup alanları (ADR-0016); tanımlar <see cref="CrmLookupDefinitions"/>'tadır.</summary>
    IReadOnlyList<LookupDefinition> IModule.Lookups => CrmLookupDefinitions.All;

    /// <summary>
    /// Seçim listeleri (Y9b). Hazır değerlerin anahtarları eski enum adlarıdır; kayıtlarda ve API'de bunlar durur.
    /// Firma adlarını, sırasını ve etkinliğini değiştirir; yeni değer eklerken kategorisini seçer.
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.OpportunityStage,
            "Fırsat aşamaları",
            Entities.Opportunity,
            "stage",
            Required: true,
            [new(StageCategories.Open, "Açık"), new(StageCategories.Won, "Kazanıldı"), new(StageCategories.Lost, "Kaybedildi")],
            [
                new("Lead", "Aday", StageCategories.Open),
                new("Qualified", "Nitelikli", StageCategories.Open),
                new("Proposal", "Teklif", StageCategories.Open),
                new("Negotiation", "Pazarlık", StageCategories.Open),
                new("Won", "Kazanıldı", StageCategories.Won),
                new("Lost", "Kaybedildi", StageCategories.Lost),
            ]),
        new(
            Lists.CompanyStatus,
            "Firma durumu",
            Entities.Company,
            "status",
            Required: true,
            [
                new(CompanyStatusCategories.Prospect, "Potansiyel"),
                new(CompanyStatusCategories.Customer, "Müşteri"),
                new(CompanyStatusCategories.Inactive, "Pasif"),
            ],
            [
                new("Prospect", "Potansiyel", CompanyStatusCategories.Prospect),
                new("Customer", "Müşteri", CompanyStatusCategories.Customer),
                new("Inactive", "Pasif", CompanyStatusCategories.Inactive),
            ]),
        new(
            Lists.CompanySource,
            "Firma kaynağı",
            Entities.Company,
            "source",
            Required: false,
            [],
            [
                new("Referral", "Tavsiye"), new("Website", "Web sitesi"), new("Event", "Etkinlik / fuar"),
                new("Advertising", "Reklam"), new("ColdCall", "Soğuk arama"), new("Other", "Diğer"),
            ]),
        new(
            Lists.ActivityType,
            "Aktivite tipi",
            Entities.Activity,
            "type",
            Required: true,
            // Kategori = aktivite ailesi (durum makinesi, ADR-0015). Kategorisiz değer görev gibi davranır; firma yeni tür eklerken aile seçer.
            [
                new(ActivityFamily.Task, "Görev"), new(ActivityFamily.Call, "Arama"), new(ActivityFamily.Meeting, "Toplantı"),
                new(ActivityFamily.Visit, "Müşteri / saha ziyareti"), new(ActivityFamily.Email, "E-posta"),
            ],
            [
                new("Call", "Arama", ActivityFamily.Call), new("Meeting", "Toplantı", ActivityFamily.Meeting),
                new("Email", "E-posta", ActivityFamily.Email), new("Task", "Görev", ActivityFamily.Task), new("Note", "Not", ActivityFamily.Task),
                new("Visit", "Ziyaret", ActivityFamily.Visit), new("FollowUp", "Takip", ActivityFamily.Task),
            ])
        {
            Default = "Task",
        },
        new(
            Lists.LeadSource,
            "Lead kaynağı",
            Entities.Lead,
            "source",
            Required: false,
            [],
            [
                new("Manual", "Elle giriş"), new("WebForm", "Web formu"), new("Referral", "Tavsiye"), new("Event", "Etkinlik / fuar"),
                new("Advertising", "Reklam"), new("Phone", "Telefon"), new("Email", "E-posta"), new("Other", "Diğer"),
            ]),
        new(
            Lists.LeadDisqualificationReason,
            "Lead eleme nedeni",
            Entities.Lead,
            "disqualificationReason",
            Required: false,
            [],
            [
                new("NoBudget", "Bütçe yok"), new("NoNeed", "İhtiyaç yok"), new("NoResponse", "Ulaşılamadı"), new("Competitor", "Rakibi seçti"),
                new("NotATarget", "Hedef müşteri değil"), new("Duplicate", "Mükerrer"), new("Spam", "Spam / geçersiz"), new("Other", "Diğer"),
            ]),
    ];

    /// <summary>İlk temas hedefi: Lead'in ilk temasının beklenen en uzun süresi. Çalışma saati takvimi olmadığı için TAKVİM saati cinsindendir.</summary>
    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.LeadFirstContactTargetHours, "İlk temas hedefi (takvim saati)", "CRM Lead", SettingType.Text, "24")
        {
            Description = "Lead'in ilk temasının hedeflenen en uzun süresi, takvim saati olarak (iş saati değildir). Boş bırakılırsa hedef uygulanmaz.",
            Pattern = @"^[1-9][0-9]{0,3}$",
            PatternMessage = "1-9999 arası tam sayı girin.",
            MaxLength = 4,
        },
    ];

    /// <summary>Denetim kaydı (Y5): alanların görünen adları. Liste alanlarının değer adları firmanın listesinden gelir (Y9b).</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Company, "Firma (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["kind"] = "Tür", ["taxOffice"] = "Vergi dairesi", ["taxNumber"] = "Vergi no", ["phone"] = "Telefon", ["email"] = "E-posta", ["website"] = "Web sitesi",
            ["city"] = "Şehir", ["address"] = "Adres", ["industry"] = "Sektör", ["status"] = "Durum", ["source"] = "Kaynak",
            ["tags"] = "Etiketler", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar", ["partyId"] = "Ortak firma kaydı",
        }, "name")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party },
        },
        new(Entities.Contact, "Kişi (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["firstName"] = "Ad", ["lastName"] = "Soyad", ["companyId"] = "Firma", ["title"] = "Unvan", ["email"] = "E-posta",
            ["phone"] = "Telefon", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar", ["personId"] = "Ortak kişi kaydı",
        })
        {
            TitleFields = ["firstName", "lastName"],
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["personId"] = SharedEntities.Person },
        },
        new(Entities.CompanyContact, "Firma–kişi ilişkisi (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["contactId"] = "Kişi", ["companyName"] = "Firma adı", ["contactName"] = "Kişi adı",
            ["department"] = "Departman", ["jobTitle"] = "Unvan", ["roles"] = "İlişki rolleri", ["isPrimary"] = "Ana irtibat",
            ["isDecisionMaker"] = "Karar verici", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş", ["isActive"] = "Aktif",
        })
        {
            TitleFields = ["contactName", "companyName"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Location, "Müşteri lokasyonu (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["companyName"] = "Firma adı", ["name"] = "Ad", ["code"] = "Kod", ["type"] = "Tür",
            ["isActive"] = "Aktif", ["contactId"] = "Yetkili kişi", ["notes"] = "Notlar",
        }, "name")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Address, "Adres (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["locationId"] = "Lokasyon", ["type"] = "Tür", ["isPrimary"] = "Ana adres", ["isActive"] = "Aktif",
            ["country"] = "Ülke", ["city"] = "Şehir", ["district"] = "İlçe", ["postalCode"] = "Posta kodu", ["line1"] = "Adres satırı 1",
            ["line2"] = "Adres satırı 2", ["normalizedText"] = "Arama metni",
        }, "line1")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["locationId"] = Entities.Location },
        },
        new(Entities.ContactPoint, "Firma iletişim noktası (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["companyId"] = "Firma", ["locationId"] = "Lokasyon", ["channel"] = "Kanal", ["type"] = "Tür", ["value"] = "Değer",
            ["normalizedValue"] = "Normalleştirilmiş değer", ["isPrimary"] = "Ana", ["isActive"] = "Aktif", ["isShared"] = "Paylaşılan", ["note"] = "Not",
        }, "value")
        {
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["locationId"] = Entities.Location },
        },
        new(Entities.PersonContactPoint, "Kişi iletişim noktası (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["contactId"] = "Kişi", ["companyContactId"] = "Firma–kişi ilişkisi", ["channel"] = "Kanal", ["type"] = "Tür", ["value"] = "Değer",
            ["normalizedValue"] = "Normalleştirilmiş değer", ["isPrimary"] = "Ana", ["isActive"] = "Aktif", ["isShared"] = "Paylaşılan", ["note"] = "Not",
        }, "value")
        {
            References = new Dictionary<string, string> { ["contactId"] = Entities.Contact, ["companyContactId"] = Entities.CompanyContact },
        },
        new(Entities.CompanyRelation, "Firma–firma ilişkisi (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["parentCompanyId"] = "Ana firma", ["childCompanyId"] = "Bağlı firma", ["parentCompanyName"] = "Ana firma adı",
            ["childCompanyName"] = "Bağlı firma adı", ["type"] = "Tür", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş",
            ["isActive"] = "Aktif", ["notes"] = "Notlar",
        })
        {
            TitleFields = ["parentCompanyName", "childCompanyName"],
            References = new Dictionary<string, string> { ["parentCompanyId"] = Entities.Company, ["childCompanyId"] = Entities.Company },
        },
        new(Entities.Opportunity, "Fırsat (CRM)", Permissions.OpportunitiesRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["companyId"] = "Firma", ["contactId"] = "Kişi", ["stage"] = "Aşama", ["amount"] = "Tutar",
            ["currency"] = "Para birimi", ["expectedCloseDate"] = "Tahmini kapanış", ["probability"] = "Olasılık (%)",
            ["lostReason"] = "Kaybetme nedeni", ["sourceLeadId"] = "Kaynak Lead", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "title")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["sourceLeadId"] = Entities.Lead,
            },
        },
        new(Entities.Activity, "Aktivite (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["type"] = "Tür", ["subject"] = "Konu", ["dueAt"] = "Son tarih", ["completedAt"] = "Tamamlanma", ["companyId"] = "Firma",
            ["contactId"] = "Kişi", ["opportunityId"] = "Fırsat", ["leadId"] = "Lead", ["outcome"] = "Sonuç", ["ownerId"] = "Sorumlu", ["notes"] = "Açıklama",
            ["status"] = "Durum", ["statusReason"] = "Durum / sonuç açıklaması", ["priority"] = "Öncelik", ["startAt"] = "Başlangıç", ["endAt"] = "Bitiş",
            ["isAllDay"] = "Tüm gün", ["startDate"] = "Başlangıç günü", ["endDate"] = "Bitiş günü", ["timeZone"] = "Saat dilimi", ["location"] = "Konum",
            ["onlineMeetingUrl"] = "Çevrim içi toplantı", ["isPrivate"] = "Özel", ["estimatedMinutes"] = "Tahmini süre (dk)", ["actualMinutes"] = "Gerçekleşen süre (dk)",
            ["rescheduleCount"] = "Ertelenme sayısı", ["previousActivityId"] = "Önceki aktivite", ["source"] = "Kaynak", ["createdBy"] = "Oluşturan",
            ["externalSource"] = "Dış kaynak", ["externalId"] = "Dış kimlik",
        }, "subject")
        {
            UserFields = ["ownerId", "createdBy"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["opportunityId"] = Entities.Opportunity, ["leadId"] = Entities.Lead,
                ["previousActivityId"] = Entities.Activity,
            },
        },
        new(Entities.ActivityLink, "Aktivite ilişkisi (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["activityId"] = "Aktivite", ["entityKey"] = "Kayıt türü", ["recordId"] = "Kayıt", ["isPrimary"] = "Birincil",
        })
        {
            TitleFields = ["entityKey"],
            References = new Dictionary<string, string> { ["activityId"] = Entities.Activity },
        },
        new(Entities.ActivityParticipant, "Aktivite katılımcısı (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["activityId"] = "Aktivite", ["userId"] = "Kullanıcı", ["contactId"] = "Kişi", ["email"] = "E-posta", ["displayName"] = "Ad", ["role"] = "Rol",
        })
        {
            TitleFields = ["displayName", "email"],
            UserFields = ["userId"],
            References = new Dictionary<string, string> { ["activityId"] = Entities.Activity, ["contactId"] = Entities.Contact },
        },
        new(Entities.LeadForm, "Lead formu (CRM)", Permissions.LeadFormsManage, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["formKey"] = "Form kimliği", ["isActive"] = "Aktif", ["defaultOwnerId"] = "Varsayılan sorumlu",
            ["allowedOrigins"] = "İzinli kaynaklar", ["serverOnly"] = "Yalnızca sunucudan", ["secretRotatedAt"] = "Sır yenilenme",
        }, "name")
        {
            UserFields = ["defaultOwnerId"],
        },
        new(Entities.Lead, "Lead (CRM)", Permissions.LeadsRead, new Dictionary<string, string>
        {
            ["leadNumber"] = "Lead no", ["subject"] = "Konu", ["description"] = "Açıklama", ["status"] = "Durum", ["statusReason"] = "Durum gerekçesi",
            ["priority"] = "Öncelik", ["firstName"] = "Ad", ["lastName"] = "Soyad", ["companyName"] = "Firma adı", ["email"] = "E-posta",
            ["phone"] = "Telefon", ["jobTitle"] = "Unvan", ["website"] = "Web sitesi", ["country"] = "Ülke", ["city"] = "Şehir",
            ["companyId"] = "Mevcut firma", ["contactId"] = "Mevcut kişi", ["productInterest"] = "İlgilenilen ürün/hizmet",
            ["needDescription"] = "İhtiyaç", ["estimatedBudget"] = "Tahmini bütçe", ["currency"] = "Para birimi",
            ["targetPurchaseDate"] = "Hedef satın alma tarihi", ["industry"] = "Sektör", ["source"] = "Kaynak", ["sourceDetail"] = "Kaynak ayrıntısı",
            ["referrer"] = "Yönlendiren", ["externalSource"] = "Dış kaynak", ["externalSubmissionId"] = "Dış gönderim no",
            ["utmSource"] = "UTM kaynak", ["utmMedium"] = "UTM ortam", ["utmCampaign"] = "UTM kampanya", ["firstReceivedAt"] = "İlk geliş",
            ["ownerId"] = "Sorumlu", ["assignedAt"] = "Atanma", ["firstContactAt"] = "İlk temas", ["followUpAt"] = "Takip tarihi",
            ["qualifiedAt"] = "Nitelendirilme", ["disqualifiedAt"] = "Elenme", ["disqualificationReason"] = "Eleme nedeni",
            ["convertedAt"] = "Dönüşüm", ["convertedBy"] = "Dönüştüren", ["convertedCompanyId"] = "Dönüşüm firması",
            ["convertedContactId"] = "Dönüşüm kişisi", ["convertedOpportunityId"] = "Dönüşüm fırsatı",
        }, "subject")
        {
            UserFields = ["ownerId", "convertedBy"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["convertedCompanyId"] = Entities.Company,
                ["convertedContactId"] = Entities.Contact, ["convertedOpportunityId"] = Entities.Opportunity,
            },
        },
    ];

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): firma ve kişi izinleri, firmalar ve kişiler tarafların ve ortak kişilerin uzantısı olduğu
    /// için ortak kaydı da açar. Firma silinince taraf kalır; taraf, CRM firması varken silinemez.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.CompaniesRead, Permissions.CompaniesWrite),
        new(SharedEntities.Person, Permissions.ContactsRead, Permissions.ContactsWrite),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Company, "partyId", "CRM firması"),
        new(SharedEntities.Person, Entities.Contact, "personId", "CRM kişisi"),
    ];

    /// <summary>
    /// Aktivite tamamlanınca Lead'in ilk temasını işler (yalnızca Lead aktiviteleri; sistem adına, audit'li). Abone idempotenttir:
    /// aynı olay tekrar gelse de ilk temas yalnızca daha erken bir temas bulunursa değişir.
    /// </summary>
    IReadOnlyList<EventSubscription> IModule.EventSubscriptions { get; } =
    [
        // Herkese açık Lead toplama ucunun sistem kimliği: olay işlemez, yalnızca "crm.lead-intake" kimliğinin izinlerini tanımlar
        // (web formu isteği oturumsuzdur; Lead'i bu dar izinlerle, sistem adına ve audit'te bu anahtarla oluşturur).
        EventSubscription.For<LeadIntakeAnchor>(
            "crm.lead-intake",
            [DomainEventTypes.RecordDeleted],
            [Entities.LeadForm],
            [Permissions.LeadsRead, Permissions.LeadsCreate, Permissions.LeadFormsManage]),
        EventSubscription.For<LeadFirstContactSubscriber>(
            "crm.lead-first-contact",
            [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated],
            [Entities.Activity],
            [Permissions.ActivitiesRead, Permissions.ActivitiesViewPrivate, Permissions.LeadsRead, Permissions.LeadsUpdate]),
    ];

    /// <summary>Tenant ayar anahtarları.</summary>
    public static class SettingKeys
    {
        public const string LeadFirstContactTargetHours = "crm.lead.first-contact-target-hours";
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<CrmStoreContext>();
        services.AddScoped<ISharedRecordVisibility, CrmSharedRecordVisibility>();
        services.AddScoped<IRecordStore, CompanyRecordStore>();
        services.AddScoped<IRecordStore, ContactRecordStore>();
        services.AddScoped<IRecordStore, CompanyContactRecordStore>();
        services.AddScoped<IRecordStore, LocationRecordStore>();
        services.AddScoped<IRecordStore, AddressRecordStore>();
        services.AddScoped<IRecordStore, CompanyContactPointRecordStore>();
        services.AddScoped<IRecordStore, PersonContactPointRecordStore>();
        services.AddScoped<IRecordStore, CompanyRelationRecordStore>();
        services.AddScoped<IRecordStore, OpportunityRecordStore>();
        services.AddScoped<IRecordStore, ActivityRecordStore>();
        services.AddScoped<IRecordStore, ActivityLinkRecordStore>();
        services.AddScoped<IRecordStore, ActivityParticipantRecordStore>();
        services.AddScoped<IActivityLinkTarget, CompanyLinkTarget>();
        services.AddScoped<IActivityLinkTarget, ContactLinkTarget>();
        services.AddScoped<IActivityLinkTarget, OpportunityLinkTarget>();
        services.AddScoped<IActivityLinkTarget, LeadLinkTarget>();
        services.AddScoped<ActivityWorkflowGate>();
        services.AddScoped<IActivityWorkflow, ActivityWorkflow>();
        services.AddScoped<IRecordStore, LeadRecordStore>();
        services.AddScoped<IRecordStore, LeadFormRecordStore>();
        services.AddScoped<ILeadIntake, LeadIntake>();
        services.AddScoped<ILeadFormAdmin, LeadFormAdmin>();
        services.AddScoped<LeadIntakeAnchor>();
        services.AddScoped<LeadWorkflowGate>();
        services.AddScoped<ILeadQualificationRule, MinimumQualificationRule>();
        services.AddScoped<ILeadWorkflow, LeadWorkflow>();
        services.AddScoped<ILeadConversion, LeadConversion>();
        services.AddScoped<LeadFirstContactSubscriber>();
    }
}
