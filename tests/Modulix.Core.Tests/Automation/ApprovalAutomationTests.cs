using System.Text.Json.Nodes;
using Modulix.Core.Approvals;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Kural aksiyonu "Onaya gönder" (ADR-0027, Dilim 4c-2): beyan, sorumlu alanı, kural adımı anahtarı, aksiyona özgü denetim.</summary>
public class ApprovalAutomationTests
{
    private static AutomationCapabilities Owned(string? ownerField, params AutomationFieldDefinition[] fields) => new()
    {
        Entities = [new(Entity, "Fırsat", "sales.deals.read") { OwnerField = ownerField }],
        Fields = fields,
    };

    [Fact]
    public void Core_declares_the_approval_action_with_the_request_permission_and_no_entities()
    {
        var catalog = Catalog();

        var found = catalog.FindAutomationAction(ApprovalAutomation.RequestAction);

        Assert.NotNull(found);
        Assert.Equal(CoreModule.ModuleKey, found.Value.ModuleKey);
        var definition = found.Value.Action.Definition;
        Assert.Equal([CoreModule.Permissions.RequestApprovals], definition.RequiredPermissions);
        Assert.Equal(AutomationSideEffect.Write, definition.SideEffect);
        Assert.True(definition.SupportsDryRun);
        // Tutar ve para birimi parametre değildir: kayda bağlı onay (Dilim 4c) onları kayıttan okur.
        Assert.Equal(["process", "title", "description"], definition.Parameters.Select(p => p.Name));

        // Süreç adı sabit yazılır; başlık isteğe bağlıdır (boşsa kaydın adı) ve kayıt değişkeni alır; Core'un kayıt türü yalnızca süreli belgedir.
        Assert.False(definition.Parameters.Single(p => p.Name == "process").AllowFieldBinding);
        Assert.True(definition.Parameters.Single(p => p.Name == "title").AllowFieldBinding);
        Assert.False(definition.Parameters.Single(p => p.Name == "title").Required);
        Assert.Equal(["core.credential"], catalog.AutomationEntities.Where(e => e.ModuleKey == CoreModule.ModuleKey).Select(e => e.Entity.EntityKey));
    }

    [Fact]
    public void The_rule_identity_gets_the_read_permission_of_the_approval_targets_amount_field()
    {
        // Tutar kayıttan okunur (kayda bağlı onay): "Onaya gönder" adımı olan kural, hedefin tutar alanı hassas olsa da onu okuyabilmeli.
        var target = new Modulix.Core.Contracts.Approvals.ApprovalTargetDefinition(Entity, "Fırsat", "title", "sales/{id}") { AmountField = "margin" };
        var catalog = new ModuleCatalog([
            new CoreModule(),
            new TestModule("sales", Capabilities, "sales.deals.read", WritePermission, SecretPermission) { ApprovalTargets = [target] },
            new TestModule("billing", AutomationCapabilities.None, "billing.invoices.read"),
        ]);
        var step = new AutomationActionStep("onay", ApprovalAutomation.RequestAction, new JsonObject { ["process"] = "İndirim" });
        var definition = new AutomationDefinition(new ManualTrigger(), null, [step]);

        var permissions = AutomationRuleIdentity.Permissions(Entity, definition, catalog);

        Assert.Contains(SecretPermission, permissions);
        Assert.Contains(CoreModule.Permissions.RequestApprovals, permissions);

        // Onaya açık olmayan kayıt türünde ya da başka aksiyonda hassas alan izni eklenmez.
        Assert.DoesNotContain(SecretPermission, AutomationRuleIdentity.Permissions(Entity, definition, Catalog()));
        Assert.DoesNotContain(SecretPermission, AutomationRuleIdentity.Permissions(Entity, ValidDefinition(), catalog));
    }

    [Fact]
    public void The_owner_field_must_be_a_declared_lookup_field_of_the_entity()
    {
        // Geçerli: beyan edilmiş lookup alanı.
        Assert.NotNull(Catalog(Owned("ownerId", Owner, Title)));

        // Geçersiz: bilinmeyen alan, lookup olmayan alan, başka türün alanı.
        Assert.Throws<InvalidOperationException>(() => Catalog(Owned("nope", Owner)));
        Assert.Throws<InvalidOperationException>(() => Catalog(Owned("title", Owner, Title)));
        Assert.Throws<InvalidOperationException>(() => Catalog(Owned("ownerId", Title)));
    }

    [Fact]
    public void A_rule_opened_request_carries_its_source_key_and_refuses_an_invalid_one()
    {
        var definition = ApprovalDefinition.Create(
            "Süreç", null, null, [new ApprovalStepDefinition("Yönetici", 1, Guid.NewGuid(), null, null, null)], Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        var (manual, _) = ApprovalRequest.Open(definition, "Başlık", null, null, null, Guid.NewGuid(), now);
        var (byRule, _) = ApprovalRequest.Open(definition, "Başlık", null, null, null, Guid.NewGuid(), now, target: null, sourceKey: "anahtar-1");

        Assert.Null(manual.SourceKey);
        Assert.Equal("anahtar-1", byRule.SourceKey);
        Assert.Throws<ArgumentException>(() => ApprovalRequest.Open(definition, "Başlık", null, null, null, Guid.NewGuid(), now, target: null, sourceKey: ""));
        Assert.Throws<ArgumentException>(() => ApprovalRequest.Open(
            definition, "Başlık", null, null, null, Guid.NewGuid(), now, target: null, sourceKey: new string('a', ApprovalRequest.SourceKeyMaxLength + 1)));
    }

    [Fact]
    public void A_step_check_runs_only_for_its_own_action_and_its_issues_block_publishing()
    {
        var access = new FakeAccess(["sales.deals.read", WritePermission, SecretPermission], ["sales", "billing"]);
        var check = new FixedCheck(FollowUp.Key, "Süreç yok.");
        IReadOnlyList<AutomationIssue> Validate(string action, JsonObject parameters) =>
            new AutomationDefinitionValidator(Catalog(), CatalogService(Catalog(), access), access, [check]).ValidateAsync(
                "sales", Entity, new AutomationDefinition(new ManualTrigger(), null, [new AutomationActionStep("s1", action, parameters)]),
                CancellationToken.None).GetAwaiter().GetResult();

        var own = Validate(FollowUp.Key, new JsonObject { ["subject"] = "a", ["dueInDays"] = 1 });
        var other = Validate(Notify.Key, new JsonObject { ["message"] = "a" });

        Assert.Contains(own, i => i.Path == "actions[0]" && i.Message == "Süreç yok.");
        Assert.DoesNotContain(other, i => i.Message == "Süreç yok.");
    }

    private sealed class FixedCheck(string actionKey, string message) : IAutomationStepCheck
    {
        public string ActionKey => actionKey;

        public Task<IReadOnlyList<AutomationIssue>> CheckAsync(string entityKey, AutomationActionStep step, string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationIssue>>([new AutomationIssue(path, message)]);
    }
}
