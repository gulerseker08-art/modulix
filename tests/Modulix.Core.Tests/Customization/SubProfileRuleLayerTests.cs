using Modulix.Core.Automation;
using Modulix.Core.Customization;
using Modulix.Core.FieldRules;

namespace Modulix.Core.Tests.Customization;

/// <summary>Alt profil kural katmanı (ADR-0041 dilim 2, K4): anahtar üretimi, katman sırası ve birleşim davranışı. Veritabanı gerekmez.</summary>
public class SubProfileRuleLayerTests
{
    private static FieldRule Rule(string key, string field, FieldRuleEffect effect, string? message = null) =>
        new(key, field, effect) { Message = message };

    [Fact]
    public void Layer_order_puts_sub_profile_between_sector_package_and_tenant_adaptation()
    {
        Assert.True(RuleLayerKind.SectorPackage < RuleLayerKind.SubProfile);
        Assert.True(RuleLayerKind.SubProfile < RuleLayerKind.TenantAdaptation);
        Assert.True(RuleLayerKind.TenantAdaptation < RuleLayerKind.Scope);
    }

    [Fact]
    public void Rule_key_is_prefixed_with_the_sub_profile_code_and_valid_for_field_rules()
    {
        var key = SubProfileRules.RuleKey("deniz_tasimaciligi", "Konteyner no zorunlu", new HashSet<string>());

        Assert.Equal("sp.deniz-tasimaciligi.konteyner-no-zorunlu", key);
        Assert.All(key, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'));
    }

    [Fact]
    public void Rule_key_gets_a_counter_when_taken_and_stays_within_the_length_limit()
    {
        var taken = new HashSet<string> { "sp.kara.zorunlu" };
        Assert.Equal("sp.kara.zorunlu-2", SubProfileRules.RuleKey("kara", "Zorunlu", taken));

        var code = new string('k', SubProfile.CodeMaxLength);
        var key = SubProfileRules.RuleKey(code, new string('a', 100), new HashSet<string>());
        Assert.True(key.Length <= FieldRule.KeyMaxLength);
        Assert.True(SubProfileRules.RuleKey(code, new string('a', 100), new HashSet<string> { key }).Length <= FieldRule.KeyMaxLength);
    }

    [Fact]
    public void Rule_input_needs_a_name_and_a_rule()
    {
        var rule = Rule("x", "crm.opportunity.title", FieldRuleEffect.Required);

        Assert.Equal("Ad", SubProfileRules.NormalizeRule(new SubProfileRuleInput("  Ad ", rule)).Name);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeRule(new SubProfileRuleInput("  ", rule))).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeRule(new SubProfileRuleInput(new string('a', SubProfileRule.NameMaxLength + 1), rule))).ParamName);
        Assert.Equal("rule", Assert.Throws<ArgumentException>(() => SubProfileRules.NormalizeRule(new SubProfileRuleInput("Ad", null!))).ParamName);
    }

    [Fact]
    public void Stored_rule_keeps_its_key_and_is_never_locked()
    {
        var entity = new SubProfileRule(Guid.NewGuid(), "Kural", Rule("sp.kara.kural", "crm.opportunity.title", FieldRuleEffect.Required) with { Locked = true }, 0);
        Assert.False(entity.ToRule("kara").Locked);

        entity.Replace("Yeni ad", Rule("baska-anahtar", "crm.opportunity.title", FieldRuleEffect.Hide));

        var rule = entity.ToRule("kara");
        Assert.Equal("sp.kara.kural", rule.Key);
        Assert.Equal("sp.kara.kural", entity.Key);
        Assert.Equal(FieldRuleEffect.Hide, rule.Effect);
        Assert.Equal("Yeni ad", entity.Name);
    }

    [Fact]
    public void Corrupt_stored_rule_names_the_sub_profile()
    {
        var entity = new SubProfileRule(Guid.NewGuid(), "Kural", Rule("sp.kara.kural", "crm.opportunity.title", FieldRuleEffect.Required), 0);
        typeof(SubProfileRule).GetProperty(nameof(SubProfileRule.RuleJson))!.SetValue(entity, "{bozuk");

        var failure = Assert.Throws<CorruptRulePackageException>(() => entity.ToRule("kara"));
        Assert.Equal("alt-profil:kara", failure.PackageCode);
    }

    [Fact]
    public void Tenant_adaptation_can_disable_or_override_a_sub_profile_rule()
    {
        var subProfile = new RuleLayer(RuleLayerKind.SubProfile, [Rule("sp.kara.zorunlu", "crm.opportunity.title", FieldRuleEffect.Required)]);

        var disabled = RuleLayerMerger.Merge([subProfile, new RuleLayer(RuleLayerKind.TenantAdaptation, [], ["sp.kara.zorunlu"])]);
        Assert.Empty(disabled.Rules);
        Assert.True(disabled.CanPublish);

        var overridden = RuleLayerMerger.Merge(
            [subProfile, new RuleLayer(RuleLayerKind.TenantAdaptation, [Rule("sp.kara.zorunlu", "crm.opportunity.title", FieldRuleEffect.Hide)])]);
        Assert.Equal(FieldRuleEffect.Hide, Assert.Single(overridden.Rules).Effect);
    }

    [Fact]
    public void Sub_profile_rule_cannot_replace_or_disable_a_locked_sector_rule_with_the_same_key()
    {
        var sector = new RuleLayer(RuleLayerKind.SectorPackage, [Rule("guvenlik", "crm.opportunity.title", FieldRuleEffect.Required) with { Locked = true }]);

        var overridden = RuleLayerMerger.Merge([sector, new RuleLayer(RuleLayerKind.SubProfile, [Rule("guvenlik", "crm.opportunity.title", FieldRuleEffect.Hide)])]);
        var disabled = RuleLayerMerger.Merge([sector, new RuleLayer(RuleLayerKind.SubProfile, [], ["guvenlik"])]);

        Assert.False(overridden.CanPublish);
        Assert.False(disabled.CanPublish);
    }

    [Fact]
    public void Two_sub_profiles_with_contradicting_unconditional_rules_conflict()
    {
        var merged = RuleLayerMerger.Merge(
        [
            new RuleLayer(RuleLayerKind.SubProfile,
            [
                Rule("sp.kara.gizli", "crm.opportunity.title", FieldRuleEffect.Hide),
                Rule("sp.deniz.zorunlu", "crm.opportunity.title", FieldRuleEffect.Required),
            ]),
        ]);

        var conflict = Assert.Single(merged.Conflicts);
        Assert.Contains("gizli", conflict.Message, StringComparison.Ordinal);
        Assert.False(merged.CanPublish);
    }

    [Fact]
    public void Rules_of_different_sub_profiles_do_not_clash_by_key()
    {
        var merged = RuleLayerMerger.Merge(
        [
            new RuleLayer(RuleLayerKind.SubProfile,
            [
                Rule("sp.kara.uyari", "crm.opportunity.title", FieldRuleEffect.Warn, "Kara"),
                Rule("sp.deniz.uyari", "crm.opportunity.title", FieldRuleEffect.Warn, "Deniz"),
            ]),
        ]);

        Assert.True(merged.CanPublish);
        Assert.Equal(2, merged.Rules.Count);
    }
}
