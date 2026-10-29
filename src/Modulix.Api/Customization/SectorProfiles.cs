using Modulix.Core.Customization;
using Modulix.Modules.Crm;
using Modulix.Modules.Logistics;

namespace Modulix.Api.Customization;

/// <summary>
/// Sektör profilleri (Y9c). Profil bir başlangıç paketidir: modüller, hazır alanlar ve liste değerleri. Tanım değişirse
/// önceden uygulanmış firmalara kendiliğinden yansımaz; profil yeniden uygulanırsa yalnızca eksikler eklenir.
/// </summary>
internal static class SectorProfiles
{
    public static IEnumerable<SectorProfile> Create() =>
    [
        new(
            "general",
            "Genel",
            "Müşteri takibi için CRM; hazır alan eklenmez.",
            [CrmModule.ModuleKey],
            [],
            []),
        new(
            "logistics",
            "Lojistik firması",
            "CRM ve Lojistik; fırsatlarda güzergâh, araç tipi ve yük tipi.",
            [CrmModule.ModuleKey, LogisticsModule.ModuleKey],
            [
                new(CrmModule.Entities.Opportunity, "Güzergâh", CustomFieldType.Text, ShowInList: true),
                new(CrmModule.Entities.Opportunity, "Araç tipi", CustomFieldType.Choice, ShowInList: true, Options: ["Tır", "Kamyon", "Kamyonet", "Frigorifik"]),
                new(CrmModule.Entities.Opportunity, "Yük tipi", CustomFieldType.Choice, Options: ["Paletli", "Dökme", "Soğuk zincir", "Parsiyel"]),
            ],
            [
                new(CrmModule.Lists.CompanySource, "Yük borsası"),
            ]),
        new(
            "clinic",
            "Klinik",
            "Hasta takibi için CRM; kişilerde doğum tarihi ve sigorta, tedaviye göre aşamalar.",
            [CrmModule.ModuleKey],
            [
                new(CrmModule.Entities.Contact, "Doğum tarihi", CustomFieldType.Date),
                new(CrmModule.Entities.Contact, "Sigorta", CustomFieldType.Choice, ShowInList: true, Options: ["SGK", "Özel sigorta", "Yok"]),
                new(CrmModule.Entities.Opportunity, "Tedavi türü", CustomFieldType.Text, ShowInList: true),
            ],
            [
                new(CrmModule.Lists.OpportunityStage, "Ön görüşme", CrmModule.StageCategories.Open),
                new(CrmModule.Lists.OpportunityStage, "Tedavi planı", CrmModule.StageCategories.Open),
                new(CrmModule.Lists.ActivityType, "Muayene", "meeting"),
                new(CrmModule.Lists.ActivityType, "Kontrol", "meeting"),
            ]),
        new(
            "manufacturer",
            "Üretici",
            "Bayi ve numune süreci için CRM; fırsatlarda ürün grubu ve teslim süresi.",
            [CrmModule.ModuleKey],
            [
                new(CrmModule.Entities.Opportunity, "Ürün grubu", CustomFieldType.Text, ShowInList: true),
                new(CrmModule.Entities.Opportunity, "Teslim süresi (gün)", CustomFieldType.Number),
                new(CrmModule.Entities.Opportunity, "Numune gönderildi", CustomFieldType.YesNo),
            ],
            [
                new(CrmModule.Lists.OpportunityStage, "Numune", CrmModule.StageCategories.Open),
                new(CrmModule.Lists.CompanySource, "Bayi"),
            ]),
    ];
}
