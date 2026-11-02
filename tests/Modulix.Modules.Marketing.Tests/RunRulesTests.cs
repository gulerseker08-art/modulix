using Modulix.Core.Contracts.Communication;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Runs;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 5: çalıştırma durum makinesi, içerik kuralları ve teslimat anahtarı (saf).</summary>
public class RunRulesTests
{
    [Theory]
    [InlineData(RunActions.Submit, RunStatuses.Draft, true)]
    [InlineData(RunActions.Submit, RunStatuses.Approved, false)]
    [InlineData(RunActions.Approve, RunStatuses.PendingApproval, true)]
    [InlineData(RunActions.Approve, RunStatuses.Draft, false)]
    [InlineData(RunActions.Start, RunStatuses.Approved, true)]
    [InlineData(RunActions.Start, RunStatuses.PendingApproval, false)]
    [InlineData(RunActions.Start, RunStatuses.Cancelled, false)]
    [InlineData(RunActions.Pause, RunStatuses.Running, true)]
    [InlineData(RunActions.Pause, RunStatuses.Paused, false)]
    [InlineData(RunActions.Resume, RunStatuses.Paused, true)]
    [InlineData(RunActions.Resume, RunStatuses.Completed, false)]
    [InlineData(RunActions.Reopen, RunStatuses.Approved, true)]
    [InlineData(RunActions.Reopen, RunStatuses.Running, false)]
    [InlineData(RunActions.Cancel, RunStatuses.Running, true)]
    [InlineData(RunActions.Cancel, RunStatuses.Completed, false)]
    [InlineData(RunActions.Cancel, RunStatuses.Cancelled, false)]
    public void Only_listed_transitions_are_allowed(string action, string status, bool allowed) =>
        Assert.Equal(allowed, RunTransitions.Allowed(action, status));

    [Fact]
    public void Finished_runs_accept_no_action_at_all()
    {
        foreach (var status in new[] { RunStatuses.Completed, RunStatuses.Cancelled })
        {
            foreach (var action in new[] { RunActions.Submit, RunActions.Approve, RunActions.Reject, RunActions.Reopen, RunActions.Start, RunActions.Pause, RunActions.Resume, RunActions.Cancel })
            {
                Assert.False(RunTransitions.Allowed(action, status), $"{action} / {status}");
            }
        }
    }

    [Theory]
    [InlineData(true, RunStatuses.PendingApproval)]
    [InlineData(false, RunStatuses.Approved)]
    public void Submission_needs_approval_only_when_required(bool required, string expected) =>
        Assert.Equal(expected, RunTransitions.Next(RunActions.Submit, required));

    [Fact]
    public void Reject_and_reopen_return_to_draft_and_reason_is_required_where_it_matters()
    {
        Assert.Equal(RunStatuses.Draft, RunTransitions.Next(RunActions.Reject, true));
        Assert.Equal(RunStatuses.Draft, RunTransitions.Next(RunActions.Reopen, true));
        Assert.True(RunTransitions.NeedsReason(RunActions.Reject));
        Assert.True(RunTransitions.NeedsReason(RunActions.Reopen));
        Assert.True(RunTransitions.NeedsReason(RunActions.Cancel));
        Assert.False(RunTransitions.NeedsReason(RunActions.Pause));
        Assert.False(RunTransitions.NeedsReason(RunActions.Approve));
    }

    [Fact]
    public void Only_a_draft_is_editable()
    {
        Assert.True(RunTransitions.IsEditable(RunStatuses.Draft));
        foreach (var status in RunStatuses.All.Where(s => s != RunStatuses.Draft))
        {
            Assert.False(RunTransitions.IsEditable(status), status);
        }
    }

    [Fact]
    public void A_valid_email_needs_a_subject_and_a_body()
    {
        Assert.Empty(ContentRules.Validate(CommunicationChannel.Email, "Konu", "Merhaba https://example.test/x"));
        Assert.Contains(ContentRules.Validate(CommunicationChannel.Email, null, "Metin"), e => e.Field == "subject");
        Assert.Contains(ContentRules.Validate(CommunicationChannel.Email, "Konu", "  "), e => e.Field == "body");
        Assert.Contains(ContentRules.Validate(CommunicationChannel.Email, "Satır\nsonu", "Metin"), e => e.Field == "subject");
        Assert.Empty(ContentRules.Validate(CommunicationChannel.Sms, null, "Kısa mesaj"));
    }

    [Theory]
    [InlineData("Tıklayın javascript:alert(1)")]
    [InlineData("JAVASCRIPT : alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("vbscript:x")]
    [InlineData("file://etc/passwd")]
    [InlineData("ara tel:+905321234567")]
    public void Only_http_and_https_links_are_allowed(string body) =>
        Assert.Contains(ContentRules.Validate(CommunicationChannel.Email, "Konu", body), e => e.Field == "body");

    [Theory]
    [InlineData("Toplantı 10:30'da. Not: yarın")]
    [InlineData("Bağlantı http://example.test ve https://example.test/yol?x=1")]
    public void Ordinary_text_with_colons_and_http_links_is_fine(string body) =>
        Assert.Empty(ContentRules.Validate(CommunicationChannel.Email, "Konu", body));

    [Fact]
    public void The_delivery_key_is_stable_per_tenant_run_channel_and_address_and_hides_the_address()
    {
        var tenant = Guid.NewGuid();
        var run = Guid.NewGuid();

        var key = DeliveryKeys.For(tenant, run, CommunicationChannel.Email, "ayse@example.com");

        Assert.Equal(key, DeliveryKeys.For(tenant, run, CommunicationChannel.Email, "ayse@example.com"));
        Assert.NotEqual(key, DeliveryKeys.For(tenant, run, CommunicationChannel.Email, "can@example.com"));
        Assert.NotEqual(key, DeliveryKeys.For(tenant, Guid.NewGuid(), CommunicationChannel.Email, "ayse@example.com"));
        Assert.NotEqual(key, DeliveryKeys.For(Guid.NewGuid(), run, CommunicationChannel.Email, "ayse@example.com"));
        Assert.NotEqual(key, DeliveryKeys.For(tenant, run, CommunicationChannel.Sms, "ayse@example.com"));
        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("ayse", key, StringComparison.OrdinalIgnoreCase);
    }
}
