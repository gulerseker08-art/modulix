using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Crm;

/// <summary>CRM: firma, kişi, fırsat, aktivite.</summary>
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

    public string Key => ModuleKey;

    public string DisplayName => "CRM";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.CompaniesRead, "Firmaları görüntüler"),
        new(Permissions.CompaniesWrite, "Firma ekler ve düzenler"),
        new(Permissions.ContactsRead, "Kişileri görüntüler"),
        new(Permissions.ContactsWrite, "Kişi ekler ve düzenler"),
        new(Permissions.OpportunitiesRead, "Fırsatları görüntüler"),
        new(Permissions.OpportunitiesWrite, "Fırsat ekler ve düzenler"),
        new(Permissions.ActivitiesRead, "Aktiviteleri görüntüler"),
        new(Permissions.ActivitiesWrite, "Aktivite ekler ve düzenler"),
    ];
}
