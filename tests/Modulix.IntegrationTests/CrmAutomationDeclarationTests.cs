using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Automation;

namespace Modulix.IntegrationTests;

/// <summary>
/// CRM'in kural yeteneği beyanı gerçek katalogla (veritabanı gerekmez). Katalog açılışta beyanı doğrular; hatalı beyan uygulamayı açtırmaz,
/// bu yüzden birim testin göremeyeceği hatalar (lookup anahtarı, izin, tip uyumu) burada yakalanır.
/// </summary>
public class CrmAutomationDeclarationTests
{
    private static readonly ModuleCatalog Catalog = ModuleCatalog.Create(new CrmModule());

    [Fact]
    public void The_real_catalog_accepts_the_declaration_and_exposes_the_follow_up_action()
    {
        Assert.NotNull(Catalog.FindAutomationAction(CrmAutomation.FollowUpAction));
        Assert.NotNull(Catalog.FindAutomationEntity(CrmModule.Entities.Lead));
        Assert.NotNull(Catalog.FindAutomationEntity(CrmModule.Entities.Opportunity));
        Assert.NotNull(Catalog.FindAutomationRelation(CrmAutomation.LeadActivities));
        Assert.NotNull(Catalog.FindAutomationRelation(CrmAutomation.OpportunityActivities));
        Assert.All(Catalog.AutomationEntities, e => Assert.NotNull(e.Entity.RecordSource));
    }

    [Fact]
    public void The_real_catalog_registers_every_crm_template_in_declaration_order()
    {
        Assert.Equal(CrmAutomationTemplates.All.Select(t => t.Key), Catalog.AutomationTemplates.Select(t => t.Template.Key));
        Assert.All(Catalog.AutomationTemplates, t => Assert.Equal(CrmModule.ModuleKey, t.ModuleKey));
        Assert.NotNull(Catalog.FindAutomationTemplate(CrmAutomationTemplates.LeadInactivity));
    }

    [Fact]
    public void Sensitive_fields_are_declared_with_the_same_read_permission_so_the_rule_engine_hides_them()
    {
        var declared = CrmAutomation.Capabilities.Fields.ToDictionary(f => f.Key);

        foreach (var sensitive in ((Modulix.Core.Contracts.Modules.IModule)new CrmModule()).SensitiveFields)
        {
            foreach (var name in sensitive.Fields)
            {
                if (declared.TryGetValue($"{sensitive.EntityKey}.{name}", out var field))
                {
                    Assert.Equal(sensitive.Permission, field.ReadPermission);
                }
            }
        }

        // Tutar ve olasılık koşulda kullanılabilir ve mutlaka tutar iznine bağlıdır.
        Assert.Equal(CrmModule.Permissions.OpportunityAmountsRead, declared["crm.opportunity.amount"].ReadPermission);
        Assert.Equal(CrmModule.Permissions.OpportunityAmountsRead, declared["crm.opportunity.probability"].ReadPermission);
    }

    [Fact]
    public void The_follow_up_action_needs_activity_write_and_an_open_crm_module_and_supports_dry_run()
    {
        var definition = Catalog.FindAutomationAction(CrmAutomation.FollowUpAction)!.Value.Action.Definition;

        Assert.Equal([CrmModule.Permissions.ActivitiesWrite], definition.RequiredPermissions);
        Assert.Equal([CrmModule.ModuleKey], definition.RequiredModules);
        Assert.Equal(AutomationSideEffect.Write, definition.SideEffect);
        Assert.True(definition.SupportsDryRun);
        Assert.Equal(AutomationIdempotency.Derived, definition.Idempotency);
    }
}
