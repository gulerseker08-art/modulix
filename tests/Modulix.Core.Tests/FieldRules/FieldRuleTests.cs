using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.FieldRules;
using Modulix.Core.Tests.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.FieldRules;

/// <summary>ADR-0029 K7/K8: alan kuralı doğrulaması, değerlendirmesi ve katman birleşimi. Core sektöre özel bir şey bilmez.</summary>
public class FieldRuleTests
{
    private static readonly TenantDayHolder Today = new();

    private static FieldRuleResult Evaluate(JsonObject values, params FieldRule[] rules)
    {
        var fields = Fields(SecretPermission);
        return FieldRuleEvaluator.EvaluateAsync(rules, fields, Facts(fields, values), Today.Day, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static FieldRule Rule(string key, AutomationFieldDefinition field, FieldRuleEffect effect, ConditionNode? condition = null, string? message = null, string? value = null) =>
        new(key, field.Key, effect, condition) { Message = message, Value = value };

    private static ConditionLeaf StageIs(string stage) => Leaf(Stage, AutomationOperator.Eq, stage);

    private static ConditionLeaf Vip => Leaf(IsVip, AutomationOperator.Eq, "true");

    [Fact]
    public void Hide_applies_when_condition_holds_and_leaves_values_alone()
    {
        var values = new JsonObject { ["stage"] = "won", ["score"] = 7 };
        var result = Evaluate(values, Rule("score-hidden", Score, FieldRuleEffect.Hide, StageIs("won")));

        Assert.False(result.Of(Score.Key).Visible);
        Assert.Equal(7, (int)values["score"]!);
        Assert.True(Evaluate(new JsonObject { ["stage"] = "new" }, Rule("score-hidden", Score, FieldRuleEffect.Hide, StageIs("won"))).Of(Score.Key).Visible);
        Assert.True(result.Of(Title.Key).Visible); // kuralı olmayan alan varsayılan durumdadır
    }

    [Fact]
    public void Show_rules_make_a_field_visible_only_when_one_passes()
    {
        var rule = Rule("score-for-vip", Score, FieldRuleEffect.Show, Vip);

        Assert.True(Evaluate(new JsonObject { ["isVip"] = true }, rule).Of(Score.Key).Visible);
        Assert.False(Evaluate(new JsonObject { ["isVip"] = false }, rule).Of(Score.Key).Visible);

        // Hide, Show'dan baskındır.
        var both = Evaluate(new JsonObject { ["isVip"] = true, ["stage"] = "won" }, rule, Rule("score-hidden", Score, FieldRuleEffect.Hide, StageIs("won")));
        Assert.False(both.Of(Score.Key).Visible);
    }

    [Fact]
    public void Required_is_conditional_and_a_hidden_field_is_not_required()
    {
        var required = Rule("title-required", Title, FieldRuleEffect.Required, StageIs("won"));

        Assert.True(Evaluate(new JsonObject { ["stage"] = "won" }, required).Of(Title.Key).Required);
        Assert.False(Evaluate(new JsonObject { ["stage"] = "new" }, required).Of(Title.Key).Required);

        var hidden = Evaluate(new JsonObject { ["stage"] = "won" }, required, Rule("title-hidden", Title, FieldRuleEffect.Hide, StageIs("won")));
        Assert.False(hidden.Of(Title.Key).Required);
    }

    [Fact]
    public void Validate_warn_approval_and_block_report_their_messages()
    {
        var values = new JsonObject { ["stage"] = "won" };
        var result = Evaluate(
            values,
            Rule("v", Title, FieldRuleEffect.Validate, StageIs("won"), "Başlık girin."),
            Rule("w", Score, FieldRuleEffect.Warn, StageIs("won"), "Puan düşük olabilir."),
            Rule("a", Amount, FieldRuleEffect.RequireApproval, StageIs("won"), "Yönetici onayı gerekir."),
            Rule("b", DueDate, FieldRuleEffect.Block, StageIs("lost"), "Kapanış engellendi."));

        Assert.Equal(["Başlık girin."], result.Of(Title.Key).Errors);
        Assert.Equal(["Puan düşük olabilir."], result.Of(Score.Key).Warnings);
        Assert.Equal(["Yönetici onayı gerekir."], result.Of(Amount.Key).Approvals);
        Assert.Empty(result.Of(DueDate.Key).Blocks); // koşul sağlanmadı
        Assert.True(result.IsRejected);
        Assert.True(result.NeedsApproval);

        var clean = Evaluate(new JsonObject { ["stage"] = "new" }, Rule("w", Score, FieldRuleEffect.Warn, StageIs("won"), "Puan düşük olabilir."));
        Assert.False(clean.IsRejected);
        Assert.False(clean.NeedsApproval);
    }

    [Fact]
    public void Validation_error_is_not_raised_for_a_hidden_field()
    {
        var result = Evaluate(
            new JsonObject { ["stage"] = "won" },
            Rule("v", Title, FieldRuleEffect.Validate, StageIs("won"), "Başlık girin."),
            Rule("h", Title, FieldRuleEffect.Hide, StageIs("won")));

        Assert.Empty(result.Of(Title.Key).Errors);
        Assert.False(result.IsRejected);
    }

    [Fact]
    public void Suggested_default_comes_from_the_last_passing_rule()
    {
        var result = Evaluate(
            new JsonObject { ["stage"] = "negotiation" },
            Rule("d1", Title, FieldRuleEffect.SuggestDefault, value: "Genel"),
            Rule("d2", Title, FieldRuleEffect.SuggestDefault, StageIs("negotiation"), value: "Pazarlık"),
            Rule("d3", Title, FieldRuleEffect.SuggestDefault, StageIs("won"), value: "Kazanç"));

        Assert.Equal("Pazarlık", result.Of(Title.Key).SuggestedDefault);
        Assert.Equal(["d1", "d2"], result.Of(Title.Key).AppliedRules);
    }

    [Fact]
    public void Rule_definition_is_validated_before_publishing()
    {
        var fields = Fields();

        Assert.Empty(FieldRuleValidator.Validate(Rule("title-required", Title, FieldRuleEffect.Required, StageIs("won")), fields));

        Assert.Contains(FieldRuleValidator.Validate(new FieldRule("Bozuk Anahtar", Title.Key, FieldRuleEffect.Hide), fields), i => i.Path == "key");
        Assert.Contains(FieldRuleValidator.Validate(new FieldRule("k", "sales.deal.yok", FieldRuleEffect.Hide), fields), i => i.Path == "field");
        Assert.Contains(FieldRuleValidator.Validate(Rule("k", Title, FieldRuleEffect.Validate), fields), i => i.Path == "message");
        Assert.Contains(FieldRuleValidator.Validate(Rule("k", Title, FieldRuleEffect.Warn, message: new string('x', 201)), fields), i => i.Path == "message");
        Assert.Contains(FieldRuleValidator.Validate(Rule("k", Title, FieldRuleEffect.SuggestDefault), fields), i => i.Path == "value");
        Assert.Contains(FieldRuleValidator.Validate(Rule("k", Title, FieldRuleEffect.Hide, value: "x"), fields), i => i.Path == "value");
        Assert.Contains(FieldRuleValidator.Validate(Rule("k", Title, FieldRuleEffect.Hide, new ConditionLeaf("sales.deal.yok", AutomationOperator.Eq, ["x"])), fields),
            i => i.Path.StartsWith("condition", StringComparison.Ordinal));
        Assert.Contains(FieldRuleValidator.Validate(new FieldRule("k", Title.Key, (FieldRuleEffect)99), fields), i => i.Path == "effect");
    }

    [Fact]
    public void Upper_layer_overrides_and_disables_lower_rules_deterministically()
    {
        var product = new RuleLayer(RuleLayerKind.ProductStandard,
        [
            Rule("title-required", Title, FieldRuleEffect.Required),
            Rule("score-hidden", Score, FieldRuleEffect.Hide),
        ]);
        var sector = new RuleLayer(RuleLayerKind.SectorPackage, [Rule("score-hidden", Score, FieldRuleEffect.Show, Vip)]);
        var tenant = new RuleLayer(RuleLayerKind.TenantAdaptation, [], ["title-required"]);

        var result = RuleLayerMerger.Merge([tenant, product, sector]); // verilen sıra önemsiz, katman türü belirler

        Assert.True(result.CanPublish);
        var rule = Assert.Single(result.Rules);
        Assert.Equal(FieldRuleEffect.Show, rule.Effect);
        Assert.Equal("score-hidden", rule.Key);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Tenant_adaptation_cannot_override_or_disable_a_locked_rule()
    {
        var product = new RuleLayer(RuleLayerKind.ProductStandard,
        [
            new FieldRule("amount-approval", Amount.Key, FieldRuleEffect.RequireApproval, StageIs("won")) { Message = "Onay gerekir.", Locked = true },
        ]);
        var overriding = new RuleLayer(RuleLayerKind.TenantAdaptation, [Rule("amount-approval", Amount, FieldRuleEffect.Warn, StageIs("won"), "Sadece uyarı.")]);
        var disabling = new RuleLayer(RuleLayerKind.TenantAdaptation, [], ["amount-approval"]);

        foreach (var tenant in new[] { overriding, disabling })
        {
            var result = RuleLayerMerger.Merge([product, tenant]);
            Assert.False(result.CanPublish);
            Assert.Contains(result.Conflicts, c => c.RuleKey == "amount-approval");
            Assert.Equal(FieldRuleEffect.RequireApproval, Assert.Single(result.Rules).Effect); // kilitli kural yerinde kalır
        }
    }

    [Fact]
    public void Merge_reports_duplicate_keys_unknown_disables_and_contradictions()
    {
        var duplicate = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.ProductStandard,
            [Rule("same", Title, FieldRuleEffect.Required), Rule("same", Score, FieldRuleEffect.Hide)])]);
        Assert.False(duplicate.CanPublish);

        var staleDisable = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.TenantAdaptation, [], ["artik-yok"])]);
        Assert.True(staleDisable.CanPublish); // paket güncellemesiyle kalkan kural, eski uyarlamayı yayını engelleyen hataya çevirmez
        Assert.Single(staleDisable.Warnings);

        var twice = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.SectorPackage, []), new RuleLayer(RuleLayerKind.SectorPackage, [])]);
        Assert.False(twice.CanPublish);

        var hiddenRequired = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.SectorPackage,
            [Rule("h", Title, FieldRuleEffect.Hide), Rule("r", Title, FieldRuleEffect.Required)])]);
        Assert.False(hiddenRequired.CanPublish);

        var defaults = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.SectorPackage,
            [Rule("d1", Title, FieldRuleEffect.SuggestDefault, value: "A"), Rule("d2", Title, FieldRuleEffect.SuggestDefault, value: "B")])]);
        Assert.False(defaults.CanPublish);

        // Koşullu kurallar çelişki sayılmaz (hangisinin geçerli olacağı kayda göre değişir).
        var conditional = RuleLayerMerger.Merge([new RuleLayer(RuleLayerKind.SectorPackage,
            [Rule("h", Title, FieldRuleEffect.Hide, StageIs("won")), Rule("r", Title, FieldRuleEffect.Required, StageIs("new"))])]);
        Assert.True(conditional.CanPublish);
    }

    [Fact]
    public void Merge_output_is_ordered_by_field_then_key()
    {
        var layer = new RuleLayer(RuleLayerKind.SectorPackage,
        [
            Rule("z-rule", Title, FieldRuleEffect.Required, StageIs("won")),
            Rule("a-rule", Title, FieldRuleEffect.Warn, StageIs("won"), "x"),
            Rule("m-rule", Amount, FieldRuleEffect.ReadOnly, StageIs("won")),
        ]);

        var first = RuleLayerMerger.Merge([layer]).Rules.Select(r => r.Key);
        var second = RuleLayerMerger.Merge([layer with { Rules = [.. layer.Rules.Reverse()] }]).Rules.Select(r => r.Key);

        Assert.Equal(["m-rule", "a-rule", "z-rule"], first); // "sales.deal.amount" < "sales.deal.title"
        Assert.Equal(first, second);
    }

    private sealed class TenantDayHolder
    {
        public Modulix.Core.Contracts.Tenancy.TenantDay Day { get; } = AutomationKit.Day(2026, 11, 4);
    }
}
