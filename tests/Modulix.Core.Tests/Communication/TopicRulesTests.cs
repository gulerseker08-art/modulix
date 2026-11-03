using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Tests.Communication;

/// <summary>ADR-0025 dilim 14: iletişim konusu kuralları. Konu yalnızca daraltır; hiçbir zaman izin vermez.</summary>
public class TopicRulesTests
{
    private static readonly CommunicationDecision Granted = new(CommunicationOutcome.Allowed, CommunicationCodes.Granted, "izin var");

    [Theory]
    [InlineData("haber-bulteni")]
    [InlineData("kampanya2026")]
    [InlineData("a")]
    public void Valid_keys_are_accepted(string key) => Assert.Null(TopicRules.ValidateKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Haber")]
    [InlineData("haber bulteni")]
    [InlineData("-haber")]
    [InlineData("haber-")]
    [InlineData("haber--bulteni")]
    [InlineData("haber_bulteni")]
    [InlineData("haber/bulteni")]
    [InlineData("bülten")]
    public void Invalid_keys_are_rejected(string? key) => Assert.NotNull(TopicRules.ValidateKey(key));

    [Fact]
    public void A_key_longer_than_the_limit_is_rejected_and_the_limit_itself_is_fine()
    {
        Assert.Null(TopicRules.ValidateKey(new string('a', CommunicationTopic.KeyMaxLength)));
        Assert.NotNull(TopicRules.ValidateKey(new string('a', CommunicationTopic.KeyMaxLength + 1)));
    }

    [Fact]
    public void Names_must_be_present_and_within_the_limit()
    {
        Assert.Null(TopicRules.ValidateName("Haber bülteni"));
        Assert.NotNull(TopicRules.ValidateName(" "));
        Assert.NotNull(TopicRules.ValidateName(null));
        Assert.NotNull(TopicRules.ValidateName(new string('a', CommunicationTopic.NameMaxLength + 1)));
    }

    [Fact]
    public void A_topic_opt_out_denies_an_allowed_marketing_decision_with_a_stable_code()
    {
        var decision = TopicRules.ApplyTopic(Granted, CommunicationPurpose.Marketing, topicOptedOut: true);

        Assert.Equal((CommunicationOutcome.Denied, CommunicationCodes.TopicOptOut), (decision.Outcome, decision.Code));
        Assert.Equal("topic-opt-out", CommunicationCodes.TopicOptOut);
    }

    [Fact]
    public void Without_an_opt_out_the_decision_is_unchanged()
    {
        Assert.Same(Granted, TopicRules.ApplyTopic(Granted, CommunicationPurpose.Marketing, topicOptedOut: false));
    }

    [Theory]
    [InlineData(CommunicationOutcome.Denied, CommunicationCodes.Denied)]
    [InlineData(CommunicationOutcome.Unknown, CommunicationCodes.Unknown)]
    [InlineData(CommunicationOutcome.Denied, "suppressed:hard-bounce")]
    public void A_decision_that_is_already_not_allowed_keeps_its_original_reason(CommunicationOutcome outcome, string code)
    {
        var original = new CommunicationDecision(outcome, code, "neden");

        Assert.Same(original, TopicRules.ApplyTopic(original, CommunicationPurpose.Marketing, topicOptedOut: true));
    }

    [Theory]
    [InlineData(CommunicationPurpose.Transactional)]
    [InlineData(CommunicationPurpose.Service)]
    public void Topics_never_affect_transactional_or_service_messages(CommunicationPurpose purpose)
    {
        var allowed = new CommunicationDecision(CommunicationOutcome.Allowed, CommunicationCodes.NoConsentRequired, "gerekmiyor");

        Assert.Same(allowed, TopicRules.ApplyTopic(allowed, purpose, topicOptedOut: true));
    }

    [Fact]
    public void A_person_may_return_to_a_topic_they_left_themselves_while_consent_is_granted_and_unblocked()
    {
        Assert.Null(TopicRules.SelfResubscribeBlock(ConsentStatus.Granted, suppressed: false, ConsentSources.PreferenceCenter));
    }

    [Theory]
    [InlineData(ConsentStatus.Denied)]
    [InlineData(ConsentStatus.Unknown)]
    public void Returning_to_a_topic_never_grants_marketing_consent(ConsentStatus consent)
    {
        Assert.NotNull(TopicRules.SelfResubscribeBlock(consent, suppressed: false, ConsentSources.PreferenceCenter));
    }

    [Fact]
    public void An_active_block_stops_a_return_to_a_topic()
    {
        Assert.NotNull(TopicRules.SelfResubscribeBlock(ConsentStatus.Granted, suppressed: true, ConsentSources.PreferenceCenter));
    }

    [Theory]
    [InlineData(ConsentSources.Manual)]
    [InlineData(ConsentSources.Import)]
    [InlineData(ConsentSources.Provider)]
    [InlineData(ConsentSources.System)]
    [InlineData(ConsentSources.Unsubscribe)]
    public void An_opt_out_recorded_by_the_company_cannot_be_reversed_through_the_link(string source)
    {
        Assert.NotNull(TopicRules.SelfResubscribeBlock(ConsentStatus.Granted, suppressed: false, source));
    }
}
