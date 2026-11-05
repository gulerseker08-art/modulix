using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Crm.Automation;

namespace Modulix.Modules.Crm.Tests;

/// <summary>
/// CRM'in başlangıç kuralı şablonları (ADR-0027, Dilim 3b): kendi beyanıyla tutarlı ve zararsız. Gerçek katalogla kaydı
/// <c>CrmAutomationDeclarationTests</c> (entegrasyon projesi) sınar.
/// </summary>
public class CrmAutomationTemplateTests
{
    private static readonly AutomationCapabilities Declared = CrmAutomation.Capabilities;

    [Fact]
    public void The_module_declares_exactly_the_template_list_with_unique_keys()
    {
        var keys = Declared.Templates.Select(t => t.Key).ToList();

        Assert.Equal(CrmAutomationTemplates.All.Select(t => t.Key), keys);
        Assert.Equal(4, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith(CrmModule.ModuleKey + ".", key));
    }

    [Fact]
    public void Templates_only_open_follow_up_activities_with_the_declared_action()
    {
        var action = Assert.Single(Declared.Actions).Definition;

        foreach (var template in CrmAutomationTemplates.All)
        {
            var step = Assert.Single(template.Definition.Actions);
            Assert.Equal(action.Key, step.Action);
            Assert.Equal(CrmAutomation.FollowUpAction, step.Action);
            Assert.Equal(AutomationSideEffect.Write, action.SideEffect);
            Assert.Equal([CrmModule.Permissions.ActivitiesWrite], action.RequiredPermissions);
            Assert.Contains(Declared.Entities, e => e.EntityKey == template.EntityKey);
        }
    }

    [Fact]
    public void Template_triggers_point_at_fields_and_relations_of_their_own_entity()
    {
        foreach (var template in CrmAutomationTemplates.All)
        {
            switch (template.Definition.Trigger)
            {
                case DateRelativeTrigger date:
                    var field = Assert.Single(Declared.Fields, f => f.Key == date.Field);
                    Assert.Equal(template.EntityKey, field.EntityKey);
                    Assert.Equal(AutomationFieldType.Date, field.Type);
                    break;
                case InactivityTrigger inactivity:
                    var relation = Assert.Single(Declared.Relations, r => r.Key == inactivity.Relation);
                    Assert.Equal(template.EntityKey, relation.EntityKey);
                    Assert.InRange(inactivity.Days, 1, 365);
                    break;
                default:
                    Assert.Fail($"Beklenmeyen tetikleyici: {template.Definition.Trigger.GetType().Name}");
                    break;
            }
        }
    }

    [Fact]
    public void Template_definitions_survive_the_stored_json_form_unchanged()
    {
        foreach (var template in CrmAutomationTemplates.All)
        {
            var json = AutomationJson.Serialize(template.Definition);

            Assert.Equal(json, AutomationJson.Serialize(AutomationJson.Deserialize(json)));
        }
    }
}
