using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Tests.Automation;

/// <summary>
/// Web'deki kural düzenleyicisi (Dilim 5c) tanımı elle kurduğu JSON'la yazar (Web, Core.Contracts'a başvuramaz). Bu test düzenleyicinin
/// ürettiği biçimin sunucunun tipli modeline çözüldüğünü sabitler; <c>AutomationEditorModelTests</c> aynı metni Web tarafında üretir.
/// Biçim değişirse iki test birlikte güncellenir.
/// </summary>
public class AutomationEditorJsonShapeTests
{
    /// <summary>Web.Tests'teki <c>EditorShape.Full</c> ile aynı metin olmalıdır.</summary>
    public const string Full = """
        {"trigger":{"type":"fieldChanged","fields":["crm.opportunity.status"]},"condition":{"type":"group","logic":"or","items":[{"type":"leaf","field":"crm.opportunity.status","operator":"changedTo","values":["Won"],"source":"old"},{"type":"leaf","field":"crm.opportunity.amount","operator":"gt","values":["1000"],"currency":"TRY"}]},"actions":[{"id":"adim1","action":"crm.follow-up.create","parameters":{"subject":"Ara","dueInDays":2}}]}
        """;

    [Fact]
    public void The_shape_the_editor_writes_deserializes_into_the_typed_model()
    {
        var definition = AutomationJson.Deserialize(Full);

        var trigger = Assert.IsType<FieldChangedTrigger>(definition.Trigger);
        Assert.Equal(["crm.opportunity.status"], trigger.Fields);

        var group = Assert.IsType<ConditionGroup>(definition.Condition);
        Assert.Equal(ConditionLogic.Or, group.Logic);
        var first = Assert.IsType<ConditionLeaf>(group.Items[0]);
        Assert.Equal(AutomationOperator.ChangedTo, first.Operator);
        Assert.Equal(AutomationValueSource.Old, first.Source);
        var second = Assert.IsType<ConditionLeaf>(group.Items[1]);
        Assert.Equal(AutomationOperator.Gt, second.Operator);
        Assert.Equal("TRY", second.Currency);

        var step = Assert.Single(definition.Actions);
        Assert.Equal("adim1", step.Id);
        Assert.Equal(2, step.Parameters["dueInDays"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"type":"recordCreated"}""", typeof(RecordCreatedTrigger))]
    [InlineData("""{"type":"manual"}""", typeof(ManualTrigger))]
    [InlineData("""{"type":"statusTransition","field":"a.b","to":"Won"}""", typeof(StatusTransitionTrigger))]
    [InlineData("""{"type":"scheduled","timeOfDay":"06:00"}""", typeof(ScheduledTrigger))]
    [InlineData("""{"type":"dateRelative","field":"a.b","offsetDays":-3}""", typeof(DateRelativeTrigger))]
    [InlineData("""{"type":"inactivity","relation":"a.r","days":7}""", typeof(InactivityTrigger))]
    public void Every_trigger_the_editor_offers_deserializes(string trigger, Type expected)
    {
        var definition = AutomationJson.Deserialize($$"""{"trigger":{{trigger}},"actions":[]}""");

        Assert.IsType(expected, definition.Trigger);
        Assert.Null(definition.Condition);
        Assert.IsType<JsonObject>(JsonNode.Parse(trigger));
    }
}
