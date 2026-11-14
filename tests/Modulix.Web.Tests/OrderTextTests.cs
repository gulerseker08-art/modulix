using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 aşama 4, dilim 11 (Siparişler): <see cref="OrderLabels"/> listeleri ve eylem/geçmiş adları sözlükten gelir; Türkçe kaynak metin
/// (koddaki yedek) <c>tr.json</c> ile aynıdır. Diller arası anahtar eşitliğini mimari testler denetler.
/// </summary>
public class OrderTextTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))!;
    }

    private static string Slug(string key) => (key.StartsWith('-') ? "desc-" + key[1..] : key).ToLowerInvariant();

    [Fact]
    public void Every_label_list_value_has_a_turkish_text_equal_to_the_source_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string group, IReadOnlyList<(string Key, string Label)> list)
        {
            foreach (var (key, label) in list)
            {
                var name = $"orders.{group}.{Slug(key)}";
                if (!tr.TryGetValue(name, out var text))
                {
                    problems.Add($"eksik: {name} ({label})");
                }
                else if (text != label)
                {
                    problems.Add($"farklı: {name} sözlük='{text}' kaynak='{label}'");
                }
            }
        }

        Check("status", OrderLabels.Statuses);
        Check("handover", OrderLabels.Handovers);
        Check("fulfillment", OrderLabels.Fulfillments);
        Check("line-kind", OrderLabels.LineKinds);
        Check("acceptance", OrderLabels.AcceptanceMethods);
        Check("sort", OrderLabels.Sorts);
        Check("job-status", OrderLabels.JobStatuses);
        Check("job-kind", OrderLabels.JobKinds);
        Check("priority", OrderLabels.Priorities);
        Check("job-sort", OrderLabels.JobSorts);
        Check("change-status", OrderLabels.ChangeStatuses);
        Check("header-field", OrderLabels.HeaderFields);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Action_and_history_names_have_a_turkish_text_equal_to_the_source_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string group, string key, string label)
        {
            var name = $"orders.{group}.{key.ToLowerInvariant()}";
            if (!tr.TryGetValue(name, out var text))
            {
                problems.Add($"eksik: {name} ({label})");
            }
            else if (text != label)
            {
                problems.Add($"farklı: {name} sözlük='{text}' kaynak='{label}'");
            }
        }

        foreach (var key in new[] { "submit-for-approval", "withdraw-approval", "approve", "return-to-draft", "cancel", "close", "force-close" })
        {
            Check("action", key, OrderLabels.Action(key));
        }

        foreach (var key in new[] { "assign", "start", "complete", "cancel" })
        {
            Check("job-action", key, OrderLabels.JobAction(key));
        }

        foreach (var key in new[]
                 {
                     "CreatedFromQuote", "SubmittedForApproval", "ApprovalWithdrawn", "Approved", "ReturnedToDraft", "Cancelled", "Closed", "ForceClosed",
                     "ChangeRequested", "ChangeApplied", "ChangeRejected", "ChangeWithdrawn", "DeliveryPlanned", "Delivered", "DeliveryFailed",
                     "DeliveryRescheduled", "DeliveryCancelled", "EvidenceAdded", "ServicePlanned", "ServiceStarted", "ServiceConfirmed", "ServiceCompleted",
                     "FulfillmentChanged",
                 })
        {
            Check("history", key, OrderLabels.History(key));
        }

        foreach (var key in new[]
                 {
                     "Generated", "CreatedManually", "Assigned", "Started", "Completed", "Cancelled", "DependencyAdded", "DependencyRemoved",
                     "UnassignedAlert", "OverdueAlert",
                 })
        {
            Check("job-history", key, OrderLabels.JobHistory(key));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_turkish_source_label_is_shown()
    {
        Assert.Equal("Onay bekliyor", OrderLabels.Label(OrderLabels.Statuses, "PendingApproval"));
        Assert.Equal("Siparişi kapat", OrderLabels.Action("close"));
        Assert.Equal("Bilinmeyen", OrderLabels.History("Bilinmeyen"));
    }
}
