using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Modules.Marketing.Audiences;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 4: alıcı seçimi, tekilleştirme, sınır ve uygunluk sayıları (saf, veritabansız).</summary>
public class AudienceRulesTests
{
    private static readonly Guid T1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid T2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid T3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = Guid.NewGuid();
    private static readonly Guid P2 = Guid.NewGuid();

    private static ResolvedContact Contact(Guid target, Guid? person, string address, bool primary = false, bool shared = false) =>
        new(target, person, Guid.NewGuid(), address, shared, primary);

    private static AudienceSelection Select(IReadOnlyList<Guid> targets, RecipientResolution resolution, int? max = null) =>
        AudienceRules.Select(targets, resolution, CommunicationChannel.Email, max);

    private static IReadOnlyDictionary<string, CommunicationDecision> Allow(params string[] addresses) =>
        addresses.ToDictionary(a => a, _ => new CommunicationDecision(CommunicationOutcome.Allowed, CommunicationCodes.Granted, "ok"));

    [Fact]
    public void Two_people_in_one_company_are_two_recipients_for_one_target()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "a@x.test", primary: true), Contact(T1, P2, "b@x.test")], []);

        var result = AudienceRules.Finish(Select([T1], resolution), Allow("a@x.test", "b@x.test"));

        Assert.Equal((1, 1, 2, 2, 2, 0), (result.Summary.Targets, result.Summary.ResolvedTargets, result.Summary.Persons, result.Summary.UniqueAddresses, result.Summary.Eligible, result.Summary.Excluded));
    }

    [Fact]
    public void The_same_person_is_selected_once_across_targets_and_the_second_is_a_duplicate()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "a@x.test", true), Contact(T2, P1, "a2@y.test", true)], []);

        var result = AudienceRules.Finish(Select([T1, T2], resolution), Allow("a@x.test", "a2@y.test"));

        Assert.Equal(1, result.Summary.Eligible);
        Assert.Equal(1, result.Summary.Exclusions[RecipientCodes.DuplicatePerson]);
        Assert.Equal(1, result.Summary.Persons);
    }

    [Fact]
    public void One_person_with_two_addresses_gets_only_one_message()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "is@x.test", true), Contact(T1, P1, "ev@x.test")], []);

        var result = AudienceRules.Finish(Select([T1], resolution), Allow("is@x.test", "ev@x.test"));

        Assert.Equal(1, result.Summary.Eligible);
        Assert.Equal(1, result.Summary.Exclusions[RecipientCodes.DuplicatePerson]);
    }

    [Fact]
    public void The_same_address_for_different_people_is_deduplicated_ignoring_case_and_spaces()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "ortak@x.test", true), Contact(T2, P2, "  ORTAK@x.test ", true)], []);

        var result = AudienceRules.Finish(Select([T1, T2], resolution), Allow("ortak@x.test"));

        Assert.Equal(1, result.Summary.Eligible);
        Assert.Equal(1, result.Summary.Exclusions[RecipientCodes.DuplicateAddress]);
        Assert.Equal(1, result.Summary.UniqueAddresses);
    }

    [Fact]
    public void Shared_and_invalid_addresses_are_never_candidates_and_never_asked_about_consent()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "santral@x.test", true, shared: true), Contact(T2, P2, "gecersiz", true)], []);

        var selection = Select([T1, T2], resolution);
        var result = AudienceRules.Finish(selection, new Dictionary<string, CommunicationDecision>());

        Assert.Empty(selection.Candidates);
        Assert.Equal(0, result.Summary.Eligible);
        Assert.Equal(1, result.Summary.Exclusions[RecipientCodes.SharedAddress]);
        Assert.Equal(1, result.Summary.Exclusions[CommunicationCodes.InvalidAddress]);
    }

    [Fact]
    public void Per_target_limit_keeps_the_primary_and_drops_the_rest()
    {
        var resolution = new RecipientResolution(
            [Contact(T1, P2, "ikinci@x.test"), Contact(T1, P1, "birinci@x.test", primary: true), Contact(T1, Guid.NewGuid(), "ucuncu@x.test")], []);

        var selection = Select([T1], resolution, max: 1);

        Assert.Equal("birinci@x.test", Assert.Single(selection.Candidates).Address);
        Assert.Equal(2, selection.Others.Count(o => o.Code == RecipientCodes.PerTargetLimit));
    }

    [Fact]
    public void Targets_without_recipients_are_counted_by_reason_and_never_disappear_silently()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "a@x.test", true)], [new UnresolvedTarget(T2, RecipientCodes.NoMatchingContact)]);

        // T3'ü çözümleyici hiç anmadı: yine de kaybolmaz.
        var result = AudienceRules.Finish(Select([T1, T2, T3], resolution), Allow("a@x.test"));

        Assert.Equal((3, 1), (result.Summary.Targets, result.Summary.ResolvedTargets));
        Assert.Equal(1, result.Summary.Unresolved[RecipientCodes.NoMatchingContact]);
        Assert.Equal(1, result.Summary.Unresolved[RecipientCodes.NoContactPoint]);
        Assert.Equal(2, result.Rows.Count(r => r.Status == AudienceStatuses.Unresolved));
    }

    [Fact]
    public void Consent_decides_eligibility_and_unknown_or_denied_or_suppressed_are_excluded_with_their_codes()
    {
        var resolution = new RecipientResolution(
            [Contact(T1, P1, "izinli@x.test", true), Contact(T2, P2, "ret@x.test", true), Contact(T3, Guid.NewGuid(), "yok@x.test", true)], []);
        var decisions = new Dictionary<string, CommunicationDecision>
        {
            ["izinli@x.test"] = new(CommunicationOutcome.Allowed, CommunicationCodes.Granted, "ok"),
            ["ret@x.test"] = new(CommunicationOutcome.Denied, CommunicationCodes.Suppressed(SuppressionReason.OptOut), "engelli"),
            ["yok@x.test"] = new(CommunicationOutcome.Unknown, CommunicationCodes.Unknown, "bilinmiyor"),
        };

        var result = AudienceRules.Finish(Select([T1, T2, T3], resolution), decisions);

        Assert.Equal((1, 2), (result.Summary.Eligible, result.Summary.Excluded));
        Assert.Equal(1, result.Summary.Exclusions["suppressed:opt-out"]);
        Assert.Equal(1, result.Summary.Exclusions[CommunicationCodes.Unknown]);
        Assert.Equal(3, result.Summary.UniqueAddresses);
    }

    [Fact]
    public void A_candidate_without_a_decision_is_treated_as_unknown_never_as_allowed()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "a@x.test", true)], []);

        var result = AudienceRules.Finish(Select([T1], resolution), new Dictionary<string, CommunicationDecision>());

        Assert.Equal(0, result.Summary.Eligible);
        Assert.Equal(1, result.Summary.Exclusions[CommunicationCodes.Unknown]);
    }

    [Fact]
    public void Phone_addresses_are_normalized_before_deduplication()
    {
        var resolution = new RecipientResolution([Contact(T1, P1, "0532 123 45 67", true), Contact(T2, P2, "+90 532 123 45 67", true)], []);

        var selection = AudienceRules.Select([T1, T2], resolution, CommunicationChannel.Phone, null);

        Assert.Equal("905321234567", Assert.Single(selection.Candidates).Address);
        Assert.Equal(1, selection.Others.Count(o => o.Code == RecipientCodes.DuplicateAddress));
    }
}
