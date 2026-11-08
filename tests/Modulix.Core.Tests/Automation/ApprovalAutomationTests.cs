using System.Text.Json.Nodes;
using Modulix.Core.Approvals;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Kural aksiyonu "Onaya gönder" ve kayda bağlı onay (ADR-0027, Dilim 4c): beyan, sorumlu alanı, bağlantı, tutar bağlama, aksiyona özgü denetim.</summary>
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
        Assert.Equal(["process", "title", "description", "amount", "currency"], definition.Parameters.Select(p => p.Name));

        // Süreç adı sabit yazılır; başlık kayıt değişkeni alır; Core kendi başına kural yazılabilen kayıt türü beyan etmez.
        Assert.False(definition.Parameters.Single(p => p.Name == "process").AllowFieldBinding);
        Assert.True(definition.Parameters.Single(p => p.Name == "title").AllowFieldBinding);
        Assert.DoesNotContain(catalog.AutomationEntities, e => e.ModuleKey == CoreModule.ModuleKey);
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
    public void A_request_links_to_a_record_with_a_source_key_and_refuses_an_invalid_link()
    {
        var definition = ApprovalDefinition.Create(
            "Süreç", null, null, [new ApprovalStepDefinition("Yönetici", 1, Guid.NewGuid(), null, null, null)], Guid.NewGuid(), DateTimeOffset.UtcNow);
        var (request, _) = ApprovalRequest.Open(definition, "Başlık", null, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var record = Guid.NewGuid();

        request.LinkToRecord("crm.opportunity", record, "anahtar-1");

        Assert.Equal(("crm.opportunity", record, "anahtar-1"), (request.EntityKey, request.RecordId, request.SourceKey));
        Assert.Throws<ArgumentException>(() => request.LinkToRecord("", record, null));
        Assert.Throws<ArgumentException>(() => request.LinkToRecord("crm.opportunity", Guid.Empty, null));
        Assert.Throws<ArgumentException>(() => request.LinkToRecord("crm.opportunity", record, new string('a', ApprovalRequest.SourceKeyMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => request.LinkToRecord(new string('a', ApprovalRequest.EntityKeyMaxLength + 1), record, null));
    }

    [Fact]
    public void A_money_field_may_feed_a_number_parameter_but_other_type_mismatches_stay_refused()
    {
        var access = new FakeAccess(["sales.deals.read", WritePermission, SecretPermission], ["sales", "billing"]);
        IReadOnlyList<AutomationIssue> Validate(JsonObject parameters) =>
            Validator(Catalog(), access).ValidateAsync(
                "sales", Entity, new AutomationDefinition(new ManualTrigger(), null, [new AutomationActionStep("s1", FollowUp.Key, parameters)]),
                CancellationToken.None).GetAwaiter().GetResult();

        // Tutar (para) alanı sayı parametresine bağlanır; para birimi ayrı parametreyle verilir.
        Assert.Empty(Validate(new JsonObject { ["subject"] = "a", ["dueInDays"] = new JsonObject { ["$field"] = Amount.Key } }));

        // Metin alanı sayı parametresine bağlanamaz.
        Assert.Contains(
            Validate(new JsonObject { ["subject"] = "a", ["dueInDays"] = new JsonObject { ["$field"] = Title.Key } }),
            i => i.Message.Contains("uyuşmuyor"));
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

        public Task<IReadOnlyList<AutomationIssue>> CheckAsync(AutomationActionStep step, string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationIssue>>([new AutomationIssue(path, message)]);
    }
}
