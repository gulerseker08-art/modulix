using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm;

/// <summary>
/// CRM'in içe aktarma tanımları (ADR-0033). Müşteri adayı sütunları elle girilebilen alanlardır; durum ve dönüşüm alanları iş akışına aittir.
/// Sorumlu dosyada verilmez, kayıt yükleyene atanır; firma ve kişi bağı lookup çözümü gerektirdiği için sonraki dilimdedir.
/// </summary>
internal static class CrmImports
{
    public static IReadOnlyList<ImportDefinition> All { get; } =
    [
        new(CrmModule.Entities.Lead, "Müşteri adayı", CrmModule.Permissions.LeadsCreate,
        [
            new("subject", "Konu", Required: true),
            new("firstName", "Ad"),
            new("lastName", "Soyad"),
            new("companyName", "Firma adı"),
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
        ])
        {
            MatchFields = ["email", "phone"],
        },
    ];
}
