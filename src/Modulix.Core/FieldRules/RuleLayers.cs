namespace Modulix.Core.FieldRules;

/// <summary>
/// Kural katmanı sırası (ADR-0029 K8): ürün standardı → sektör paketi → tenant uyarlaması → kapsam (şirket/şube/tesis; ileride).
/// Kullanıcı görünüm tercihi kural katmanı değildir: kural koyamaz, yalnızca kendi ekran görünümünü etkiler.
/// </summary>
public enum RuleLayerKind
{
    ProductStandard = 0,
    SectorPackage = 1,
    TenantAdaptation = 2,
    Scope = 3,
}

/// <summary>Bir katmanın kuralları ve alttaki katmandan kapattığı kuralların anahtarları.</summary>
public sealed record RuleLayer(RuleLayerKind Kind, IReadOnlyList<FieldRule> Rules, IReadOnlyList<string>? DisabledRuleKeys = null);

/// <summary>Birleşimi yayını engelleyen çakışma ya da engellemeyen uyarı.</summary>
public sealed record RuleMergeIssue(string RuleKey, string Message);

/// <summary>
/// Birleşmiş kural kümesi. <see cref="Conflicts"/> yayını engeller; <see cref="Warnings"/> yalnızca bildirir (ör. paket güncellemesiyle
/// kalkmış bir kuralı kapatan eski tenant ayarı). <see cref="Rules"/> alan ve anahtara göre sıralıdır (belirleyici).
/// </summary>
public sealed record RuleMergeResult(IReadOnlyList<FieldRule> Rules, IReadOnlyList<RuleMergeIssue> Conflicts, IReadOnlyList<RuleMergeIssue> Warnings)
{
    public bool CanPublish => Conflicts.Count == 0;
}

/// <summary>
/// Katmanları belirleyici biçimde birleştirir: aynı anahtarlı üst katman kuralı alttakini geçersiz kılar, <c>DisabledRuleKeys</c> alttaki
/// kuralı kaldırır. Kilitli kural geçersiz kılınamaz ve kapatılamaz: tenant uyarlaması güvenlik kurallarını kaldıramaz; bu bir çakışmadır
/// ve yayını engeller. Paket güncellemesi tenant uyarlamasını ezmez çünkü uyarlama kendi katmanında ayrı tutulur ve her birleşimde yeniden uygulanır.
/// </summary>
public static class RuleLayerMerger
{
    public static RuleMergeResult Merge(IReadOnlyCollection<RuleLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var conflicts = new List<RuleMergeIssue>();
        var warnings = new List<RuleMergeIssue>();
        var merged = new Dictionary<string, FieldRule>(StringComparer.Ordinal);

        foreach (var kindGroup in layers.GroupBy(l => l.Kind).OrderBy(g => g.Key))
        {
            if (kindGroup.Count() > 1)
            {
                conflicts.Add(new RuleMergeIssue(string.Empty, $"Aynı katman birden fazla verildi: {kindGroup.Key}."));
                continue;
            }

            var layer = kindGroup.Single();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in layer.Rules)
            {
                if (!seen.Add(rule.Key))
                {
                    conflicts.Add(new RuleMergeIssue(rule.Key, $"Aynı katmanda kural anahtarı tekrar ediyor: {rule.Key} ({layer.Kind})."));
                    continue;
                }

                if (merged.TryGetValue(rule.Key, out var lower) && lower.Locked)
                {
                    conflicts.Add(new RuleMergeIssue(rule.Key, $"Kilitli kural geçersiz kılınamaz: {rule.Key} ({layer.Kind})."));
                    continue;
                }

                merged[rule.Key] = rule;
            }

            foreach (var key in layer.DisabledRuleKeys ?? [])
            {
                if (!merged.TryGetValue(key, out var target))
                {
                    warnings.Add(new RuleMergeIssue(key, $"Kapatılmak istenen kural bulunamadı: {key} ({layer.Kind})."));
                }
                else if (target.Locked)
                {
                    conflicts.Add(new RuleMergeIssue(key, $"Kilitli kural kapatılamaz: {key} ({layer.Kind})."));
                }
                else
                {
                    merged.Remove(key);
                }
            }
        }

        var rules = merged.Values.OrderBy(r => r.Field, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        conflicts.AddRange(FindContradictions(rules));
        return new RuleMergeResult(rules, conflicts, warnings);
    }

    /// <summary>Koşulsuz (her zaman geçerli) kurallar arasındaki çelişkiler: gizli ama zorunlu alan, farklı öneri değerleri.</summary>
    private static IEnumerable<RuleMergeIssue> FindContradictions(IReadOnlyList<FieldRule> rules)
    {
        foreach (var group in rules.Where(r => r.Condition is null).GroupBy(r => r.Field, StringComparer.Ordinal))
        {
            var hide = group.FirstOrDefault(r => r.Effect == FieldRuleEffect.Hide);
            var required = group.FirstOrDefault(r => r.Effect == FieldRuleEffect.Required);
            if (hide is not null && required is not null)
            {
                yield return new RuleMergeIssue(required.Key, $"Alan her zaman gizli ve her zaman zorunlu olamaz: {group.Key} ({hide.Key} / {required.Key}).");
            }

            var defaults = group.Where(r => r.Effect == FieldRuleEffect.SuggestDefault).ToList();
            if (defaults.Select(r => r.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                yield return new RuleMergeIssue(defaults[0].Key, $"Aynı alana koşulsuz farklı öneri değerleri verilmiş: {group.Key} ({string.Join(" / ", defaults.Select(r => r.Key))}).");
            }
        }
    }
}
