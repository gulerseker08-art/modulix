using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Tests.Communication;

/// <summary>ADR-0025 dilim 1: adres normalizasyonu, karar sırası (engel izni ezer) ve izin değişikliği kuralları.</summary>
public class CommunicationRulesTests
{
    [Theory]
    [InlineData("  Ayse.Demir@Example.COM ", "ayse.demir@example.com")]
    [InlineData("a+etiket@example.com", "a+etiket@example.com")]
    [InlineData("a.b@example.com", "a.b@example.com")]
    public void Email_is_trimmed_and_lowercased_but_local_part_is_never_merged(string raw, string expected) =>
        Assert.Equal(expected, CommunicationAddress.Normalize(CommunicationChannel.Email, raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ayse")]
    [InlineData("@example.com")]
    [InlineData("ayse@")]
    [InlineData("a@b@example.com")]
    [InlineData("ayse@example")]
    [InlineData("ayse@example..com")]
    [InlineData("ayse demir@example.com")]
    public void Invalid_email_is_rejected(string raw) =>
        Assert.Null(CommunicationAddress.Normalize(CommunicationChannel.Email, raw));

    [Theory]
    [InlineData("0532 123 45 67", "905321234567")]
    [InlineData("532 123 45 67", "905321234567")]
    [InlineData("+90 (532) 123-45-67", "905321234567")]
    [InlineData("0090 532 123 45 67", "905321234567")]
    [InlineData("+44 20 7946 0958", "442079460958")]
    public void Phone_is_reduced_to_international_digits_with_turkey_as_default(string raw, string expected)
    {
        foreach (var channel in new[] { CommunicationChannel.Phone, CommunicationChannel.Sms, CommunicationChannel.WhatsApp })
        {
            Assert.Equal(expected, CommunicationAddress.Normalize(channel, raw));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("+1234567890123456")]
    public void Invalid_phone_is_rejected(string raw) =>
        Assert.Null(CommunicationAddress.Normalize(CommunicationChannel.Phone, raw));

    [Fact]
    public void Mask_hides_most_of_the_address()
    {
        Assert.Equal("a***@example.com", CommunicationAddress.Mask(CommunicationChannel.Email, "ayse@example.com"));
        Assert.Equal("***4567", CommunicationAddress.Mask(CommunicationChannel.Phone, "905321234567"));
    }

    [Theory]
    [InlineData(ConsentStatus.Unknown, CommunicationOutcome.Unknown, "unknown")]
    [InlineData(ConsentStatus.Granted, CommunicationOutcome.Allowed, "granted")]
    [InlineData(ConsentStatus.Denied, CommunicationOutcome.Denied, "denied")]
    public void Marketing_needs_explicit_consent_and_unknown_is_not_allowed(ConsentStatus status, CommunicationOutcome outcome, string code)
    {
        var decision = CommunicationRules.Decide(CommunicationPurpose.Marketing, status, []);
        Assert.Equal((outcome, code), (decision.Outcome, decision.Code));
        Assert.Equal(status == ConsentStatus.Granted, decision.IsAllowed);
    }

    [Theory]
    [InlineData(CommunicationPurpose.Transactional)]
    [InlineData(CommunicationPurpose.Service)]
    public void Transactional_and_service_messages_do_not_need_marketing_consent(CommunicationPurpose purpose)
    {
        Assert.True(CommunicationRules.Decide(purpose, ConsentStatus.Denied, []).IsAllowed);
        Assert.True(CommunicationRules.Decide(purpose, ConsentStatus.Unknown, []).IsAllowed);
    }

    [Theory]
    [InlineData(SuppressionReason.OptOut)]
    [InlineData(SuppressionReason.Complaint)]
    [InlineData(SuppressionReason.Manual)]
    public void Suppression_overrides_a_granted_consent_for_marketing_but_not_for_transactional(SuppressionReason reason)
    {
        var marketing = CommunicationRules.Decide(CommunicationPurpose.Marketing, ConsentStatus.Granted, [reason]);
        Assert.Equal(CommunicationOutcome.Denied, marketing.Outcome);
        Assert.Equal(CommunicationCodes.Suppressed(reason), marketing.Code);

        Assert.True(CommunicationRules.Decide(CommunicationPurpose.Transactional, ConsentStatus.Granted, [reason]).IsAllowed);
    }

    [Theory]
    [InlineData(SuppressionReason.HardBounce)]
    [InlineData(SuppressionReason.InvalidAddress)]
    public void Undeliverable_addresses_are_blocked_for_every_purpose(SuppressionReason reason)
    {
        foreach (var purpose in Enum.GetValues<CommunicationPurpose>())
        {
            Assert.Equal(CommunicationOutcome.Denied, CommunicationRules.Decide(purpose, ConsentStatus.Granted, [reason]).Outcome);
        }
    }

    [Fact]
    public void Undeliverable_reason_is_reported_before_an_opt_out()
    {
        var decision = CommunicationRules.Decide(CommunicationPurpose.Marketing, ConsentStatus.Granted, [SuppressionReason.OptOut, SuppressionReason.HardBounce]);
        Assert.Equal("suppressed:hard-bounce", decision.Code);
    }

    [Fact]
    public void Granting_requires_evidence_or_a_reason()
    {
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Unknown, ConsentStatus.Granted, ConsentSources.Manual, null, null));
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Unknown, ConsentStatus.Granted, ConsentSources.Manual, "  ", " "));
        Assert.Null(CommunicationRules.ValidateChange(ConsentStatus.Unknown, ConsentStatus.Granted, ConsentSources.Manual, "form-v3", null));
        Assert.Null(CommunicationRules.ValidateChange(ConsentStatus.Unknown, ConsentStatus.Granted, ConsentSources.Manual, null, "Telefonda sözlü onay"));
    }

    [Fact]
    public void Status_unknown_cannot_be_recorded_and_denying_needs_no_evidence()
    {
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Granted, ConsentStatus.Unknown, ConsentSources.Manual, "x", null));
        Assert.Null(CommunicationRules.ValidateChange(ConsentStatus.Granted, ConsentStatus.Denied, ConsentSources.Import, null, null));
    }

    [Theory]
    [InlineData(ConsentSources.Import)]
    [InlineData(ConsentSources.Provider)]
    [InlineData(ConsentSources.System)]
    [InlineData(ConsentSources.Unsubscribe)]
    public void A_denial_cannot_be_overridden_by_bulk_or_automatic_sources(string source) =>
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Denied, ConsentStatus.Granted, source, "kanıt-1", "neden"));

    [Theory]
    [InlineData(ConsentSources.Manual)]
    [InlineData(ConsentSources.Form)]
    [InlineData(ConsentSources.PreferenceCenter)]
    public void A_denial_can_be_reopened_only_by_the_person_or_staff_and_with_evidence(string source)
    {
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Denied, ConsentStatus.Granted, source, null, "yalnızca gerekçe"));
        Assert.Null(CommunicationRules.ValidateChange(ConsentStatus.Denied, ConsentStatus.Granted, source, "form-v3", null));
    }

    [Fact]
    public void Unknown_source_is_rejected() =>
        Assert.NotNull(CommunicationRules.ValidateChange(ConsentStatus.Unknown, ConsentStatus.Denied, "bilinmeyen", null, null));
}
