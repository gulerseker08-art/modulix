using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Automation;

/// <summary>
/// CRM'in başlangıç kuralı şablonları (ADR-0027, Dilim 3b). Hepsi takip aktivitesi açar; hiçbiri mesaj göndermez, kayıt değiştirmez.
/// Firma "kullan" deyince şablon taslak kural olur (çalışmaz); yayın ve açma firmanın kararıdır.
/// </summary>
public static class CrmAutomationTemplates
{
    public const string LeadInactivity = "crm.lead-inactivity-follow-up";

    public const string OpportunityInactivity = "crm.opportunity-inactivity-follow-up";

    public const string OpportunityCloseDate = "crm.opportunity-close-date-reminder";

    public const string LeadPurchaseDate = "crm.lead-purchase-date-reminder";

    // Metin değişkenleri (Dilim 4d): kayıt türünün beyan edilmiş, okuma izni gerektirmeyen alanları.
    private static readonly string LeadSubject = $"{CrmModule.Entities.Lead}.subject";
    private static readonly string LeadPurchaseDateField = $"{CrmModule.Entities.Lead}.targetPurchaseDate";
    private static readonly string OpportunityTitle = $"{CrmModule.Entities.Opportunity}.title";
    private static readonly string OpportunityCloseDateField = $"{CrmModule.Entities.Opportunity}.expectedCloseDate";

    private static string Var(string fieldKey) => "{{" + fieldKey + "}}";

    public static IReadOnlyList<AutomationTemplateDefinition> All { get; } =
    [
        new(
            LeadInactivity,
            "Hareketsiz müşteri adayı için takip",
            "Açık bir müşteri adayında 7 gündür aktivite yoksa kaydın sorumlusuna ertesi güne takip aktivitesi açar.",
            CrmModule.Entities.Lead,
            new AutomationDefinition(
                new InactivityTrigger(CrmAutomation.LeadActivities, 7),
                null,
                [FollowUp($"Müşteri adayıyla yeniden iletişime geçin: {Var(LeadSubject)}", "Bu aday son 7 gündür hareketsiz.", 1)])),
        new(
            OpportunityInactivity,
            "Hareketsiz fırsat için takip",
            "Açık bir fırsatta 14 gündür aktivite yoksa kaydın sorumlusuna ertesi güne takip aktivitesi açar.",
            CrmModule.Entities.Opportunity,
            new AutomationDefinition(
                new InactivityTrigger(CrmAutomation.OpportunityActivities, 14),
                null,
                [FollowUp($"Fırsatı gözden geçirin: {Var(OpportunityTitle)}", "Bu fırsat son 14 gündür hareketsiz.", 1)])),
        new(
            OpportunityCloseDate,
            "Kapanış tarihi yaklaşan fırsat için hatırlatma",
            "Beklenen kapanış tarihine 3 gün kala, hâlâ açık ya da beklemede olan fırsatın sorumlusuna takip aktivitesi açar.",
            CrmModule.Entities.Opportunity,
            new AutomationDefinition(
                new DateRelativeTrigger($"{CrmModule.Entities.Opportunity}.expectedCloseDate", -3),
                new ConditionLeaf($"{CrmModule.Entities.Opportunity}.status", AutomationOperator.In, [OpportunityStatus.Open, OpportunityStatus.OnHold]),
                [FollowUp(
                    $"Kapanış tarihi yaklaşıyor: {Var(OpportunityTitle)}",
                    $"Fırsatın beklenen kapanış tarihi {Var(OpportunityCloseDateField)}; 3 gün kaldı.",
                    0)])),
        new(
            LeadPurchaseDate,
            "Satın alma tarihi yaklaşan müşteri adayı için hatırlatma",
            "Hedef satın alma tarihine 7 gün kala, açık müşteri adayının sorumlusuna takip aktivitesi açar.",
            CrmModule.Entities.Lead,
            new AutomationDefinition(
                new DateRelativeTrigger($"{CrmModule.Entities.Lead}.targetPurchaseDate", -7),
                new ConditionLeaf($"{CrmModule.Entities.Lead}.status", AutomationOperator.In, [.. LeadStatus.Open]),
                [FollowUp(
                    $"Satın alma tarihi yaklaşıyor: {Var(LeadSubject)}",
                    $"Adayın hedef satın alma tarihi {Var(LeadPurchaseDateField)}; 7 gün kaldı.",
                    1)])),
    ];

    private static AutomationActionStep FollowUp(string subject, string description, int dueInDays) =>
        new("followup", CrmAutomation.FollowUpAction, new JsonObject
        {
            ["subject"] = subject,
            ["description"] = description,
            ["dueInDays"] = dueInDays,
        });
}
