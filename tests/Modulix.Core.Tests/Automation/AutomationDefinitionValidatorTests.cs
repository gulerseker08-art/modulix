using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationDefinitionValidatorTests
{
    private static readonly string[] AllPermissions = ["sales.deals.read", WritePermission, SecretPermission];

    private static IReadOnlyList<AutomationIssue> Validate(
        AutomationDefinition definition, string[]? permissions = null, string[]? modules = null, string module = "sales", string entity = Entity)
    {
        var catalog = Catalog();
        var access = new FakeAccess(permissions ?? AllPermissions, modules ?? ["sales", "billing"]);
        return Validator(catalog, access).ValidateAsync(module, entity, definition, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static AutomationDefinition WithAction(string action, JsonObject parameters, AutomationTrigger? trigger = null, string id = "step-1") =>
        new(trigger ?? new ManualTrigger(), null, [new AutomationActionStep(id, action, parameters)]);

    [Fact]
    public void Valid_definition_has_no_issues()
    {
        var definition = ValidDefinition(Leaf(Stage, AutomationOperator.Eq, "won"));

        Assert.Empty(Validate(definition));
    }

    [Fact]
    public void Entity_must_belong_to_the_module_and_be_open_for_rules()
    {
        Assert.Contains("kural yazmaya açık değil", Assert.Single(Validate(ValidDefinition(), entity: "sales.unknown")).Message);
        Assert.Contains("kural yazmaya açık değil", Assert.Single(Validate(ValidDefinition(), module: "billing")).Message);
    }

    [Fact]
    public void Closed_module_blocks_the_rule()
    {
        var issue = Assert.Single(Validate(ValidDefinition(), modules: ["billing"]));

        Assert.Contains("etkin değil", issue.Message);
    }

    [Fact]
    public void Entity_read_permission_is_required_to_write_rules_for_it()
    {
        var issue = Assert.Single(Validate(ValidDefinition(), permissions: [WritePermission]));

        Assert.Contains("izniniz yok", issue.Message);
    }

    [Fact]
    public void Trigger_must_be_declared_by_the_module()
    {
        // Etkinlik kayıt türünde güncelleme olayı yok: sales.activity için hiçbir olay beyan edilmedi.
        var issues = Validate(ValidDefinition(trigger: new RecordCreatedTrigger()), entity: Activity);

        Assert.Contains(issues, i => i.Path == "trigger" && i.Message.Contains("tanımlı değil"));
    }

    [Fact]
    public void Field_changed_and_status_transition_check_their_fields()
    {
        Assert.Contains(Validate(ValidDefinition(trigger: new FieldChangedTrigger(["sales.deal.nope"]))), i => i.Message.Contains("Bilinmeyen alan"));
        Assert.Contains(Validate(ValidDefinition(trigger: new FieldChangedTrigger([]))), i => i.Path == "trigger.fields");
        Assert.Contains(Validate(ValidDefinition(trigger: new StatusTransitionTrigger(Title.Key, null, "x"))), i => i.Message.Contains("seçim alanlarında"));
        Assert.Contains(Validate(ValidDefinition(trigger: new StatusTransitionTrigger(Stage.Key, null, "bilinmeyen"))), i => i.Message.Contains("seçeneklerinden"));
        Assert.Empty(Validate(ValidDefinition(trigger: new StatusTransitionTrigger(Stage.Key, "negotiation", "won"))));
    }

    [Fact]
    public void Scheduled_date_relative_inactivity_and_domain_event_triggers_are_checked()
    {
        Assert.Contains(Validate(ValidDefinition(trigger: new ScheduledTrigger("25:00"))), i => i.Path == "trigger.timeOfDay");
        Assert.Empty(Validate(ValidDefinition(trigger: new ScheduledTrigger("06:30"))));

        Assert.Contains(Validate(ValidDefinition(trigger: new DateRelativeTrigger(Title.Key, -3))), i => i.Message.Contains("tarih alanı"));
        Assert.Empty(Validate(ValidDefinition(trigger: new DateRelativeTrigger(DueDate.Key, -3))));

        Assert.Contains(Validate(ValidDefinition(trigger: new InactivityTrigger("sales.deal.nope", 7))), i => i.Path == "trigger.relation");
        Assert.Contains(Validate(ValidDefinition(trigger: new InactivityTrigger(Activities.Key, 0))), i => i.Path == "trigger.days");
        Assert.Empty(Validate(ValidDefinition(trigger: new InactivityTrigger(Activities.Key, 7))));

        Assert.Empty(Validate(ValidDefinition(trigger: new DomainEventTrigger("sales.deal.won"))));
        Assert.Contains(Validate(ValidDefinition(trigger: new DomainEventTrigger("sales.deal.updated"))), i => i.Path == "trigger.eventKey");
        Assert.Contains(Validate(ValidDefinition(trigger: new DomainEventTrigger("sales.deal.yok"))), i => i.Path == "trigger.eventKey");
    }

    [Fact]
    public void Change_conditions_need_an_update_trigger()
    {
        var condition = Leaf(Stage, AutomationOperator.ChangedTo, "won");

        Assert.Contains(Validate(ValidDefinition(condition, new RecordCreatedTrigger())), i => i.Message.Contains("güncelleme tetikleyicilerinde"));
        Assert.Empty(Validate(ValidDefinition(condition, new FieldChangedTrigger([Stage.Key]))));
    }

    [Fact]
    public void At_least_one_action_and_at_most_twenty()
    {
        Assert.Contains(Validate(new AutomationDefinition(new ManualTrigger(), null, [])), i => i.Path == "actions");

        var many = Enumerable.Range(0, AutomationDefinitionValidator.MaxActions + 1)
            .Select(i => new AutomationActionStep($"s{i}", FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1 }))
            .ToList();
        Assert.Contains(Validate(new AutomationDefinition(new ManualTrigger(), null, many)), i => i.Message.Contains("En çok"));
    }

    [Fact]
    public void Step_ids_must_be_unique_and_well_formed()
    {
        var step = new JsonObject { ["subject"] = "a", ["dueInDays"] = 1 };
        var duplicate = new AutomationDefinition(
            new ManualTrigger(), null, [new("a", FollowUp.Key, step.DeepClone().AsObject()), new("a", FollowUp.Key, step.DeepClone().AsObject())]);
        var bad = WithAction(FollowUp.Key, step, id: "Bad Id!");

        Assert.Single(Validate(duplicate));
        Assert.Single(Validate(bad));
    }

    [Fact]
    public void Unknown_action_and_unknown_parameters_are_rejected()
    {
        Assert.Contains("Bilinmeyen", Assert.Single(Validate(WithAction("sales.yok", new JsonObject()))).Message);

        var issues = Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1, ["extra"] = 5 }));
        Assert.Contains("'extra' parametresi yok", Assert.Single(issues).Message);
    }

    [Fact]
    public void Required_parameters_and_types_are_checked_against_the_schema()
    {
        Assert.Contains(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a" })), i => i.Path.EndsWith("dueInDays") && i.Message.Contains("zorunlu"));
        Assert.Contains(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = "çok" })), i => i.Path.EndsWith("dueInDays"));
        Assert.Contains(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1, ["priority"] = "acil" })), i => i.Path.EndsWith("priority"));
        Assert.Contains(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1, ["ownerId"] = "Ayşe" })), i => i.Path.EndsWith("ownerId"));
        Assert.Contains(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = new string('x', 501), ["dueInDays"] = 1 })), i => i.Path.EndsWith("subject"));
        Assert.Empty(Validate(WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1, ["priority"] = "high", ["ownerId"] = Guid.NewGuid().ToString() })));
    }

    [Fact]
    public void Parameters_can_bind_to_a_record_field_of_the_same_type()
    {
        var bound = new JsonObject
        {
            ["subject"] = "a",
            ["dueInDays"] = new JsonObject { ["$field"] = Score.Key },
            ["ownerId"] = new JsonObject { ["$field"] = Owner.Key },
        };
        Assert.Empty(Validate(WithAction(FollowUp.Key, bound)));

        // Tip uyuşmuyor, alan yok, bağlamaya kapalı parametre, görülemeyen alan.
        bound["dueInDays"] = new JsonObject { ["$field"] = Title.Key };
        Assert.Contains(Validate(WithAction(FollowUp.Key, bound)), i => i.Message.Contains("uyuşmuyor"));
        bound["dueInDays"] = new JsonObject { ["$field"] = "sales.deal.yok" };
        Assert.Contains(Validate(WithAction(FollowUp.Key, bound)), i => i.Message.Contains("bilinmiyor"));
        bound["dueInDays"] = 1;
        bound["note"] = new JsonObject { ["$field"] = Title.Key };
        Assert.Contains(Validate(WithAction(FollowUp.Key, bound)), i => i.Message.Contains("bağlanamaz"));
        bound.Remove("note");
        bound["dueInDays"] = new JsonObject { ["$field"] = Secret.Key };
        Assert.Contains(Validate(WithAction(FollowUp.Key, bound), permissions: ["sales.deals.read", WritePermission]), i => i.Message.Contains("görme izniniz yok"));
    }

    [Fact]
    public void Author_cannot_publish_an_action_that_needs_a_permission_they_lack()
    {
        var definition = WithAction(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1 });

        var issue = Assert.Single(Validate(definition, permissions: ["sales.deals.read"]));

        Assert.Contains("gereken izne sahip değilsiniz", issue.Message);
    }

    [Fact]
    public void Action_needs_its_licensed_modules_to_be_open()
    {
        var definition = WithAction(Notify.Key, new JsonObject { ["message"] = "Merhaba" });

        Assert.Empty(Validate(definition));
        Assert.Contains("etkin değil", Assert.Single(Validate(definition, modules: ["sales"])).Message);
    }
}
