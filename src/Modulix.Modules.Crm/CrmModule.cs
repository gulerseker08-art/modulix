using Microsoft.Extensions.DependencyInjection;
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
                Permissions.OpportunitiesRead, Permissions.OpportunitiesWrite,
                Permissions.ActivitiesRead, Permissions.ActivitiesWrite,
            ]),
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
