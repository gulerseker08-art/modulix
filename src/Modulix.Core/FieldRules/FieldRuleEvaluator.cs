using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.FieldRules;

/// <summary>Bir alanın kurallardan çıkan durumu. İleti listeleri kural sırasındadır.</summary>
public sealed record FieldState(
    string Field,
    bool Visible,
    bool ReadOnly,
    bool Required,
    string? SuggestedDefault,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Approvals,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> AppliedRules)
{
    public static FieldState Default(string field) => new(field, true, false, false, null, [], [], [], [], []);
}

/// <summary>Bir kaydın alan durumları. Kuralı olmayan alan <see cref="FieldState.Default"/> sayılır.</summary>
public sealed record FieldRuleResult(IReadOnlyDictionary<string, FieldState> Fields)
{
    public FieldState Of(string field) => Fields.TryGetValue(field, out var state) ? state : FieldState.Default(field);

    /// <summary>Kaydı reddeden hata ya da engel var mı?</summary>
    public bool IsRejected => Fields.Values.Any(f => f.Errors.Count > 0 || f.Blocks.Count > 0);

    public bool NeedsApproval => Fields.Values.Any(f => f.Approvals.Count > 0);
}

/// <summary>
/// Alan kurallarını bir kaydın değerlerine karşı değerlendirir (ADR-0029 K7). Koşul değerlendirmesi ADR-0027'nin
/// <see cref="ConditionEvaluator"/>'ıdır: ikinci bir koşul dili yoktur. Yan etkisizdir ve değer yazmaz (gizli alanın değeri silinmez).
/// Arayüz, API ve içe aktarma aynı çağrıyı kullanır; sunucu yetkisi görünürlükten bağımsızdır.
/// <list type="bullet">
///   <item>Gizli: bir Hide kuralı sağlanıyorsa, ya da alanın Show kuralları var ve hiçbiri sağlanmıyorsa.</item>
///   <item>Gizli alan zorunlu sayılmaz ve doğrulama hatası üretmez; uyarı, onay ve engel yine geçerlidir.</item>
///   <item>Öneri değerinde, sağlanan son kural kazanır (birleşim sırası belirleyicidir).</item>
/// </list>
/// </summary>
public static class FieldRuleEvaluator
{
    public static async Task<FieldRuleResult> EvaluateAsync(
        IReadOnlyList<FieldRule> rules,
        AutomationFieldSet fields,
        IAutomationFacts facts,
        TenantDay day,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var passed = new List<(FieldRule Rule, bool Passed)>(rules.Count);
        foreach (var rule in rules)
        {
            var explanation = await ConditionEvaluator.EvaluateAsync(rule.Condition, fields, facts, day, cancellationToken);
            passed.Add((rule, explanation.Passed));
        }

        var states = new Dictionary<string, FieldState>(StringComparer.Ordinal);
        foreach (var group in passed.GroupBy(p => p.Rule.Field, StringComparer.Ordinal))
        {
            var hasShow = group.Any(p => p.Rule.Effect == FieldRuleEffect.Show);
            var shown = group.Any(p => p.Rule.Effect == FieldRuleEffect.Show && p.Passed);
            var hidden = group.Any(p => p.Rule.Effect == FieldRuleEffect.Hide && p.Passed) || (hasShow && !shown);

            string[] Messages(FieldRuleEffect effect) =>
                [.. group.Where(p => p.Passed && p.Rule.Effect == effect).Select(p => p.Rule.Message!)];

            states[group.Key] = new FieldState(
                group.Key,
                Visible: !hidden,
                ReadOnly: group.Any(p => p.Passed && p.Rule.Effect == FieldRuleEffect.ReadOnly),
                Required: !hidden && group.Any(p => p.Passed && p.Rule.Effect == FieldRuleEffect.Required),
                SuggestedDefault: group.LastOrDefault(p => p.Passed && p.Rule.Effect == FieldRuleEffect.SuggestDefault).Rule?.Value,
                Errors: hidden ? [] : Messages(FieldRuleEffect.Validate),
                Warnings: Messages(FieldRuleEffect.Warn),
                Approvals: Messages(FieldRuleEffect.RequireApproval),
                Blocks: Messages(FieldRuleEffect.Block),
                AppliedRules: [.. group.Where(p => p.Passed).Select(p => p.Rule.Key)]);
        }

        return new FieldRuleResult(states);
    }
}
