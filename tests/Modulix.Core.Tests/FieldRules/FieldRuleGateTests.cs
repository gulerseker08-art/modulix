using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.FieldRules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.FieldRules;

/// <summary>
/// ADR-0029 K7: alan kurallarının kayıt boru hattındaki kapısı. Kural kaynağı tenant'a bağlı olduğundan (<see cref="ITenantFieldRules"/>)
/// kapı yalnızca verilen kuralları uygular; kuralı olmayan kayıt türünde kural servisine hiç gitmez.
/// </summary>
public class FieldRuleGateTests
{
    private static readonly Guid RecordId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static FieldRule Rule(string key, AutomationFieldDefinition field, FieldRuleEffect effect, ConditionNode? when = null, string? message = null) =>
        new(key, field.Key, effect, when) { Message = message };

    private static ConditionLeaf Won => Leaf(Stage, AutomationOperator.Eq, "won");

    private static (FieldRuleGate Gate, FixedRules Rules) Create(params FieldRule[] rules)
    {
        var modules = Catalog();
        var access = new FakeAccess(["sales.deals.read", WritePermission], ["sales"]);
        var source = new FixedRules(rules);
        return (new FieldRuleGate(modules, Services(source, CatalogService(modules, access))), source);
    }

    private static IServiceProvider Services(FixedRules rules, IAutomationCatalog catalog) =>
        new ServiceCollection()
            .AddSingleton<ITenantFieldRules>(rules)
            .AddSingleton(catalog)
            .AddSingleton<IAutomationRelationCounter>(new FixedCounter(null))
            .AddSingleton<ITenantClock>(new FixedClock())
            .BuildServiceProvider();

    private static IReadOnlyList<Modulix.Core.Contracts.Records.RecordFieldError> Create(FieldRuleGate gate, JsonObject values) =>
        gate.CheckAsync(Entity, null, null, values, CancellationToken.None).GetAwaiter().GetResult();

    private static IReadOnlyList<Modulix.Core.Contracts.Records.RecordFieldError> Update(FieldRuleGate gate, JsonObject before, JsonObject after) =>
        gate.CheckAsync(Entity, RecordId, before, after, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void Records_that_satisfy_the_rules_pass_and_conditions_that_do_not_hold_do_nothing()
    {
        var (gate, _) = Create(
            Rule("title-required", Title, FieldRuleEffect.Required, Won),
            Rule("no-won-without-score", Score, FieldRuleEffect.Block, And(Won, Leaf(Score, AutomationOperator.IsEmpty)), "Kazanılan fırsatta puan olmalı."),
            Rule("warn", Title, FieldRuleEffect.Warn, Won, "Başlık kısa."));

        Assert.Empty(Create(gate, new JsonObject { ["stage"] = "won", ["title"] = "Büyük iş", ["score"] = 5 }));
        Assert.Empty(Create(gate, new JsonObject { ["stage"] = "new" })); // koşullar sağlanmıyor
        Assert.Empty(Update(gate, new JsonObject { ["stage"] = "new" }, new JsonObject { ["stage"] = "negotiation" }));
    }

    [Fact]
    public void Required_validate_block_and_approval_reject_the_write_and_name_the_field()
    {
        var (gate, _) = Create(
            Rule("title-required", Title, FieldRuleEffect.Required, Won),
            Rule("score-invalid", Score, FieldRuleEffect.Validate, Won, "Puan girilmeli."),
            Rule("stage-blocked", Stage, FieldRuleEffect.Block, Won, "Kazanıldı aşamasına geçilemez."),
            Rule("amount-approval", Amount, FieldRuleEffect.RequireApproval, Won, "Üst onay gerekir."));

        var errors = Create(gate, new JsonObject { ["stage"] = "won" });

        Assert.Contains(errors, e => e.Field == "title");
        Assert.Contains(errors, e => e.Field == "score" && e.Message == "Puan girilmeli.");
        Assert.Contains(errors, e => e.Field == "stage" && e.Message == "Kazanıldı aşamasına geçilemez.");
        Assert.Contains(errors, e => e.Field == "amount" && e.Message.Contains("Üst onay gerekir."));
    }

    [Fact]
    public void A_required_field_that_the_rules_hide_is_not_demanded_and_warnings_never_block()
    {
        var (gate, _) = Create(
            Rule("title-required", Title, FieldRuleEffect.Required, Won),
            Rule("title-hidden", Title, FieldRuleEffect.Hide, Won),
            Rule("warn", Score, FieldRuleEffect.Warn, Won, "Puan düşük."));

        Assert.Empty(Create(gate, new JsonObject { ["stage"] = "won" }));
    }

    [Fact]
    public void Read_only_is_judged_on_the_current_record_so_it_cannot_be_unlocked_in_the_same_request()
    {
        var (gate, _) = Create(Rule("title-locked", Title, FieldRuleEffect.ReadOnly, Won));
        var before = new JsonObject { ["stage"] = "won", ["title"] = "A" };

        Assert.Contains(Update(gate, before, new JsonObject { ["stage"] = "won", ["title"] = "B" }), e => e.Field == "title");
        Assert.Contains(Update(gate, before, new JsonObject { ["stage"] = "negotiation", ["title"] = "B" }), e => e.Field == "title"); // koşulu bozup aynı istekte değiştirme
        Assert.Empty(Update(gate, before, new JsonObject { ["stage"] = "won", ["title"] = "A", ["score"] = 3 })); // değişmeyen alan serbest
        Assert.Empty(Update(gate, new JsonObject { ["stage"] = "new", ["title"] = "A" }, new JsonObject { ["stage"] = "new", ["title"] = "B" }));
        Assert.Empty(Create(gate, new JsonObject { ["stage"] = "won", ["title"] = "B" })); // oluşturmada değiştirilecek bir şey yok
    }

    [Fact]
    public void Entities_without_rule_fields_never_reach_the_rule_service()
    {
        var (gate, rules) = Create(Rule("title-required", Title, FieldRuleEffect.Required, Won));

        var errors = gate.CheckAsync("test.note", null, null, new JsonObject(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(errors);
        Assert.Equal(0, rules.Reads);
    }

    [Fact]
    public void Rules_are_read_once_per_scope_and_each_tenant_gets_only_its_own_rules()
    {
        var (gateA, rulesA) = Create(Rule("title-required", Title, FieldRuleEffect.Required, Won));
        var (gateB, _) = Create();
        var values = new JsonObject { ["stage"] = "won" };

        Assert.NotEmpty(Create(gateA, values));
        Assert.NotEmpty(Create(gateA, values));
        Assert.Equal(1, rulesA.Reads);

        // B firmasının etkin kuralı yok: aynı kayıt orada geçer.
        Assert.Empty(Create(gateB, values));
    }

    private sealed class FixedRules(IReadOnlyList<FieldRule> rules) : ITenantFieldRules
    {
        public int Reads { get; private set; }

        public Task<RuleMergeResult> GetEffectiveAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new RuleMergeResult(rules, [], []));
        }

        public Task<TenantRuleAdaptationView> GetAdaptationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantRuleAdaptationView> SaveAdaptationAsync(
            IReadOnlyList<FieldRule> rules, IReadOnlyList<string> disabledRuleKeys, long? expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock : ITenantClock
    {
        public Task<TenantDay> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Day(2026, 11, 4));
    }
}
