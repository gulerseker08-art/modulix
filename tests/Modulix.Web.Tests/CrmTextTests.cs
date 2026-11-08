using System.Text.Json;
using Modulix.Shared.Crm;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034: Shared'daki <c>Label(...)</c> işlevlerinin (müşteri adayı durumu/önceliği, aktivite sonucu vb.) çevirisi. Sözleşmedeki
/// Türkçe ad kaynak metindir; her değer için <c>tr.json</c>'da anahtar vardır ve metin sözleşmeyle aynıdır. Diller arası anahtar
/// eşitliğini mimari testler denetler.
/// </summary>
public class CrmTextTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void Every_value_has_a_turkish_text_equal_to_the_contract_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string group, string value, string label)
        {
            var key = $"{group}.{value.ToLowerInvariant()}";
            if (!tr.TryGetValue(key, out var text))
            {
                problems.Add($"eksik: {key} ({label})");
            }
            else if (text != label)
            {
                problems.Add($"farklı: {key} sözlük='{text}' sözleşme='{label}'");
            }
        }

        foreach (var status in LeadStatus.All)
        {
            Check(CrmText.LeadStatusGroup, status, LeadStatus.Label(status));
        }

        foreach (var priority in LeadPriority.All)
        {
            Check(CrmText.LeadPriorityGroup, priority, LeadPriority.Label(priority));
        }

        foreach (var status in OpportunityStatus.All)
        {
            Check(CrmText.OpportunityStatusGroup, status, OpportunityStatus.Label(status));
        }

        foreach (var status in OpportunityBudgetStatus.All)
        {
            Check(CrmText.OpportunityBudgetStatusGroup, status, OpportunityBudgetStatus.Label(status));
        }

        foreach (var role in OpportunityContactRoles.All)
        {
            Check(CrmText.OpportunityContactRoleGroup, role, OpportunityContactRoles.Label(role));
        }

        foreach (var outcome in new[] { ActivityOutcome.Reached, ActivityOutcome.NoAnswer, ActivityOutcome.Sent })
        {
            Check(CrmText.ActivityOutcomeGroup, outcome, ActivityOutcome.Label(outcome));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_contract_label_is_shown()
    {
        Assert.Equal(LeadStatus.Label(LeadStatus.Qualified), CrmText.LeadStatusName(LeadStatus.Qualified));
        Assert.Equal(LeadPriority.Label(LeadPriority.Urgent), CrmText.LeadPriorityName(LeadPriority.Urgent));
        Assert.Equal("Bilinmeyen", CrmText.LeadStatusName("Bilinmeyen"));
    }
}
