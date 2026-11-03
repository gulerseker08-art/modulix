using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class ConditionValidatorTests
{
    private static IReadOnlyList<AutomationIssue> Validate(ConditionNode? node, ConditionOptions? options = null, params string[] granted) =>
        ConditionValidator.Validate(node, Fields(granted), options ?? ConditionOptions.Current);

    [Fact]
    public void Nested_tree_with_typed_values_has_no_issues()
    {
        var node = And(
            Leaf(Stage, AutomationOperator.In, "new", "negotiation"),
            Or(
                new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["5000"]) { Currency = "TRY" },
                Leaf(IsVip, AutomationOperator.Eq, "true")),
            Leaf(Owner, AutomationOperator.Eq, Guid.NewGuid().ToString()),
            Leaf(DueDate, AutomationOperator.WithinNextDays, "7"));

        Assert.Empty(Validate(node));
    }

    [Fact]
    public void No_condition_is_valid()
    {
        Assert.Empty(Validate(null));
    }

    [Fact]
    public void Unknown_field_is_reported_with_its_path()
    {
        var issues = Validate(And(Leaf(Title, AutomationOperator.Eq, "a"), new ConditionLeaf("sales.deal.nope", AutomationOperator.Eq, ["x"])));

        var issue = Assert.Single(issues);
        Assert.Equal("condition.items[1]", issue.Path);
        Assert.Contains("Bilinmeyen alan", issue.Message);
    }

    [Fact]
    public void Operator_must_fit_the_field_type()
    {
        Assert.Single(Validate(Leaf(IsVip, AutomationOperator.Gt, "true")));
        Assert.Single(Validate(Leaf(Title, AutomationOperator.Between, "a", "b")));
        Assert.Single(Validate(Leaf(Owner, AutomationOperator.Contains, Guid.NewGuid().ToString())));
        Assert.Single(Validate(Leaf(Stage, AutomationOperator.Gt, "new")));
    }

    [Fact]
    public void Value_count_follows_the_operator()
    {
        Assert.Single(Validate(Leaf(Score, AutomationOperator.Between, "1")));
        Assert.Single(Validate(Leaf(Score, AutomationOperator.IsEmpty, "1")));
        Assert.Single(Validate(Leaf(Score, AutomationOperator.Eq)));
        Assert.Empty(Validate(Leaf(Score, AutomationOperator.IsNotEmpty)));
    }

    [Fact]
    public void Money_needs_a_currency_and_other_types_must_not_have_one()
    {
        Assert.Single(Validate(Leaf(Amount, AutomationOperator.Gt, "100")));
        Assert.Single(Validate(new ConditionLeaf(Amount.Key, AutomationOperator.Gt, ["100"]) { Currency = "try" }));
        Assert.Single(Validate(new ConditionLeaf(Score.Key, AutomationOperator.Gt, ["100"]) { Currency = "TRY" }));
        Assert.Empty(Validate(Leaf(Amount, AutomationOperator.IsEmpty)));
    }

    [Fact]
    public void Lookup_compares_ids_not_display_text()
    {
        var issue = Assert.Single(Validate(Leaf(Owner, AutomationOperator.Eq, "Ayşe Yılmaz")));

        Assert.Contains("kalıcı kimliği", issue.Message);
    }

    [Fact]
    public void Choice_value_must_be_a_declared_option()
    {
        Assert.Single(Validate(Leaf(Stage, AutomationOperator.Eq, "Kazanıldı")));
        Assert.Empty(Validate(Leaf(Stage, AutomationOperator.Eq, "won")));
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("abc")]
    [InlineData("")]
    public void Number_must_use_a_dot_decimal_separator(string value)
    {
        Assert.Single(Validate(Leaf(Score, AutomationOperator.Eq, value)));
    }

    [Fact]
    public void Dates_must_be_iso_and_ranges_ordered()
    {
        Assert.Single(Validate(Leaf(DueDate, AutomationOperator.Before, "03.11.2026")));
        Assert.Empty(Validate(Leaf(DueDate, AutomationOperator.Before, "2026-11-03")));
        Assert.Single(Validate(Leaf(DueDate, AutomationOperator.Between, "2026-12-01", "2026-11-01")));
        Assert.Single(Validate(Leaf(Score, AutomationOperator.Between, "10", "2")));
    }

    [Fact]
    public void Day_operators_need_a_bounded_whole_number()
    {
        Assert.Empty(Validate(Leaf(DueDate, AutomationOperator.OlderThanDays, "90")));
        Assert.Single(Validate(Leaf(DueDate, AutomationOperator.OlderThanDays, "-1")));
        Assert.Single(Validate(Leaf(DueDate, AutomationOperator.OlderThanDays, "99999")));
        Assert.Single(Validate(Leaf(DueDate, AutomationOperator.WithinNextDays, "1.5")));
    }

    [Fact]
    public void Old_values_and_change_operators_need_an_update_trigger()
    {
        var old = new ConditionLeaf(Stage.Key, AutomationOperator.Eq, ["new"]) { Source = AutomationValueSource.Old };
        var changed = Leaf(Stage, AutomationOperator.ChangedTo, "won");

        Assert.Single(Validate(old));
        Assert.Single(Validate(changed));
        Assert.Empty(Validate(old, ConditionOptions.WithChanges));
        Assert.Empty(Validate(changed, ConditionOptions.WithChanges));
    }

    [Fact]
    public void Empty_group_and_deep_nesting_are_rejected()
    {
        Assert.Single(Validate(And()));

        ConditionNode deep = Leaf(Title, AutomationOperator.Eq, "x");
        for (var i = 0; i < ConditionValidator.MaxDepth + 1; i++)
        {
            deep = And(deep);
        }

        Assert.Contains(Validate(deep), i => i.Message.Contains("seviye"));
    }

    [Fact]
    public void Leaf_count_is_limited()
    {
        var leaves = Enumerable.Range(0, ConditionValidator.MaxLeaves + 1)
            .Select(i => (ConditionNode)Leaf(Title, AutomationOperator.Eq, i.ToString()))
            .ToArray();
        var groups = leaves.Chunk(ConditionValidator.MaxGroupItems).Select(chunk => (ConditionNode)Or(chunk)).ToArray();

        Assert.Contains(Validate(And(groups)), i => i.Message.Contains("en çok"));
    }

    [Fact]
    public void Field_the_author_cannot_read_cannot_be_used()
    {
        var node = Leaf(Secret, AutomationOperator.Gt, "10");

        Assert.Contains("görme izniniz yok", Assert.Single(Validate(node)).Message);
        Assert.Empty(Validate(node, null, SecretPermission));
    }

    [Fact]
    public void Relation_condition_is_bounded_to_declared_relations_and_target_fields()
    {
        var ok = new ConditionRelation(Activities.Key, RelationQuantifier.Exists) { Where = Leaf(ActivityKind, AutomationOperator.Eq, "call") };
        Assert.Empty(Validate(ok));

        Assert.Single(Validate(new ConditionRelation("sales.deal.unknown", RelationQuantifier.Exists)));

        // Hedef kaydın alanı olmayan alan ilişki koşulunda kullanılamaz (ana kaydın alanına gezinme yok).
        var wrongField = new ConditionRelation(Activities.Key, RelationQuantifier.Exists) { Where = Leaf(Title, AutomationOperator.Eq, "x") };
        Assert.Contains("Bilinmeyen alan", Assert.Single(Validate(wrongField)).Message);
    }

    [Fact]
    public void Relation_cannot_nest_another_relation_and_count_is_checked()
    {
        var nested = new ConditionRelation(Activities.Key, RelationQuantifier.Exists)
        {
            Where = new ConditionRelation(Activities.Key, RelationQuantifier.Exists),
        };
        Assert.Contains("ikinci bir ilişki", Assert.Single(Validate(nested)).Message);

        Assert.Single(Validate(new ConditionRelation(Activities.Key, RelationQuantifier.Count) { CountOperator = AutomationOperator.Contains, CountValue = 1 }));
        Assert.Single(Validate(new ConditionRelation(Activities.Key, RelationQuantifier.Count) { CountOperator = AutomationOperator.Gte, CountValue = -1 }));
        Assert.Empty(Validate(new ConditionRelation(Activities.Key, RelationQuantifier.Count) { CountOperator = AutomationOperator.Gte, CountValue = 3 }));
    }

    [Fact]
    public void All_issues_are_collected_not_just_the_first()
    {
        var issues = Validate(And(
            new ConditionLeaf("x.y.z", AutomationOperator.Eq, ["1"]),
            Leaf(IsVip, AutomationOperator.Gt, "true"),
            Leaf(Score, AutomationOperator.Eq, "abc")));

        Assert.Equal(3, issues.Count);
    }
}
