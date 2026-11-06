using Modulix.Core.Contracts.Customs;

namespace Modulix.Core.Tests.Customs;

/// <summary>Rejim profili içeriği ve tescil no kuralı (ADR-0032 K7, K16): doğrulama, değerlendirme ve tenant sıkılaştırması.</summary>
public class RegimeProfileRulesTests
{
    private static RegimeProfileContent Content(RegistrationNoRule? rule = null, string? regime = "7100", int schema = RegimeProfileContent.CurrentSchemaVersion) =>
        new(schema, regime, rule);

    private static RegistrationNoRule Rule(string? pattern = null, int? min = null, int? max = null, string? example = null, RuleEnforcement level = RuleEnforcement.Warn) =>
        new(pattern, min, max, example, level);

    [Fact]
    public void Content_without_any_section_and_a_full_rule_are_valid()
    {
        Assert.Empty(RegimeProfileRules.Validate(Content()));
        Assert.Empty(RegimeProfileRules.Validate(Content(Rule(@"\d{8}[A-Z]{2}\d{8}", 18, 18, "25341300IM00123456"))));
    }

    [Fact]
    public void Invalid_content_is_reported()
    {
        Assert.NotEmpty(RegimeProfileRules.Validate(null));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(schema: 2)));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(regime: "71 00")));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(regime: new string('1', 21))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(min: 0))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(max: RegimeProfileRules.RegistrationNoMaxLength + 1))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(min: 10, max: 5))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(level: (RuleEnforcement)7))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(pattern: "([unclosed"))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(pattern: new string('a', RegimeProfileRules.PatternMaxLength + 1)))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(example: new string('1', RegimeProfileRules.ExampleMaxLength + 1)))));
    }

    [Fact]
    public void Example_must_satisfy_the_rule_it_documents()
    {
        var errors = RegimeProfileRules.Validate(Content(Rule(@"\d{4}", example: "ABCD")));

        Assert.Contains(errors, e => e.Contains("örnek", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(RegimeProfileRules.Validate(Content(Rule(@"\d{4}", example: "1234"))));
        Assert.NotEmpty(RegimeProfileRules.Validate(Content(Rule(min: 5, example: "1234"))));
    }

    [Theory]
    [InlineData("25341300IM00123456", true)]
    [InlineData("25341300im00123456", false)]
    [InlineData("25341300IM0012345", false)]
    [InlineData("25341300IM001234567", false)]
    [InlineData("", false)]
    public void Pattern_must_match_the_whole_number(string number, bool valid)
    {
        var rule = Rule(@"\d{8}[A-Z]{2}\d{8}");

        Assert.Equal(valid, RegimeProfileRules.CheckRegistrationNo(rule, number).Count == 0);
    }

    [Fact]
    public void Alternation_in_the_pattern_is_anchored_as_a_whole()
    {
        // "A|B" yalnızca tam olarak A ya da B ile eşleşir; "XA" kabul edilmez.
        var rule = Rule("A|B");

        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(rule, "A"));
        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(rule, "B"));
        Assert.NotEmpty(RegimeProfileRules.CheckRegistrationNo(rule, "XA"));
    }

    [Fact]
    public void Length_bounds_are_checked_and_messages_name_the_limit()
    {
        var rule = Rule(min: 5, max: 8);

        Assert.Contains("en az 5", Assert.Single(RegimeProfileRules.CheckRegistrationNo(rule, "1234")));
        Assert.Contains("en çok 8", Assert.Single(RegimeProfileRules.CheckRegistrationNo(rule, "123456789")));
        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(rule, "12345"));
        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(rule, " 12345678 "));
    }

    [Fact]
    public void No_rule_means_no_violation_and_violation_message_includes_the_example()
    {
        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(null, "herhangi"));
        Assert.Empty(RegimeProfileRules.CheckRegistrationNo(null, null));
        Assert.Contains("örnek: 12345", Assert.Single(RegimeProfileRules.CheckRegistrationNo(Rule(@"\d{5}", example: "12345"), "abc")));
    }

    [Theory]
    [InlineData(RuleEnforcement.Warn, null, RuleEnforcement.Warn)]
    [InlineData(RuleEnforcement.Warn, RuleEnforcement.Warn, RuleEnforcement.Warn)]
    [InlineData(RuleEnforcement.Warn, RuleEnforcement.Block, RuleEnforcement.Block)]
    [InlineData(RuleEnforcement.Block, null, RuleEnforcement.Block)]
    [InlineData(RuleEnforcement.Block, RuleEnforcement.Warn, RuleEnforcement.Block)]
    public void Tenant_may_tighten_but_never_loosen_the_profile(RuleEnforcement profile, RuleEnforcement? tenant, RuleEnforcement expected) =>
        Assert.Equal(expected, RegimeProfileRules.Tighten(profile, tenant));
}
