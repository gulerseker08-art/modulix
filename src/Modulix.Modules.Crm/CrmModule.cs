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
        new(Permissions.CompaniesRead, "Firmaları görüntüler"),
        new(Permissions.CompaniesWrite, "Firma ekler, düzenler ve siler"),
        new(Permissions.ContactsRead, "Kişileri görüntüler"),
        new(Permissions.ContactsWrite, "Kişi ekler, düzenler ve siler"),
        new(Permissions.OpportunitiesRead, "Fırsatları görüntüler"),
        new(Permissions.OpportunitiesWrite, "Fırsat ekler, düzenler ve siler"),
        new(Permissions.ActivitiesRead, "Aktiviteleri görüntüler"),
        new(Permissions.ActivitiesWrite, "Aktivite ekler, düzenler ve siler"),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IRecordStore, CompanyRecordStore>();
        services.AddScoped<IRecordStore, ContactRecordStore>();
        services.AddScoped<IRecordStore, OpportunityRecordStore>();
        services.AddScoped<IRecordStore, ActivityRecordStore>();
    }
}
