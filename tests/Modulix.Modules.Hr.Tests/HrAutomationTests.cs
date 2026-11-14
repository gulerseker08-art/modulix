using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using Modulix.Modules.Hr.Automation;

namespace Modulix.Modules.Hr.Tests;

/// <summary>
/// İK'nın kural yeteneği beyanı (ADR-0042 bölüm 18). Katalog açılışta beyanı doğrular ve hatalı beyan uygulamayı açtırmaz; bu yüzden gerçek katalogla
/// kurulur. Şablonların yayın doğrulaması gerçek veritabanıyla entegrasyon testindedir (<c>HrAutomationTests</c>).
/// </summary>
public partial class HrAutomationTests
{
    private static readonly ModuleCatalog Catalog = ModuleCatalog.Create(new HrModule());
    private static readonly AutomationCapabilities Declared = HrAutomation.Capabilities;

    [Fact]
    public void The_real_catalog_accepts_the_declaration_with_the_action_and_every_template_in_order()
    {
        Assert.NotNull(Catalog.FindAutomationAction(HrAutomation.StartChecklist));
        Assert.Equal(HrAutomationTemplates.All.Select(t => t.Key),
            Catalog.AutomationTemplates.Where(t => t.ModuleKey == HrModule.ModuleKey).Select(t => t.Template.Key));
        Assert.Equal(7, HrAutomationTemplates.All.Count);
        Assert.All(HrAutomationTemplates.All, t => Assert.StartsWith(HrModule.ModuleKey + ".", t.Key));
    }

    [Fact]
    public void Core_keys_written_in_the_module_match_core()
    {
        Assert.Equal(NotifyAutomation.RecipientLookup, HrAutomation.NotifyRecipientLookup);
        Assert.Equal(NotifyAutomation.NotifyAction, HrAutomation.NotifyAction);
    }

    [Fact]
    public void Every_declared_date_field_is_scanned_by_the_record_source_and_nothing_else()
    {
        foreach (var entity in Declared.Entities)
        {
            var dates = Declared.Fields.Where(f => f.EntityKey == entity.EntityKey && f.Type == AutomationFieldType.Date).Select(f => f.Name).Order().ToList();
            if (entity.RecordSource is null)
            {
                Assert.Empty(dates);
                continue;
            }

            Assert.Equal(dates, HrAutomation.DateFields[entity.EntityKey].Order());
        }

        Assert.Equal(4, Declared.Entities.Count(e => e.RecordSource is not null));
    }

    [Fact]
    public void User_fields_are_bindable_to_the_notification_recipient()
    {
        string[] expected =
        [
            "hr.employee.userId", "hr.leave-request.employeeUserId", "hr.leave-request.approverUserId", "hr.expense-claim.employeeUserId",
            "hr.expense-claim.approverUserId", "hr.asset-assignment.employeeUserId", "hr.checklist-task.assigneeUserId", "hr.checklist-task.employeeUserId",
        ];
        var recipients = Declared.Fields.Where(f => f.LookupKey == HrAutomation.NotifyRecipientLookup).Select(f => f.Key).ToList();

        Assert.Equal(expected.Order(), recipients.Order());
        Assert.All(Declared.Fields.Where(f => recipients.Contains(f.Key)), f => Assert.Equal(AutomationFieldType.Lookup, f.Type));
    }

    [Fact]
    public void The_start_action_needs_checklist_management_and_an_open_hr_module_and_supports_dry_run()
    {
        var definition = Catalog.FindAutomationAction(HrAutomation.StartChecklist)!.Value.Action.Definition;

        Assert.Equal([HrModule.Permissions.ChecklistsRead, HrModule.Permissions.ChecklistsManage], definition.RequiredPermissions);
        Assert.Equal([HrModule.ModuleKey], definition.RequiredModules);
        Assert.Equal(AutomationSideEffect.Write, definition.SideEffect);
        Assert.True(definition.SupportsDryRun);
        Assert.Equal(["kind"], definition.Parameters.Where(p => p.Required).Select(p => p.Name));
    }

    [Fact]
    public void Template_bindings_and_text_variables_point_at_fields_of_their_own_entity()
    {
        var fields = Declared.Fields.ToDictionary(f => f.Key);
        foreach (var template in HrAutomationTemplates.All)
        {
            var step = Assert.Single(template.Definition.Actions);
            Assert.Contains(step.Action, new[] { HrAutomation.NotifyAction, HrAutomation.StartChecklist });

            foreach (var (name, value) in step.Parameters)
            {
                if (value is JsonObject binding && binding["$field"]?.GetValue<string>() is { } bound)
                {
                    var field = fields[bound];
                    Assert.Equal(template.EntityKey, field.EntityKey);
                    Assert.Equal(name == "recipientId" ? AutomationFieldType.Lookup : AutomationFieldType.Date, field.Type);
                }
                else if (value?.ToString() is { } text)
                {
                    foreach (Match match in Variable().Matches(text))
                    {
                        Assert.Equal(template.EntityKey, fields[match.Groups[1].Value].EntityKey);
                    }
                }
            }

            switch (template.Definition.Trigger)
            {
                case DateRelativeTrigger date:
                    Assert.Contains(date.Field.Split('.').Last(), HrAutomation.DateFields[template.EntityKey]);
                    break;
                case StatusTransitionTrigger transition:
                    Assert.Contains(fields[transition.Field].Options, o => o.Key == transition.To);
                    break;
                case RecordCreatedTrigger:
                    break;
                default:
                    Assert.Fail($"Beklenmeyen tetikleyici: {template.Definition.Trigger.GetType().Name}");
                    break;
            }
        }
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex Variable();
}
