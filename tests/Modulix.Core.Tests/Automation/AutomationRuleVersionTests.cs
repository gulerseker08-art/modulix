using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationRuleVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();

    private static AutomationVersionContent Content(string name = "Fırsat takibi", int priority = 100) =>
        new(name, null, priority, false, null, null, ValidDefinition(Leaf(Stage, AutomationOperator.Eq, "won")));

    [Fact]
    public void Definition_round_trips_through_json_including_every_node_and_trigger_kind()
    {
        var definition = new AutomationDefinition(
            new StatusTransitionTrigger(Stage.Key, "new", "won"),
            And(
                Leaf(Stage, AutomationOperator.In, "new", "won"),
                Or(
                    new ConditionLeaf(Amount.Key, AutomationOperator.Gte, ["5000"]) { Currency = "TRY", Source = AutomationValueSource.Old },
                    new ConditionRelation(Activities.Key, RelationQuantifier.Count)
                    {
                        CountOperator = AutomationOperator.Lt,
                        CountValue = 2,
                        Where = Leaf(ActivityKind, AutomationOperator.Eq, "call"),
                    })),
            [new AutomationActionStep("a", FollowUp.Key, new JsonObject { ["subject"] = "Ara", ["dueInDays"] = new JsonObject { ["$field"] = Score.Key } })]);

        var json = AutomationJson.Serialize(definition);
        var back = AutomationJson.Deserialize(json);

        Assert.Equal(json, AutomationJson.Serialize(back));
        Assert.IsType<StatusTransitionTrigger>(back.Trigger);
        var group = Assert.IsType<ConditionGroup>(back.Condition);
        var nested = Assert.IsType<ConditionGroup>(group.Items[1]);
        var leaf = Assert.IsType<ConditionLeaf>(nested.Items[0]);
        Assert.Equal(AutomationValueSource.Old, leaf.Source);
        Assert.Equal("TRY", leaf.Currency);
        Assert.IsType<ConditionRelation>(nested.Items[1]);
        Assert.Contains("\"type\":\"group\"", json);
        Assert.Contains("\"logic\":\"and\"", json);
    }

    [Theory]
    [InlineData("""{"trigger":{"type":"yok"},"actions":[]}""")]
    [InlineData("not json")]
    [InlineData("null")]
    public void Malformed_definition_is_a_format_error_not_a_crash(string json)
    {
        Assert.Throws<FormatException>(() => AutomationJson.Deserialize(json));
    }

    [Fact]
    public void Definition_has_no_place_for_code_or_sql()
    {
        // Bilinmeyen alanlar (ör. "sql", "script") yok sayılır; tip bilinmeyen düğüm ise reddedilir.
        var withScript = """{"trigger":{"type":"manual"},"condition":{"type":"script","code":"1==1"},"actions":[]}""";

        Assert.Throws<FormatException>(() => AutomationJson.Deserialize(withScript));
        Assert.DoesNotContain("sql", JsonSerializer.Serialize(new ConditionLeaf("a", AutomationOperator.Eq, ["x"])), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void New_rule_starts_as_a_draft_with_one_draft_version()
    {
        var rule = AutomationRule.Create(" Fırsat takibi ", "sales", Entity, User, Now);
        var version = AutomationRuleVersion.CreateDraft(rule.Id, 1, Content(), User, Now);

        Assert.Equal(AutomationRuleStatus.Draft, rule.Status);
        Assert.Equal("Fırsat takibi", rule.Name);
        Assert.Null(rule.PublishedVersionId);
        Assert.Equal(AutomationVersionState.Draft, version.State);
        Assert.Equal(1, version.VersionNo);
        Assert.NotEmpty(version.DefinitionHash);
    }

    [Fact]
    public void Published_version_is_immutable()
    {
        var version = AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(), User, Now);
        version.MarkPublished(User, Now);

        Assert.Equal(AutomationVersionState.Published, version.State);
        Assert.Equal(Now, version.PublishedAt);
        Assert.Throws<InvalidOperationException>(() => version.Apply(Content("Başka ad")));
        Assert.Throws<InvalidOperationException>(() => version.MarkPublished(User, Now));
        Assert.Equal("Fırsat takibi", version.Name);
    }

    [Fact]
    public void Hash_follows_the_content_so_identical_drafts_are_detected()
    {
        var a = AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(), User, Now);
        var same = AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 2, Content(), User, Now);
        var different = AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 3, Content(priority: 5), User, Now);

        Assert.Equal(a.DefinitionHash, same.DefinitionHash);
        Assert.NotEqual(a.DefinitionHash, different.DefinitionHash);
    }

    [Fact]
    public void Publishing_moves_the_rule_to_the_published_version_and_chooses_the_state()
    {
        var rule = AutomationRule.Create("Kural", "sales", Entity, User, Now);
        var version = AutomationRuleVersion.CreateDraft(rule.Id, 1, Content("Yayın adı"), User, Now);
        var before = rule.Version;

        rule.Publish(version.Id, version.Name, activate: false, User, Now);

        Assert.Equal(version.Id, rule.PublishedVersionId);
        Assert.Equal(AutomationRuleStatus.Inactive, rule.Status);
        Assert.Equal("Yayın adı", rule.Name);
        Assert.Equal(before + 1, rule.Version);

        rule.SetActive(true, User, Now);
        Assert.Equal(AutomationRuleStatus.Active, rule.Status);
    }

    [Fact]
    public void Unpublished_rule_cannot_be_activated()
    {
        var rule = AutomationRule.Create("Kural", "sales", Entity, User, Now);

        Assert.Throws<InvalidOperationException>(() => rule.SetActive(true, User, Now));
    }

    [Theory]
    [InlineData("", 100)]
    [InlineData("   ", 100)]
    public void Name_is_required(string name, int priority)
    {
        Assert.Throws<ArgumentException>(() => AutomationRule.Create(name, "sales", Entity, User, Now));
        Assert.Throws<ArgumentException>(() => AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(name, priority), User, Now));
    }

    [Fact]
    public void Priority_and_active_dates_are_validated()
    {
        Assert.Throws<ArgumentException>(() => AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(priority: -1), User, Now));
        Assert.Throws<ArgumentException>(() => AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(priority: 1001), User, Now));
        Assert.Throws<ArgumentException>(() => AutomationRuleVersion.CreateDraft(
            Guid.NewGuid(), 1, Content() with { ActiveFrom = Now, ActiveUntil = Now.AddDays(-1) }, User, Now));
    }

    [Fact]
    public void Content_can_be_read_back_from_a_version()
    {
        var version = AutomationRuleVersion.CreateDraft(Guid.NewGuid(), 1, Content(priority: 7), User, Now);

        var content = version.Content;

        Assert.Equal(7, content.Priority);
        Assert.Equal(AutomationJson.Serialize(Content().Definition), AutomationJson.Serialize(content.Definition));
    }
}
