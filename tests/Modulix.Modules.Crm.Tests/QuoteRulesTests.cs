using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Teklif geçiş kuralları: onay şartı, süre, sürüm geçersizleşmesi, tekrar çağrı (veritabanı gerektirmez).</summary>
public class QuoteRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Future = Now.AddDays(10);

    private static QuoteRuleResult Can(string action, string status, string approval = QuoteApprovalStatus.NotRequired,
        DateTimeOffset? until = null, bool lines = true) => QuoteRules.Can(action, status, approval, until ?? Future, Now, lines);

    [Theory]
    [InlineData(QuoteApprovalStatus.NotRequired, true)]
    [InlineData(QuoteApprovalStatus.Approved, true)]
    [InlineData(QuoteApprovalStatus.Pending, false)]
    [InlineData(QuoteApprovalStatus.Rejected, false)]
    [InlineData(QuoteApprovalStatus.ChangesRequested, false)]
    public void Present_requires_completed_approval(string approval, bool allowed) =>
        Assert.Equal(allowed, Can(QuoteAction.Present, QuoteStatus.Draft, approval).Allowed);

    [Fact]
    public void Present_needs_lines_and_valid_date()
    {
        Assert.Equal("no-lines", Can(QuoteAction.Present, QuoteStatus.Draft, lines: false).Code);
        Assert.Equal("expired", Can(QuoteAction.Present, QuoteStatus.Draft, until: Now.AddMinutes(-1)).Code);
    }

    [Theory]
    [InlineData(QuoteStatus.Superseded, "superseded")]
    [InlineData(QuoteStatus.Cancelled, "cancelled")]
    [InlineData(QuoteStatus.Accepted, "already-accepted")]
    [InlineData(QuoteStatus.Rejected, "already-rejected")]
    [InlineData(QuoteStatus.Draft, "not-presented")]
    public void Accept_is_blocked_for_non_presented(string status, string code)
    {
        var result = Can(QuoteAction.Accept, status);

        Assert.False(result.Allowed);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public void Accept_rechecks_validity_at_acceptance_time()
    {
        Assert.True(Can(QuoteAction.Accept, QuoteStatus.Presented).Allowed);
        Assert.Equal("expired", Can(QuoteAction.Accept, QuoteStatus.Presented, until: Now).Code);
    }

    [Fact]
    public void Only_draft_is_commercially_editable()
    {
        Assert.True(QuoteRules.IsCommerciallyEditable(QuoteStatus.Draft));
        Assert.All(QuoteStatus.All.Where(s => s != QuoteStatus.Draft), s => Assert.False(QuoteRules.IsCommerciallyEditable(s)));
    }

    [Fact]
    public void Approval_decisions_need_pending_draft()
    {
        Assert.True(Can(QuoteAction.Approve, QuoteStatus.Draft, QuoteApprovalStatus.Pending).Allowed);
        Assert.False(Can(QuoteAction.Approve, QuoteStatus.Draft, QuoteApprovalStatus.Approved).Allowed);
        Assert.False(Can(QuoteAction.SubmitForApproval, QuoteStatus.Draft, QuoteApprovalStatus.Pending).Allowed);
    }
}
