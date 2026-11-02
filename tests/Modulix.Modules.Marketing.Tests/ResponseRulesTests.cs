using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Responses;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 7: yanıt durum makinesi, lead'e dönüşebilen türler, kaynak/UTM kuralları ve kişi adı ayrıştırma (saf).</summary>
public class ResponseRulesTests
{
    [Theory]
    [InlineData(ResponseActions.Take, ResponseStatuses.New, ResponseKinds.Interested, true)]
    [InlineData(ResponseActions.Take, ResponseStatuses.InProgress, ResponseKinds.Interested, false)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.New, ResponseKinds.Interested, true)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.InProgress, ResponseKinds.MeetingRequest, true)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.Converted, ResponseKinds.Interested, false)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.New, ResponseKinds.NotInterested, false)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.New, ResponseKinds.OptOut, false)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.New, ResponseKinds.Complaint, false)]
    [InlineData(ResponseActions.CreateActivity, ResponseStatuses.Converted, ResponseKinds.Interested, true)]
    [InlineData(ResponseActions.CreateActivity, ResponseStatuses.Closed, ResponseKinds.Interested, false)]
    [InlineData(ResponseActions.ApplyOptOut, ResponseStatuses.New, ResponseKinds.OptOut, true)]
    [InlineData(ResponseActions.ApplyOptOut, ResponseStatuses.New, ResponseKinds.NotInterested, false)]
    [InlineData(ResponseActions.ApplyOptOut, ResponseStatuses.Closed, ResponseKinds.OptOut, false)]
    [InlineData(ResponseActions.Close, ResponseStatuses.Converted, ResponseKinds.Other, true)]
    [InlineData(ResponseActions.Close, ResponseStatuses.Closed, ResponseKinds.Other, false)]
    [InlineData(ResponseActions.Reopen, ResponseStatuses.Closed, ResponseKinds.Other, true)]
    [InlineData(ResponseActions.Reopen, ResponseStatuses.New, ResponseKinds.Other, false)]
    [InlineData("bilinmeyen", ResponseStatuses.New, ResponseKinds.Other, false)]
    public void Only_listed_transitions_are_allowed(string action, string status, string kind, bool allowed) =>
        Assert.Equal(allowed, ResponseTransitions.Allowed(action, status, kind));

    [Fact]
    public void Every_kind_and_status_is_known_and_only_interest_kinds_become_leads()
    {
        Assert.Equal(
            [ResponseKinds.Interested, ResponseKinds.Question, ResponseKinds.MeetingRequest],
            ResponseKinds.All.Where(ResponseTransitions.CanBecomeLead).ToArray());
        Assert.Equal(4, ResponseStatuses.All.Count);
    }

    [Theory]
    [InlineData(ResponseActions.Take, ResponseStatuses.New, ResponseStatuses.InProgress)]
    [InlineData(ResponseActions.ConvertToLead, ResponseStatuses.InProgress, ResponseStatuses.Converted)]
    [InlineData(ResponseActions.ApplyOptOut, ResponseStatuses.New, ResponseStatuses.Closed)]
    [InlineData(ResponseActions.Close, ResponseStatuses.Converted, ResponseStatuses.Closed)]
    [InlineData(ResponseActions.Reopen, ResponseStatuses.Closed, ResponseStatuses.New)]
    [InlineData(ResponseActions.CreateActivity, ResponseStatuses.New, ResponseStatuses.InProgress)]
    [InlineData(ResponseActions.CreateActivity, ResponseStatuses.Converted, ResponseStatuses.Converted)]
    public void Next_status_follows_the_action(string action, string status, string expected) =>
        Assert.Equal(expected, ResponseTransitions.Next(action, status));

    [Fact]
    public void Closing_and_reopening_need_a_reason_the_others_do_not()
    {
        Assert.True(ResponseTransitions.NeedsReason(ResponseActions.Close));
        Assert.True(ResponseTransitions.NeedsReason(ResponseActions.Reopen));
        Assert.All(new[] { ResponseActions.Take, ResponseActions.ConvertToLead, ResponseActions.CreateActivity, ResponseActions.ApplyOptOut }, a => Assert.False(ResponseTransitions.NeedsReason(a)));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("   ", null, true)]
    [InlineData("  Bahar-2026 ", "bahar-2026", true)]
    [InlineData("Haber.Bulteni_1~x", "haber.bulteni_1~x", true)]
    [InlineData("iki kelime", "iki kelime", false)]
    [InlineData("a&b=c", "a&b=c", false)]
    [InlineData("-tire", "-tire", false)]
    [InlineData("türkçe", "türkçe", false)]
    public void Utm_values_are_trimmed_lowercased_and_restricted_to_safe_characters(string? raw, string? expected, bool valid)
    {
        Assert.Equal(expected, UtmRules.Normalize(raw, out var ok));
        Assert.Equal(valid, ok);
    }

    [Fact]
    public void Utm_values_longer_than_the_limit_are_invalid()
    {
        UtmRules.Normalize(new string('a', UtmRules.MaxLength + 1), out var ok);
        Assert.False(ok);
    }

    [Fact]
    public void Blank_utm_fields_fall_back_to_source_campaign_medium_channel_and_campaign_code()
    {
        Assert.Equal(("kampanya", "email", "antrepo-2026"), UtmRules.Resolve(null, null, null, "Email", "antrepo-2026"));
        Assert.Equal(("bulten", "sms", "yaz"), UtmRules.Resolve("bulten", "sms", "yaz", "Email", "antrepo-2026"));
        Assert.Equal(("bulten", "whatsapp", "antrepo-2026"), UtmRules.Resolve("bulten", null, null, "WhatsApp", "antrepo-2026"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  ", null, null)]
    [InlineData("Ayşe", null, "Ayşe")]
    [InlineData("Ayşe Demir", "Ayşe", "Demir")]
    [InlineData("  Ayşe  Nur   Demir ", "Ayşe Nur", "Demir")]
    public void A_full_name_splits_into_first_and_last_name(string? full, string? first, string? last) =>
        Assert.Equal((first, last), PersonNames.Split(full));
}
