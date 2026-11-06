using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM'in içe aktarma tanımları (ADR-0033). Sütunlar elle girilebilen alanlardır; durum ve dönüşüm gibi iş akışı alanları dışarıdadır. Sorumlu boşsa kayıt
/// yükleyene atanır. Firma bağlantısı vergi numarası ya da tam adla çözülür: önce firmalar, sonra kişiler ve müşteri adayları yüklenir.
/// </summary>
internal static class CrmImports
{
    private static readonly ImportReference CompanyReference = new(CrmModule.Entities.Company, ["taxNumber", "name"]);

    public static IReadOnlyList<ImportDefinition> All { get; } =
    [
        new(CrmModule.Entities.Company, "Firma", CrmModule.Permissions.CompaniesWrite,
        [
            new("name", "Firma adı", Required: true),
            new("kind", "Firma türü", ImportFieldType.Choice)
            {
                Options = [new(nameof(PartyKind.Organization), "Tüzel kişi"), new(nameof(PartyKind.Individual), "Gerçek kişi")],
            },
            new("taxNumber", "Vergi numarası"),
            new("taxOffice", "Vergi dairesi"),
            new("phone", "Telefon"),
            new("email", "E-posta"),
            new("website", "Web sitesi"),
            new("city", "Şehir"),
            new("address", "Adres"),
            new("industry", "Sektör"),
            new("status", "Durum", ImportFieldType.Choice) { ChoiceListKey = CrmModule.Lists.CompanyStatus },
            new("source", "Kaynak", ImportFieldType.Choice) { ChoiceListKey = CrmModule.Lists.CompanySource },
            new("tags", "Etiketler"),
            new(OwnedRecordType.OwnerField, "Sorumlu", ImportFieldType.User),
            new("notes", "Notlar"),
        ])
        {
            MatchRules =
            [
                new("taxNumber", null, Exact: true),
                new("name", "name"),
                new("email", "contactValue"),
                new("phone", "contactValue"),
            ],
        },
        new(CrmModule.Entities.Contact, "Kişi", CrmModule.Permissions.ContactsWrite,
        [
            new("firstName", "Ad", Required: true),
            new("lastName", "Soyad", Required: true),
            new("companyId", "Firma", ImportFieldType.Reference) { Reference = CompanyReference },
            new("title", "Unvan"),
            new("email", "E-posta"),
            new("phone", "Telefon"),
            new(OwnedRecordType.OwnerField, "Sorumlu", ImportFieldType.User),
            new("notes", "Notlar"),
        ])
        {
            MatchRules = [new("email", "contactValue"), new("phone", "contactValue")],
        },
        new(CrmModule.Entities.Lead, "Müşteri adayı", CrmModule.Permissions.LeadsCreate,
        [
            new("subject", "Konu", Required: true),
            new("firstName", "Ad"),
            new("lastName", "Soyad"),
            new("companyName", "Firma adı"),
            new("companyId", "Bağlı firma", ImportFieldType.Reference) { Reference = CompanyReference },
            new("email", "E-posta"),
            new("phone", "Telefon"),
            new("jobTitle", "Unvan"),
            new("website", "Web sitesi"),
            new("country", "Ülke"),
            new("city", "Şehir"),
            new("industry", "Sektör"),
            new("priority", "Öncelik", ImportFieldType.Choice)
            {
                Options =
                [
                    new(LeadPriority.Low, "Düşük"), new(LeadPriority.Normal, "Normal"),
                    new(LeadPriority.High, "Yüksek"), new(LeadPriority.Urgent, "Acil"),
                ],
            },
            new("source", "Kaynak", ImportFieldType.Choice) { ChoiceListKey = CrmModule.Lists.LeadSource },
            new("sourceDetail", "Kaynak ayrıntısı"),
            new("productInterest", "İlgilenilen ürün"),
            new("needDescription", "İhtiyaç"),
            new("estimatedBudget", "Tahmini bütçe", ImportFieldType.Decimal),
            new("currency", "Para birimi"),
            new("targetPurchaseDate", "Hedef satın alma tarihi", ImportFieldType.Date),
            new("description", "Açıklama"),
            new(OwnedRecordType.OwnerField, "Sorumlu", ImportFieldType.User),
        ])
        {
            MatchRules = [new("email", "email"), new("phone", "phone")],
        },
    ];
}
