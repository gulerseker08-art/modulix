using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Onay kuralı değerlendirmesi ve revizyon karşılaştırma (saf; veritabanı gerektirmez).</summary>
public class QuoteApprovalAndComparisonTests
{
    private static QuoteApprovalRuleInfo Rule(string name, decimal? discount = null, decimal? gross = null, string? currency = null, Guid? approver = null, int priority = 0) =>
        new(Guid.NewGuid(), 3, name, priority, currency, discount, gross, approver);

    [Fact]
    public void A_rule_matches_only_when_all_its_conditions_hold()
    {
        var rule = Rule("Büyük ve indirimli", discount: 10m, gross: 50_000m);

        // İndirim %20, toplam 60.000: ikisi de aşıldı.
        Assert.Single(QuoteApprovalEvaluator.Match([rule], "TRY", 10_000m, 2_000m, 60_000m));
        // İndirim yeterli ama toplam düşük → eşleşmez (VE).
        Assert.Empty(QuoteApprovalEvaluator.Match([rule], "TRY", 10_000m, 2_000m, 40_000m));
        // Toplam yeterli ama indirim düşük → eşleşmez.
        Assert.Empty(QuoteApprovalEvaluator.Match([rule], "TRY", 10_000m, 500m, 60_000m));
    }

    [Fact]
    public void Any_matching_rule_is_enough_and_thresholds_mean_strictly_above()
    {
        var discount = Rule("İndirim", discount: 10m);
        var gross = Rule("Tutar", gross: 50_000m);

        Assert.Empty(QuoteApprovalEvaluator.Match([discount, gross], "TRY", 1_000m, 100m, 50_000m));   // tam eşik: aşılmadı
        var matches = QuoteApprovalEvaluator.Match([discount, gross], "TRY", 1_000m, 101m, 50_000.01m);
        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(3, m.RuleVersion));
    }

    [Fact]
    public void Amount_conditions_apply_only_to_the_rule_currency()
    {
        var usd = Rule("USD tutar", gross: 1_000m, currency: "USD");
        Assert.Empty(QuoteApprovalEvaluator.Match([usd], "TRY", 5_000m, 0m, 5_000m));
        Assert.Single(QuoteApprovalEvaluator.Match([usd], "USD", 5_000m, 0m, 5_000m));
        // Para birimi boşsa her para birimi.
        Assert.Single(QuoteApprovalEvaluator.Match([Rule("Hepsi", gross: 1_000m)], "EUR", 5_000m, 0m, 5_000m));
    }

    [Fact]
    public void A_zero_list_total_never_triggers_a_discount_rule_and_matches_are_ordered_by_priority()
    {
        Assert.Empty(QuoteApprovalEvaluator.Match([Rule("İndirim", discount: 1m)], "TRY", 0m, 0m, 0m));
        var matches = QuoteApprovalEvaluator.Match([Rule("B", gross: 1m, priority: 5), Rule("A", gross: 1m, priority: 1)], "TRY", 100m, 0m, 100m);
        Assert.Equal(["A", "B"], matches.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void Named_approvers_restrict_who_may_decide_and_the_history_note_records_rule_versions()
    {
        var alice = Guid.NewGuid();
        var matches = QuoteApprovalEvaluator.Match([Rule("Yönetici onayı", gross: 1m, approver: alice), Rule("Serbest", gross: 1m)], "TRY", 100m, 0m, 100m);
        Assert.Equal([alice], QuoteApprovalEvaluator.RestrictedApprovers(matches));
        Assert.Empty(QuoteApprovalEvaluator.RestrictedApprovers(QuoteApprovalEvaluator.Match([Rule("Serbest", gross: 1m)], "TRY", 100m, 0m, 100m)));

        var note = QuoteApprovalEvaluator.HistoryNote(matches, "Genel ayar");
        Assert.Contains("Yönetici onayı (sürüm 3)", note);
        Assert.Contains("Genel ayar", note);
    }

    private static Quote NewQuote(int revision, string title = "Teklif", decimal gross = 120m) =>
        new() { RevisionNo = revision, Title = title, Currency = "TRY", Status = QuoteStatus.Presented, GrossTotal = gross, NetTotal = gross / 1.2m, TaxTotal = gross - gross / 1.2m, ListTotal = gross };

    private static QuoteLine Line(Guid key, string name, decimal quantity = 1m, decimal price = 100m, int position = 1, decimal? cost = null) =>
        new() { StableKey = key, Name = name, Quantity = quantity, UnitPrice = price, Position = position, Net = quantity * price, UnitCost = cost };

    [Fact]
    public void Lines_are_matched_by_stable_key_so_a_rename_is_a_change_not_an_add_and_remove()
    {
        var kept = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var added = Guid.NewGuid();
        var comparison = QuoteComparer.Compare(
            NewQuote(1), [Line(kept, "Sabit"), Line(renamed, "Eski ad", position: 2), Line(removed, "Silinecek", position: 3)],
            NewQuote(2, "Yeni başlık"), [Line(kept, "Sabit"), Line(renamed, "Yeni ad", quantity: 3m, position: 2), Line(added, "Eklenen", position: 3)]);

        Assert.Equal((1, 2), (comparison.LeftRevision, comparison.RightRevision));
        Assert.Equal("Unchanged", comparison.Lines.Single(l => l.StableKey == kept).Change);
        var changed = comparison.Lines.Single(l => l.StableKey == renamed);
        Assert.Equal("Changed", changed.Change);
        Assert.Contains(changed.Fields, f => f is { Field: "name", Before: "Eski ad", After: "Yeni ad" });
        Assert.Contains(changed.Fields, f => f.Field == "quantity" && f.After == "3");
        Assert.Equal("Added", comparison.Lines.Single(l => l.StableKey == added).Change);
        Assert.Equal("Removed", comparison.Lines.Single(l => l.StableKey == removed).Change);
        Assert.Contains(comparison.Header, h => h is { Field: "title", Before: "Teklif", After: "Yeni başlık" });
    }

    [Fact]
    public void Identical_revisions_have_no_differences_and_cost_never_appears()
    {
        var key = Guid.NewGuid();
        var comparison = QuoteComparer.Compare(NewQuote(1), [Line(key, "Kalem", cost: 40m)], NewQuote(2), [Line(key, "Kalem", cost: 99m)]);

        Assert.Empty(comparison.Header);
        Assert.Empty(comparison.Totals);
        Assert.Equal("Unchanged", Assert.Single(comparison.Lines).Change);
        Assert.DoesNotContain(comparison.Lines.SelectMany(l => l.Fields), f => f.Field.Contains("cost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Totals_changes_are_listed_with_turkish_number_format()
    {
        var comparison = QuoteComparer.Compare(NewQuote(1, gross: 1200m), [], NewQuote(2, gross: 1500.5m), []);
        var gross = Assert.Single(comparison.Totals, t => t.Field == "grossTotal");
        Assert.Equal(("1.200,00", "1.500,50"), (gross.Before, gross.After));
    }
}
