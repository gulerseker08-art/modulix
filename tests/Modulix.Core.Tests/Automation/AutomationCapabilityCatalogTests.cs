using Modulix.Core.Approvals;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationCapabilityCatalogTests
{
    private static Action Build(AutomationCapabilities capabilities) => () => Catalog(capabilities);

    [Fact]
    public void Valid_capabilities_are_registered_and_found_by_key()
    {
        var catalog = Catalog();

        Assert.Equal("sales", catalog.FindAutomationEntity(Entity)!.Value.ModuleKey);
        Assert.Equal("Tutar", catalog.FindAutomationField(Amount.Key)!.Label);
        Assert.Equal(DomainEventTypes.RecordCreated, catalog.FindAutomationEvent("sales.deal.created")!.EventType);
        Assert.Equal(Activity, catalog.FindAutomationRelation(Activities.Key)!.TargetEntityKey);
        Assert.Equal("sales", catalog.FindAutomationAction(FollowUp.Key)!.Value.ModuleKey);
        Assert.Equal(9, catalog.AutomationFieldsOf(Entity).Count());
        Assert.Equal(3, catalog.AutomationEventsOf(Entity).Count());
        Assert.Equal(2, catalog.AutomationActions.Count(a => a.ModuleKey == "sales"));

        // Core'un tek aksiyonu "Onaya gönder"dir (Dilim 4c); kayıt türü, alan ya da olay beyan etmez.
        Assert.Equal(ApprovalAutomation.RequestAction, Assert.Single(catalog.AutomationActions, a => a.ModuleKey == "core").Action.Definition.Key);
    }

    [Fact]
    public void Module_without_capabilities_is_valid_and_declares_nothing()
    {
        var catalog = Catalog(AutomationCapabilities.None);

        Assert.Empty(catalog.AutomationEntities);

        // Yetenek beyan etmeyen modül hiçbir aksiyon eklemez; kalan yalnızca Core'un "Onaya gönder"idir.
        Assert.Equal(ApprovalAutomation.RequestAction, Assert.Single(catalog.AutomationActions).Action.Definition.Key);
    }

    [Fact]
    public void Entity_key_must_start_with_the_module_key()
    {
        var capabilities = new AutomationCapabilities { Entities = [new("billing.invoice", "Fatura")] };

        Assert.Throws<InvalidOperationException>(Build(capabilities));
    }

    [Fact]
    public void Entity_and_field_keys_must_be_unique()
    {
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = [new(Entity, "A"), new(Entity, "B")] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = [new(Entity, "A")],
            Fields = [Title, Title],
        }));
    }

    [Fact]
    public void Field_must_belong_to_a_declared_entity_and_have_a_valid_name()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A") };

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Fields = [new(Activity, "x", "X", AutomationFieldType.Text)] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Fields = [new(Entity, "Bad Name", "X", AutomationFieldType.Text)] }));
    }

    [Fact]
    public void Money_field_must_name_its_currency_field_and_only_money_may()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A") };

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Fields = [new(Entity, "price", "Fiyat", AutomationFieldType.Money)] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = entities,
            Fields = [new(Entity, "price", "Fiyat", AutomationFieldType.Number) { CurrencyField = "currency" }],
        }));
    }

    [Fact]
    public void Lookup_field_needs_a_known_lookup_and_read_permission_must_exist()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A") };

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = entities,
            Fields = [new(Entity, "customerId", "Müşteri", AutomationFieldType.Lookup) { LookupKey = "sales.yok" }],
        }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = entities,
            Fields = [new(Entity, "secret", "Gizli", AutomationFieldType.Number) { ReadPermission = "sales.yok.read" }],
        }));
    }

    [Fact]
    public void Event_must_use_a_declared_entity_and_a_known_or_module_prefixed_type()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A") };

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Events = [new("sales.x", Activity, DomainEventTypes.RecordCreated, "X")] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Events = [new("sales.x", Entity, "billing.invoice.paid", "X")] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Entities = entities, Events = [new("billing.x", Entity, DomainEventTypes.RecordCreated, "X")] }));
    }

    [Fact]
    public void Relation_needs_a_resolver_that_implements_the_contract()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A"), new(Activity, "B") };

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = entities,
            Relations = [new("sales.deal.acts", Entity, Activity, "Aktiviteler", [], typeof(string))],
        }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Entities = entities,
            Relations = [new("sales.deal.acts", Entity, "billing.invoice", "Aktiviteler", [], typeof(NoopResolver))],
        }));
    }

    [Fact]
    public void Action_key_permissions_modules_and_handler_are_validated()
    {
        AutomationActionDefinition Action(string key = "sales.a.b", string[]? permissions = null, string[]? modules = null) =>
            new(key, "Ad", "Açıklama", [], permissions ?? [], modules ?? [], AutomationSideEffect.Read, false);

        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Actions = [AutomationActionRegistration.For<NoopAction>(Action("billing.a.b"))] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Actions = [AutomationActionRegistration.For<NoopAction>(Action(permissions: ["billing.invoices.read"]))] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Actions = [AutomationActionRegistration.For<NoopAction>(Action(modules: ["yok"]))] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities { Actions = [new(Action(), typeof(string))] }));
        Assert.Throws<InvalidOperationException>(Build(new AutomationCapabilities
        {
            Actions = [AutomationActionRegistration.For<NoopAction>(Action()), AutomationActionRegistration.For<NoopAction>(Action())],
        }));

        // Başka modülün iznini istemek yasak; başka modülün lisansını istemek serbest (lisans bağımlılığı kod bağımlılığı değildir).
        _ = Catalog(new AutomationCapabilities { Actions = [AutomationActionRegistration.For<NoopAction>(Action(modules: ["billing"]))] });
    }

    [Fact]
    public void Action_parameters_are_validated()
    {
        AutomationCapabilities With(AutomationParameter parameter) => new()
        {
            Actions =
            [
                AutomationActionRegistration.For<NoopAction>(new("sales.a.b", "Ad", "Açıklama", [parameter], [], [], AutomationSideEffect.Read, false)),
            ],
        };

        Assert.Throws<InvalidOperationException>(Build(With(new AutomationParameter("Bad-Name", "X", AutomationFieldType.Text))));
        Assert.Throws<InvalidOperationException>(Build(With(new AutomationParameter("x", "X", AutomationFieldType.Choice))));
        Assert.Throws<InvalidOperationException>(Build(With(new AutomationParameter("x", "X", AutomationFieldType.Lookup))));
        Assert.Throws<InvalidOperationException>(Build(With(new AutomationParameter("x", "X", AutomationFieldType.Money))));
    }

    [Fact]
    public void Core_defines_the_automation_permissions_and_audit_type()
    {
        var catalog = Catalog();

        foreach (var key in new[]
        {
            CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CoreModule.Permissions.PublishAutomation,
            CoreModule.Permissions.RunAutomation, CoreModule.Permissions.ReadAutomationLogs, CoreModule.Permissions.ApproveAutomation,
        })
        {
            Assert.True(catalog.ContainsPermission(key), key);
        }

        Assert.NotNull(catalog.FindAuditedEntity(CoreModule.AuditedEntityKeys.AutomationRule));
    }
}
