using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Başlangıç şablonlarının katalog denetimi (ADR-0027, Dilim 3b): bozuk beyan uygulamayı açtırmaz.</summary>
public class AutomationTemplateCatalogTests
{
    private static AutomationDefinition Definition(string action = "sales.follow-up.create", params string[] stepIds) =>
        new(
            new ManualTrigger(),
            null,
            [.. (stepIds.Length == 0 ? ["a"] : stepIds).Select(id => new AutomationActionStep(id, action, new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 1 }))]);

    private static AutomationTemplateDefinition Template(
        string key = "sales.deal-follow-up", string name = "Takip", string description = "Açıklama", string entity = Entity, AutomationDefinition? definition = null) =>
        new(key, name, description, entity, definition ?? Definition());

    private static AutomationCapabilities With(params AutomationTemplateDefinition[] templates) =>
        new()
        {
            Entities = Capabilities.Entities,
            Fields = Capabilities.Fields,
            Actions = Capabilities.Actions,
            Templates = templates,
        };

    private static Action Build(params AutomationTemplateDefinition[] templates) => () => Catalog(With(templates));

    [Fact]
    public void A_valid_template_is_registered_and_found_by_key()
    {
        var catalog = Catalog(With(Template()));

        var found = catalog.FindAutomationTemplate("sales.deal-follow-up");

        Assert.NotNull(found);
        Assert.Equal("sales", found.Value.ModuleKey);
        Assert.Equal(Entity, found.Value.Template.EntityKey);
        Assert.Single(catalog.AutomationTemplates);
        Assert.Null(catalog.FindAutomationTemplate("sales.yok"));
    }

    [Fact]
    public void A_module_without_templates_declares_none_and_templates_alone_make_the_declaration_non_empty()
    {
        Assert.Empty(Catalog().AutomationTemplates);
        Assert.False(With(Template()).IsEmpty);
        Assert.True(AutomationCapabilities.None.IsEmpty);
    }

    [Fact]
    public void Template_key_name_description_and_entity_are_validated()
    {
        Assert.Throws<InvalidOperationException>(Build(Template(key: "billing.deal-follow-up")));
        Assert.Throws<InvalidOperationException>(Build(Template(key: "Sales.Bad Key")));
        Assert.Throws<InvalidOperationException>(Build(Template(name: " ")));
        Assert.Throws<InvalidOperationException>(Build(Template(name: new string('a', 201))));
        Assert.Throws<InvalidOperationException>(Build(Template(description: "")));
        Assert.Throws<InvalidOperationException>(Build(Template(entity: "billing.invoice")));
    }

    [Fact]
    public void Template_keys_must_be_unique()
    {
        Assert.Throws<InvalidOperationException>(Build(Template(), Template()));
    }

    [Fact]
    public void Template_actions_must_exist_and_step_ids_must_be_unique()
    {
        Assert.Throws<InvalidOperationException>(Build(Template(definition: Definition(action: "sales.yok.yok"))));
        Assert.Throws<InvalidOperationException>(Build(Template(definition: new AutomationDefinition(new ManualTrigger(), null, []))));
        Assert.Throws<InvalidOperationException>(Build(Template(definition: Definition("sales.follow-up.create", "a", "a"))));
    }
}
