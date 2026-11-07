using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Otomasyon kural düzenleyicisinin (ADR-0027, Dilim 5c) model sınıfları: sunucu JSON'unu okur, yazar, bilmediğini silmez.</summary>
public class AutomationEditorModelTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string InactivityRule = """
        {
          "trigger": { "type": "inactivity", "relation": "crm.lead.activities", "days": 7 },
          "actions": [
            { "id": "followup", "action": "crm.follow-up.create",
              "parameters": { "subject": "Yeniden iletişime geçin", "dueInDays": 1 } }
          ]
        }
        """;

    [Fact]
    public void A_simple_rule_round_trips_without_losing_anything()
    {
        var definition = EditorDefinition.Parse(Json(InactivityRule));

        Assert.Equal(EditorTrigger.Inactivity, definition.Trigger.Type);
        Assert.Equal("crm.lead.activities", definition.Trigger.Relation);
        Assert.Equal(7, definition.Trigger.Days);
        Assert.Empty(definition.Condition.Leaves);

        var built = definition.Build();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(InactivityRule), built));
        Assert.Null(built["condition"]);
    }

    /// <summary>Core.Tests'teki <c>AutomationEditorJsonShapeTests.Full</c> ile aynı metin: sunucunun tipli modeline çözüldüğü orada sabitlenir.</summary>
    private const string Full =
        """{"trigger":{"type":"fieldChanged","fields":["crm.opportunity.status"]},"condition":{"type":"group","logic":"or","items":[{"type":"leaf","field":"crm.opportunity.status","operator":"changedTo","values":["Won"],"source":"old"},{"type":"leaf","field":"crm.opportunity.amount","operator":"gt","values":["1000"],"currency":"TRY"}]},"actions":[{"id":"adim1","action":"crm.follow-up.create","parameters":{"subject":"Ara","dueInDays":2}}]}""";

    [Fact]
    public void A_rule_built_from_scratch_writes_exactly_the_shape_the_server_model_reads()
    {
        var definition = new EditorDefinition();
        definition.Trigger.ChangeType(EditorTrigger.FieldChanged);
        definition.Trigger.Fields = ["crm.opportunity.status"];
        definition.Condition.Logic = EditorCondition.Or;
        definition.Condition.Leaves =
        [
            new EditorLeaf { Field = "crm.opportunity.status", Operator = "changedTo", Values = ["Won"], Source = "old" },
            new EditorLeaf { Field = "crm.opportunity.amount", Operator = "gt", Values = ["1000"], Currency = "TRY" },
        ];
        var step = new EditorAction { Id = definition.NextStepId(), Action = "crm.follow-up.create" };
        step.SetValue("subject", "Text", "Ara");
        step.SetValue("dueInDays", "Number", "2");
        definition.Actions.Add(step);

        Assert.Equal(Full, definition.Build().ToJsonString());
    }

    [Fact]
    public void A_single_root_leaf_becomes_a_one_item_group_with_the_same_meaning()
    {
        var definition = EditorDefinition.Parse(Json("""
            { "trigger": { "type": "dateRelative", "field": "crm.opportunity.expectedCloseDate", "offsetDays": -3 },
              "condition": { "type": "leaf", "field": "crm.opportunity.status", "operator": "in", "values": ["Open", "OnHold"] },
              "actions": [] }
            """));

        Assert.False(definition.Condition.IsLocked);
        var leaf = Assert.Single(definition.Condition.Leaves);
        Assert.Equal("in", leaf.Operator);
        Assert.Equal(["Open", "OnHold"], leaf.Values);

        var condition = (JsonObject)definition.Build()["condition"]!;
        Assert.Equal("group", condition["type"]!.GetValue<string>());
        Assert.Equal("and", condition["logic"]!.GetValue<string>());
        Assert.Single((JsonArray)condition["items"]!);
    }

    [Fact]
    public void A_flat_or_group_keeps_its_logic_and_the_old_value_source()
    {
        var definition = EditorDefinition.Parse(Json("""
            { "trigger": { "type": "fieldChanged", "fields": ["crm.lead.status"] },
              "condition": { "type": "group", "logic": "or", "items": [
                { "type": "leaf", "field": "crm.lead.status", "operator": "changedFrom", "values": ["New"], "source": "old" },
                { "type": "leaf", "field": "crm.lead.amount", "operator": "gt", "values": ["100"] } ] },
              "actions": [] }
            """));

        Assert.Equal(EditorCondition.Or, definition.Condition.Logic);
        Assert.Equal(2, definition.Condition.Leaves.Count);
        Assert.True(definition.Trigger.HasChanges);

        var items = (JsonArray)((JsonObject)definition.Build()["condition"]!)["items"]!;
        Assert.Equal("old", items[0]!["source"]!.GetValue<string>());
        Assert.Null(items[1]!["source"]); // varsayılan "new" yazılmaz
    }

    [Fact]
    public void Nested_groups_and_relation_conditions_are_kept_verbatim_and_not_editable()
    {
        const string nested = """
            { "type": "group", "logic": "and", "items": [
              { "type": "group", "logic": "or", "items": [ { "type": "leaf", "field": "a.b", "operator": "eq", "values": ["x"] } ] } ] }
            """;
        const string relation = """{ "type": "relation", "relation": "crm.lead.activities", "quantifier": "notExists" }""";

        foreach (var condition in new[] { nested, relation })
        {
            var definition = EditorDefinition.Parse(Json($$"""{ "trigger": { "type": "manual" }, "condition": {{condition}}, "actions": [] }"""));

            Assert.True(definition.Condition.IsLocked);
            Assert.Empty(definition.Condition.Leaves);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(condition), definition.Build()["condition"]));
        }
    }

    [Fact]
    public void A_domain_event_trigger_is_locked_and_preserved_and_can_be_replaced()
    {
        var definition = EditorDefinition.Parse(Json("""{ "trigger": { "type": "domainEvent", "eventKey": "crm.opportunity.won" }, "actions": [] }"""));

        Assert.True(definition.Trigger.IsLocked);
        Assert.Equal("crm.opportunity.won", definition.Build()["trigger"]!["eventKey"]!.GetValue<string>());

        // Kullanıcı bilerek başka bir tetikleyici seçerse kilit kalkar ve eski değerler taşınmaz.
        definition.Trigger.ChangeType(EditorTrigger.Manual);
        Assert.False(definition.Trigger.IsLocked);
        Assert.Equal("manual", definition.Build()["trigger"]!["type"]!.GetValue<string>());
        Assert.Null(definition.Build()["trigger"]!["eventKey"]);
    }

    [Fact]
    public void Trigger_parameters_are_written_only_for_the_chosen_type()
    {
        var trigger = new EditorTrigger();
        trigger.ChangeType(EditorTrigger.StatusTransition);
        trigger.Field = "crm.opportunity.status";
        trigger.To = "Won";
        var node = trigger.Build();
        Assert.Null(node["from"]); // boş önceki değer yazılmaz: herhangi bir değerden
        Assert.Equal("Won", node["to"]!.GetValue<string>());

        trigger.ChangeType(EditorTrigger.Scheduled);
        Assert.Equal("06:00", trigger.Build()["timeOfDay"]!.GetValue<string>());
        Assert.Null(trigger.Build()["field"]);

        trigger.ChangeType(EditorTrigger.FieldChanged);
        trigger.Fields = ["a.b", "a.c"];
        Assert.Equal(2, ((JsonArray)trigger.Build()["fields"]!).Count);
    }

    [Fact]
    public void Action_parameters_are_typed_from_the_catalog_and_empty_values_are_removed()
    {
        var step = new EditorAction { Id = "adim1", Action = "crm.follow-up.create" };

        step.SetValue("dueInDays", "Number", "2");
        step.SetValue("score", "Number", "1,5");
        step.SetValue("urgent", "Boolean", "true");
        step.SetValue("subject", "Text", "Ara");

        Assert.Equal(2L, step.Parameters["dueInDays"]!.GetValue<long>());
        Assert.Equal(1.5m, step.Parameters["score"]!.GetValue<decimal>());
        Assert.True(step.Parameters["urgent"]!.GetValue<bool>());
        Assert.Equal("Ara", step.Parameters["subject"]!.GetValue<string>());

        step.SetValue("subject", "Text", "  ");
        Assert.Null(step.Parameters["subject"]);

        // Çözülemeyen sayı olduğu gibi gider; sunucu doğrulaması bunu kullanıcıya bildirir.
        step.SetValue("dueInDays", "Number", "iki");
        Assert.Equal("iki", step.Parameters["dueInDays"]!.GetValue<string>());
    }

    [Fact]
    public void Field_bindings_and_unknown_parameters_survive_editing()
    {
        var definition = EditorDefinition.Parse(Json("""
            { "trigger": { "type": "manual" },
              "actions": [ { "id": "s1", "action": "x.y", "parameters": { "owner": { "$field": "crm.lead.ownerId" }, "eski": 5 } } ] }
            """));
        var step = definition.Actions[0];

        Assert.Equal("crm.lead.ownerId", step.BoundField("owner"));
        Assert.Equal(string.Empty, step.ValueText("owner"));
        Assert.Null(step.BoundField("eski"));
        Assert.Equal("5", step.ValueText("eski"));

        var built = definition.Build()["actions"]![0]!["parameters"]!;
        Assert.Equal("crm.lead.ownerId", built["owner"]!["$field"]!.GetValue<string>());
        Assert.Equal(5, built["eski"]!.GetValue<int>());
    }

    [Fact]
    public void New_step_ids_are_unique_even_after_removing_steps()
    {
        var definition = new EditorDefinition();
        definition.Actions.Add(new EditorAction { Id = definition.NextStepId() });
        definition.Actions.Add(new EditorAction { Id = definition.NextStepId() });
        Assert.Equal(["adim1", "adim2"], definition.Actions.Select(a => a.Id));

        definition.Actions.RemoveAt(0);
        var next = definition.NextStepId();
        Assert.DoesNotContain(definition.Actions, a => a.Id == next);
    }

    [Fact]
    public void Operator_names_follow_the_server_contract_and_value_counts_match_it()
    {
        Assert.Equal("withinNextDays", EditorLeaf.OperatorKey("WithinNextDays"));
        Assert.Equal((0, 0), EditorLeaf.ValueCount("isEmpty"));
        Assert.Equal((0, 0), EditorLeaf.ValueCount("changed"));
        Assert.Equal((2, 2), EditorLeaf.ValueCount("between"));
        Assert.Equal((1, 50), EditorLeaf.ValueCount("in"));
        Assert.Equal((1, 1), EditorLeaf.ValueCount("gte"));
        Assert.True(EditorLeaf.IsChange("changedTo"));
        Assert.False(EditorLeaf.IsChange("eq"));

        // Sunucudaki 21 operatörün hepsinin Türkçe adı var (etiketsiz kalıp ekranda İngilizce görünmesin).
        string[] all =
        [
            "Eq", "Ne", "Gt", "Gte", "Lt", "Lte", "Between", "Contains", "StartsWith", "In", "NotIn", "IsEmpty", "IsNotEmpty",
            "Before", "After", "WithinNextDays", "WithinLastDays", "OlderThanDays", "Changed", "ChangedTo", "ChangedFrom",
        ];
        Assert.All(all, op => Assert.NotEqual(op, AutomationLabels.Operator(op)));
    }

    [Fact]
    public void Every_trigger_type_the_editor_offers_has_a_turkish_name()
    {
        Assert.All(EditorTrigger.Types, t => Assert.NotEqual(t, AutomationLabels.TriggerType(t)));
        Assert.DoesNotContain(EditorTrigger.DomainEvent, EditorTrigger.Types); // iş olayı tetikleyicisi henüz yayınlanamaz
    }
}
