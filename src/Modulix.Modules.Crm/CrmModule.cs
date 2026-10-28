using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
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
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string OpportunityStage = "crm.opportunity-stage";
        public const string CompanyStatus = "crm.company-status";
        public const string CompanySource = "crm.company-source";
        public const string ActivityType = "crm.activity-type";
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
        new(Permissions.ActivitiesWrite, "Aktivite ekler, düzenler ve siler", "Aktiviteler"),
    ];

    /// <summary>Devre dışı bırakılan kişinin firmaları, açık fırsatları ve açık aktiviteleri devredilir.</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Company, "firma") { Path = "crm/firmalar/{id}" },
        new(Entities.Opportunity, "açık fırsat", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/firsatlar" },
        new(Entities.Activity, "açık aktivite", new Dictionary<string, string> { ["open"] = "true" }) { Path = "crm/aktiviteler" },
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
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite,
            ])
        {
            // Y8: temsilci yalnızca sorumlusu olduğu kayıtları görür; ekip lideri için kapsam rolde genişletilir.
            Scope = RecordScope.Own,
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
    ];

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
            [],
            [new("Call", "Arama"), new("Meeting", "Toplantı"), new("Email", "E-posta"), new("Task", "Görev"), new("Note", "Not")])
        {
            Default = "Task",
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
            ["lostReason"] = "Kaybetme nedeni", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "title")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Activity, "Aktivite (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["type"] = "Tür", ["subject"] = "Konu", ["dueAt"] = "Tarih", ["completedAt"] = "Tamamlanma", ["companyId"] = "Firma",
            ["contactId"] = "Kişi", ["opportunityId"] = "Fırsat", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "subject")
        {
            UserFields = ["ownerId"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["opportunityId"] = Entities.Opportunity,
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
    }
}
