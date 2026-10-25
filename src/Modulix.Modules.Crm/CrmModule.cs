using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
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
        public const string Opportunity = "crm.opportunity";
        public const string Activity = "crm.activity";
    }

    public string Key => ModuleKey;

    public string DisplayName => "CRM";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.CompaniesRead, "Firmaları görüntüler", "Firmalar"),
        new(Permissions.CompaniesWrite, "Firma ekler, düzenler ve siler", "Firmalar"),
        new(Permissions.ContactsRead, "Kişileri görüntüler", "Kişiler"),
        new(Permissions.ContactsWrite, "Kişi ekler, düzenler ve siler", "Kişiler"),
        new(Permissions.OpportunitiesRead, "Fırsatları görüntüler", "Fırsatlar"),
        new(Permissions.OpportunitiesWrite, "Fırsat ekler, düzenler ve siler", "Fırsatlar"),
        new(Permissions.OpportunityAmountsRead, "Fırsat tutarını ve olasılığını görür", "Fırsat tutarları"),
        new(Permissions.ActivitiesRead, "Aktiviteleri görüntüler", "Aktiviteler"),
        new(Permissions.ActivitiesWrite, "Aktivite ekler, düzenler ve siler", "Aktiviteler"),
    ];

    /// <summary>Devre dışı bırakılan kişinin firmaları, açık fırsatları ve açık aktiviteleri devredilir.</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Company, "firma"),
        new(Entities.Opportunity, "açık fırsat", new Dictionary<string, string> { ["open"] = "true" }),
        new(Entities.Activity, "açık aktivite", new Dictionary<string, string> { ["open"] = "true" }),
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

    /// <summary>Denetim kaydı (Y5): alanların görünen adları; seçenek adları arayüzdekilerle aynıdır.</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Company, "Firma (CRM)", Permissions.CompaniesRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["taxNumber"] = "Vergi no", ["phone"] = "Telefon", ["email"] = "E-posta", ["website"] = "Web sitesi",
            ["city"] = "Şehir", ["address"] = "Adres", ["industry"] = "Sektör", ["status"] = "Durum", ["source"] = "Kaynak",
            ["tags"] = "Etiketler", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "name")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string> { ["Prospect"] = "Potansiyel", ["Customer"] = "Müşteri", ["Inactive"] = "Pasif" },
                ["source"] = new Dictionary<string, string>
                {
                    ["Referral"] = "Tavsiye", ["Website"] = "Web sitesi", ["Event"] = "Etkinlik / fuar", ["Advertising"] = "Reklam",
                    ["ColdCall"] = "Soğuk arama", ["Other"] = "Diğer",
                },
            },
            UserFields = ["ownerId"],
        },
        new(Entities.Contact, "Kişi (CRM)", Permissions.ContactsRead, new Dictionary<string, string>
        {
            ["firstName"] = "Ad", ["lastName"] = "Soyad", ["companyId"] = "Firma", ["title"] = "Unvan", ["email"] = "E-posta",
            ["phone"] = "Telefon", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        })
        {
            TitleFields = ["firstName", "lastName"],
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company },
        },
        new(Entities.Opportunity, "Fırsat (CRM)", Permissions.OpportunitiesRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["companyId"] = "Firma", ["contactId"] = "Kişi", ["stage"] = "Aşama", ["amount"] = "Tutar",
            ["currency"] = "Para birimi", ["expectedCloseDate"] = "Tahmini kapanış", ["probability"] = "Olasılık (%)",
            ["lostReason"] = "Kaybetme nedeni", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "title")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["stage"] = new Dictionary<string, string>
                {
                    ["Lead"] = "Aday", ["Qualified"] = "Nitelikli", ["Proposal"] = "Teklif", ["Negotiation"] = "Pazarlık",
                    ["Won"] = "Kazanıldı", ["Lost"] = "Kaybedildi",
                },
            },
            UserFields = ["ownerId"],
            References = new Dictionary<string, string> { ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact },
        },
        new(Entities.Activity, "Aktivite (CRM)", Permissions.ActivitiesRead, new Dictionary<string, string>
        {
            ["type"] = "Tür", ["subject"] = "Konu", ["dueAt"] = "Tarih", ["completedAt"] = "Tamamlanma", ["companyId"] = "Firma",
            ["contactId"] = "Kişi", ["opportunityId"] = "Fırsat", ["ownerId"] = "Sorumlu", ["notes"] = "Notlar",
        }, "subject")
        {
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["type"] = new Dictionary<string, string>
                {
                    ["Call"] = "Arama", ["Meeting"] = "Toplantı", ["Email"] = "E-posta", ["Task"] = "Görev", ["Note"] = "Not",
                },
            },
            UserFields = ["ownerId"],
            References = new Dictionary<string, string>
            {
                ["companyId"] = Entities.Company, ["contactId"] = Entities.Contact, ["opportunityId"] = Entities.Opportunity,
            },
        },
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<CrmStoreContext>();
        services.AddScoped<IRecordStore, CompanyRecordStore>();
        services.AddScoped<IRecordStore, ContactRecordStore>();
        services.AddScoped<IRecordStore, OpportunityRecordStore>();
        services.AddScoped<IRecordStore, ActivityRecordStore>();
    }
}
