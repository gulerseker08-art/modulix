using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class ConditionEvaluatorTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static JsonObject Deal() => new()
    {
        ["amount"] = 7500,
        ["currency"] = "TRY",
        ["stage"] = "negotiation",
        ["dueDate"] = "2026-11-10",
        ["lastContactAt"] = "2026-11-03T22:30:00Z",
        ["ownerId"] = OwnerId.ToString(),
        ["title"] = "Büyük Fırsat",
        ["score"] = 42,
        ["isVip"] = true,
        ["margin"] = 12345,
    };

    private static ConditionExplanation Run(ConditionNode? node, JsonObject? values = null, JsonObject? changes = null, int? relations = null, params string[] granted) =>
        Evaluate(node, Fields(granted), Facts(Fields(granted), values ?? Deal(), changes, relations), Day(2026, 11, 4));

    [Fact]
    public void No_condition_passes_for_every_record()
    {
        Assert.True(Run(null).Passed);
    }

    [Fact]
    public void And_requires_all_and_or_requires_one()
    {
        var yes = Leaf(Stage, AutomationOperator.Eq, "negotiation");
        var no = Leaf(Stage, AutomationOperator.Eq, "won");

        Assert.True(Run(And(yes, yes)).Passed);
        Assert.False(Run(And(yes, no)).Passed);
        Assert.True(Run(Or(no, yes)).Passed);
        Assert.False(Run(Or(no, no)).Passed);
        Assert.True(Run(And(yes, Or(no, yes))).Passed);
    }

    [Fact]
    public void Explanation_has_a_node_for_every_condition_with_its_reason()
    {
        var explanation = Run(And(Leaf(Stage, AutomationOperator.Eq, "won"), Leaf(Score, AutomationOperator.Gt, "10")));

        Assert.False(explanation.Passed);
        Assert.Equal(2, explanation.Children.Count);
        Assert.False(explanation.Children[0].Passed);
        Assert.Contains("Aşama = Kazanıldı", explanation.Children[0].Text);
        Assert.Contains("sağlanmadı", explanation.Children[0].Text);
        Assert.Contains("değer: Müzakere", explanation.Children[0].Text);
        Assert.True(explanation.Children[1].Passed);
    }

    [Fact]
    public void Null_never_satisfies_comparisons_except_not_equal_and_is_empty()
    {
        var values = Deal();
        values["score"] = null;
        values.Remove("title");

        Assert.False(Run(Leaf(Score, AutomationOperator.Eq, "42"), values).Passed);
        Assert.False(Run(Leaf(Score, AutomationOperator.Gt, "0"), values).Passed);
        Assert.False(Run(Leaf(Title, AutomationOperator.Contains, "a"), values).Passed);
        Assert.True(Run(Leaf(Score, AutomationOperator.Ne, "42"), values).Passed);
        Assert.True(Run(Leaf(Title, AutomationOperator.NotIn, "a"), values).Passed);
        Assert.True(Run(Leaf(Score, AutomationOperator.IsEmpty), values).Passed);
        Assert.False(Run(Leaf(Score, AutomationOperator.IsNotEmpty), values).Passed);
        Assert.True(Run(Leaf(Title, AutomationOperator.IsEmpty), values).Passed);
    }

    [Fact]
    public void Empty_string_counts_as_null()
    {
        var values = Deal();
        values["title"] = "";

        Assert.True(Run(Leaf(Title, AutomationOperator.IsEmpty), values).Passed);
    }

    [Fact]
    public void Number_and_range_comparisons()
    {
        Assert.True(Run(Leaf(Score, AutomationOperator.Gte, "42")).Passed);
        Assert.False(Run(Leaf(Score, AutomationOperator.Lt, "42")).Passed);
        Assert.True(Run(Leaf(Score, AutomationOperator.Between, "40", "50")).Passed);
        Assert.False(Run(Leaf(Score, AutomationOperator.Between, "43", "50")).Passed);
    }

    [Fact]
    public void Money_compares_only_within_the_same_currency_and_is_never_converted()
    {
        Assert.True(Run(new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["5000"]) { Currency = "TRY" }).Passed);
        Assert.False(Run(new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["8000"]) { Currency = "TRY" }).Passed);

        // 7500 TRY, "100 EUR"dan büyük görünse de farklı para birimi karşılaştırılmaz: kural kaçar, nedeni açıklanır.
        var different = Run(new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["100"]) { Currency = "EUR" });
        Assert.False(different.Passed);
        Assert.Contains("para birimi farklı", different.Text);

        var ne = Run(new ConditionLeaf(Amount.Key, AutomationOperator.Ne, ["100"]) { Currency = "EUR" });
        Assert.False(ne.Passed);
    }

    [Fact]
    public void Lookup_compares_the_persistent_id()
    {
        Assert.True(Run(Leaf(Owner, AutomationOperator.Eq, OwnerId.ToString())).Passed);
        Assert.False(Run(Leaf(Owner, AutomationOperator.Eq, Guid.NewGuid().ToString())).Passed);
        Assert.True(Run(Leaf(Owner, AutomationOperator.In, Guid.NewGuid().ToString(), OwnerId.ToString())).Passed);
        Assert.True(Run(Leaf(Owner, AutomationOperator.NotIn, Guid.NewGuid().ToString())).Passed);
    }

    [Fact]
    public void Text_comparison_ignores_case_without_depending_on_culture()
    {
        Assert.True(Run(Leaf(Title, AutomationOperator.Contains, "BÜYÜK")).Passed);
        Assert.True(Run(Leaf(Title, AutomationOperator.StartsWith, "büyük")).Passed);
        Assert.True(Run(Leaf(Title, AutomationOperator.Eq, "büyük fırsat")).Passed);
    }

    [Fact]
    public void Boolean_and_choice_comparisons()
    {
        Assert.True(Run(Leaf(IsVip, AutomationOperator.Eq, "true")).Passed);
        Assert.False(Run(Leaf(IsVip, AutomationOperator.Eq, "false")).Passed);
        Assert.True(Run(Leaf(Stage, AutomationOperator.NotIn, "won", "lost")).Passed);
    }

    [Fact]
    public void Date_operators_use_the_tenant_calendar_day()
    {
        // Bugün 2026-11-04; kapanış tarihi 2026-11-10.
        Assert.True(Run(Leaf(DueDate, AutomationOperator.WithinNextDays, "7")).Passed);
        Assert.False(Run(Leaf(DueDate, AutomationOperator.WithinNextDays, "5")).Passed);
        Assert.False(Run(Leaf(DueDate, AutomationOperator.WithinLastDays, "30")).Passed);
        Assert.False(Run(Leaf(DueDate, AutomationOperator.OlderThanDays, "1")).Passed);
        Assert.True(Run(Leaf(DueDate, AutomationOperator.Before, "2026-11-11")).Passed);
        Assert.True(Run(Leaf(DueDate, AutomationOperator.After, "2026-11-09")).Passed);
        Assert.True(Run(Leaf(DueDate, AutomationOperator.Between, "2026-11-10", "2026-11-10")).Passed);
    }

    [Fact]
    public void Inactivity_style_condition_counts_whole_days_back_from_today()
    {
        var values = Deal();
        values["dueDate"] = "2026-08-06"; // bugünden 90 gün önce

        Assert.True(Run(Leaf(DueDate, AutomationOperator.OlderThanDays, "90"), values).Passed);
        Assert.False(Run(Leaf(DueDate, AutomationOperator.OlderThanDays, "91"), values).Passed);
    }

    [Fact]
    public void Instants_are_converted_to_the_tenant_zone_before_the_day_is_compared()
    {
        // 2026-11-03T22:30Z İstanbul'da (UTC+3) 4 Kasım 01:30'dur: bugün (4 Kasım) ile aynı gündür.
        Assert.True(Run(Leaf(LastContact, AutomationOperator.WithinLastDays, "0")).Passed);
        Assert.False(Run(Leaf(LastContact, AutomationOperator.OlderThanDays, "1")).Passed);

        // Anlar UTC olarak karşılaştırılır.
        Assert.True(Run(Leaf(LastContact, AutomationOperator.After, "2026-11-03T22:00:00Z")).Passed);
        Assert.False(Run(Leaf(LastContact, AutomationOperator.After, "2026-11-03T23:00:00Z")).Passed);
    }

    [Fact]
    public void Old_and_new_values_come_from_the_event_diff()
    {
        var changes = new JsonObject { ["stage"] = new JsonObject { ["old"] = "new", ["new"] = "won" } };
        var values = Deal();
        values["stage"] = "won";

        Assert.True(Run(new ConditionLeaf(Stage.Key, AutomationOperator.Eq, ["new"]) { Source = AutomationValueSource.Old }, values, changes).Passed);
        Assert.True(Run(Leaf(Stage, AutomationOperator.Eq, "won"), values, changes).Passed);
        Assert.True(Run(Leaf(Stage, AutomationOperator.Changed), values, changes).Passed);
        Assert.True(Run(Leaf(Stage, AutomationOperator.ChangedTo, "won"), values, changes).Passed);
        Assert.False(Run(Leaf(Stage, AutomationOperator.ChangedTo, "lost"), values, changes).Passed);
        Assert.True(Run(Leaf(Stage, AutomationOperator.ChangedFrom, "new"), values, changes).Passed);
        Assert.False(Run(Leaf(Stage, AutomationOperator.ChangedFrom, "won"), values, changes).Passed);
    }

    [Fact]
    public void Field_missing_from_the_diff_did_not_change()
    {
        var changes = new JsonObject { ["stage"] = new JsonObject { ["old"] = "new", ["new"] = "won" } };

        var result = Run(Leaf(Title, AutomationOperator.Changed), changes: changes);

        Assert.False(result.Passed);
        Assert.Contains("değişmedi", result.Text);
        Assert.True(Run(new ConditionLeaf(Title.Key, AutomationOperator.Eq, ["Büyük Fırsat"]) { Source = AutomationValueSource.Old }, changes: changes).Passed);
    }

    [Fact]
    public void Without_change_information_old_values_and_change_operators_do_not_pass_and_say_why()
    {
        var old = Run(new ConditionLeaf(Stage.Key, AutomationOperator.Eq, ["new"]) { Source = AutomationValueSource.Old });
        var changed = Run(Leaf(Stage, AutomationOperator.Changed));

        Assert.False(old.Passed);
        Assert.Contains("değişiklik bilgisi yok", old.Text);
        Assert.False(changed.Passed);
        Assert.Contains("değişiklik bilgisi yok", changed.Text);
    }

    [Fact]
    public void Field_the_identity_cannot_read_fails_and_never_leaks_its_value()
    {
        var hidden = Run(Leaf(Secret, AutomationOperator.Gt, "100"));
        var granted = Run(Leaf(Secret, AutomationOperator.Gt, "100"), granted: [SecretPermission]);

        Assert.False(hidden.Passed);
        Assert.Contains("yetkisi yok", hidden.Text);
        Assert.DoesNotContain("12345", hidden.Text);
        Assert.True(granted.Passed);
    }

    [Fact]
    public void Relation_conditions_use_the_resolved_count()
    {
        var exists = new ConditionRelation(Activities.Key, RelationQuantifier.Exists);
        var none = new ConditionRelation(Activities.Key, RelationQuantifier.NotExists);
        var atLeastTwo = new ConditionRelation(Activities.Key, RelationQuantifier.Count) { CountOperator = AutomationOperator.Gte, CountValue = 2 };

        Assert.True(Run(exists, relations: 3).Passed);
        Assert.False(Run(none, relations: 3).Passed);
        Assert.True(Run(none, relations: 0).Passed);
        Assert.True(Run(atLeastTwo, relations: 2).Passed);
        Assert.False(Run(atLeastTwo, relations: 1).Passed);
    }

    [Fact]
    public void Relation_that_cannot_be_counted_does_not_pass()
    {
        var result = Run(new ConditionRelation(Activities.Key, RelationQuantifier.NotExists), relations: null);

        Assert.False(result.Passed);
        Assert.Contains("sayılamadı", result.Text);
    }

    [Fact]
    public void Evaluation_does_not_change_the_record()
    {
        var values = Deal();
        var before = values.ToJsonString();

        Run(And(Leaf(Stage, AutomationOperator.Eq, "won"), Leaf(Score, AutomationOperator.Gt, "1")), values);

        Assert.Equal(before, values.ToJsonString());
    }

    [Fact]
    public void Readable_text_describes_the_condition_in_turkish()
    {
        var node = And(
            Leaf(Stage, AutomationOperator.In, "new", "negotiation"),
            Or(new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["5000"]) { Currency = "TRY" }, Leaf(DueDate, AutomationOperator.WithinNextDays, "7")));

        var text = ConditionText.Describe(node, Fields());

        Assert.Equal("Aşama şunlardan biri: Yeni, Müzakere ve (Tutar ≥ 5000 TRY veya Kapanış tarihi önümüzdeki 7 gün içinde)", text);
    }
}
