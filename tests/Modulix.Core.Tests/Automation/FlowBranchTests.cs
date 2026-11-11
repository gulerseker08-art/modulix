using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Akışta dallanma (ADR-0027, Dilim 4c-5): adım koşulu ve "Akışı bitir" adımı.</summary>
public class FlowBranchTests
{
    private static readonly string[] AllPermissions = ["sales.deals.read", WritePermission, SecretPermission];

    private static AutomationActionStep Step(string id, ConditionNode? when) =>
        new(id, FollowUp.Key, new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 2 }) { When = when };

    private static AutomationDefinition Flow(params AutomationActionStep[] steps) => new(new ManualTrigger(), null, steps);

    private static IReadOnlyList<AutomationIssue> Validate(AutomationDefinition definition, string[]? permissions = null) =>
        Validator(Catalog(), new FakeAccess(permissions ?? AllPermissions, ["sales"]))
            .ValidateAsync("sales", Entity, definition, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Opposite_step_conditions_form_a_valid_branch()
    {
        var definition = Flow(
            Step("buyuk", Leaf(Score, AutomationOperator.Gte, "50")),
            Step("kucuk", Leaf(Score, AutomationOperator.Lt, "50")),
            new AutomationActionStep("bitir", FlowAutomation.StopAction, new JsonObject { ["reason"] = "Dal bitti" }) { When = Leaf(Stage, AutomationOperator.Eq, "won") });

        Assert.Empty(Validate(definition));
    }

    [Fact]
    public void Step_conditions_are_validated_under_the_step_path()
    {
        var issues = Validate(Flow(Step("a", null), Step("b", Leaf(Score, AutomationOperator.Gte))));

        var issue = Assert.Single(issues);
        Assert.StartsWith("actions[1].when", issue.Path);
        Assert.Contains("değer ister", issue.Message);
        Assert.Contains("Bilinmeyen alan", Assert.Single(Validate(Flow(Step("a", Leaf(Title, AutomationOperator.Eq, "x") with { Field = "yok" })))).Message);
    }

    [Fact]
    public void Step_conditions_look_at_current_values_so_change_and_old_value_conditions_are_refused_even_on_update_triggers()
    {
        var changed = new AutomationDefinition(
            new FieldChangedTrigger([Stage.Key]), null, [Step("a", Leaf(Stage, AutomationOperator.Changed))]);
        var old = new AutomationDefinition(
            new FieldChangedTrigger([Stage.Key]), null, [Step("a", Leaf(Stage, AutomationOperator.Eq, "won") with { Source = AutomationValueSource.Old })]);

        Assert.Contains("güncel değerine bakar", Assert.Single(Validate(changed)).Message);
        Assert.Contains("güncel değerine bakar", Assert.Single(Validate(old)).Message);
    }

    [Fact]
    public void A_step_condition_cannot_use_a_field_the_publisher_cannot_see()
    {
        var issues = Validate(Flow(Step("a", Leaf(Secret, AutomationOperator.Gt, "10"))), ["sales.deals.read", WritePermission]);

        var issue = Assert.Single(issues);
        Assert.Equal("actions[0].when", issue.Path);
        Assert.Contains("görme izniniz yok", issue.Message);
    }

    [Fact]
    public void The_rule_identity_gets_the_read_permission_of_fields_used_in_step_conditions()
    {
        var withSecret = AutomationRuleIdentity.Permissions(Entity, Flow(Step("a", Leaf(Secret, AutomationOperator.Gt, "10"))), Catalog());
        var without = AutomationRuleIdentity.Permissions(Entity, Flow(Step("a", null)), Catalog());

        Assert.Contains(SecretPermission, withSecret);
        Assert.DoesNotContain(SecretPermission, without);
    }

    [Fact]
    public void Steps_without_a_condition_serialize_exactly_as_before_and_conditions_round_trip()
    {
        var plain = AutomationJson.Serialize(Flow(Step("a", null)));
        Assert.DoesNotContain("\"when\"", plain); // eski sürümlerin özeti (hash) değişmez

        var json = AutomationJson.Serialize(Flow(Step("a", Leaf(Score, AutomationOperator.Gte, "50"))));
        var leaf = Assert.IsType<ConditionLeaf>(AutomationJson.Deserialize(json).Actions[0].When);
        Assert.Equal(Score.Key, leaf.Field);
        Assert.Equal(AutomationOperator.Gte, leaf.Operator);
    }

    [Fact]
    public async Task Stop_flow_ends_the_flow_on_purpose_and_only_plans_on_dry_run()
    {
        var registration = Catalog().FindAutomationAction(FlowAutomation.StopAction)!.Value;
        Assert.Equal(CoreModule.ModuleKey, registration.ModuleKey);
        Assert.Empty(registration.Action.Definition.RequiredPermissions);
        Assert.Equal(AutomationSideEffect.Read, registration.Action.Definition.SideEffect);

        var handler = (IAutomationAction)Activator.CreateInstance(registration.Action.Handler)!;
        var context = new AutomationCallContext(AutomationCallerKind.Automation, Guid.Empty);
        var target = new RecordRef(Entity, Guid.NewGuid());

        var stop = await handler.ExecuteAsync(
            new AutomationActionRequest(FlowAutomation.StopAction, new JsonObject { ["reason"] = "Küçük tutar" }, target, "k1"), context, CancellationToken.None);
        Assert.Equal(AutomationActionStatus.Succeeded, stop.Status);
        Assert.True(stop.StopFlow);
        Assert.Contains("Küçük tutar", stop.Summary);

        var planned = await handler.ExecuteAsync(
            new AutomationActionRequest(FlowAutomation.StopAction, [], target, "k2", DryRun: true), context, CancellationToken.None);
        Assert.Equal(AutomationActionStatus.Planned, planned.Status);
        Assert.False(planned.StopFlow);
    }
}
