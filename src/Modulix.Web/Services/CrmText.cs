using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Paylaşılan sözleşmelerdeki (Shared) <c>Label(...)</c> işlevlerinin (durum, öncelik, sonuç vb.) etkin dildeki karşılıkları
/// (ADR-0034). Sözleşmedeki Türkçe ad kaynak metindir; sözlükte <c>{grup}.{değer küçük harf}</c> yoksa o gösterilir.
/// Yeni değer eklenince üç sözlüğe de anahtar eklenir (<c>CrmTextTests</c> kapsamı denetler).
/// </summary>
public static class CrmText
{
    public const string LeadStatusGroup = "crm.lead-status";
    public const string LeadPriorityGroup = "crm.lead-priority";
    public const string ActivityOutcomeGroup = "crm.activity-outcome";

    public static string Of(string group, string key, string fallback) =>
        Texts.GetOr($"{group}.{key.ToLowerInvariant()}", fallback);

    public static string LeadStatusName(string status) => Of(LeadStatusGroup, status, LeadStatus.Label(status));

    public static string LeadPriorityName(string priority) => Of(LeadPriorityGroup, priority, LeadPriority.Label(priority));

    public static string ActivityOutcomeName(string outcome) => Of(ActivityOutcomeGroup, outcome, ActivityOutcome.Label(outcome));
}
