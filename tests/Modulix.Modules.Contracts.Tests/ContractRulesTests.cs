using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 saf kurallar: tarih tutarlılığı, bildirim tarihi, içerik düzenleme ve silme kapıları, numara biçimi.</summary>
public class ContractRulesTests
{
    [Fact]
    public void End_date_before_start_date_is_rejected()
    {
        var errors = ContractRules.ValidateDates(new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 31), false, RenewalMode.None, null, null);
        Assert.Contains(errors, e => e.Field == "endDate" && e.Message.Contains("önce olamaz"));
    }

    [Fact]
    public void Indefinite_contract_must_not_carry_an_end_date_but_needs_none()
    {
        var withEnd = ContractRules.ValidateDates(new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31), true, RenewalMode.None, null, null);
        Assert.Single(withEnd, e => e.Field == "endDate");

        var withoutEnd = ContractRules.ValidateDates(new DateOnly(2026, 11, 1), null, true, RenewalMode.None, null, 30);
        Assert.Empty(withoutEnd);
    }

    [Theory]
    [InlineData(RenewalMode.Manual, null)]
    [InlineData(RenewalMode.Automatic, 0)]
    public void Renewable_contract_requires_a_positive_renewal_period(string mode, int? months)
    {
        var errors = ContractRules.ValidateDates(null, null, false, mode, months, null);
        Assert.Single(errors, e => e.Field == "renewalPeriodMonths");
    }

    [Fact]
    public void Non_renewable_contract_rejects_a_renewal_period_and_bounds_are_enforced()
    {
        Assert.Single(ContractRules.ValidateDates(null, null, false, RenewalMode.None, 12, null), e => e.Field == "renewalPeriodMonths");
        Assert.Single(ContractRules.ValidateDates(null, null, false, RenewalMode.Manual, 121, null), e => e.Field == "renewalPeriodMonths");
        Assert.Single(ContractRules.ValidateDates(null, null, false, RenewalMode.None, null, -1), e => e.Field == "noticePeriodDays");
        Assert.Single(ContractRules.ValidateDates(null, null, false, "Weird", null, null), e => e.Field == "renewalMode");
    }

    [Fact]
    public void Notice_deadline_is_end_minus_notice_days_and_absent_for_indefinite_or_open_ended()
    {
        Assert.Equal(new DateOnly(2027, 8, 2), ContractRules.NoticeDeadline(new DateOnly(2027, 10, 31), 90, false));
        Assert.Null(ContractRules.NoticeDeadline(new DateOnly(2027, 10, 31), 90, true));
        Assert.Null(ContractRules.NoticeDeadline(null, 90, false));
        Assert.Null(ContractRules.NoticeDeadline(new DateOnly(2027, 10, 31), null, false));
    }

    [Fact]
    public void Content_is_editable_only_in_draft_without_pending_approval_or_signature()
    {
        Assert.True(ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.NotRequired, SignatureStatus.NotSent, EffectiveStatus.NotInForce).Allowed);
        Assert.True(ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.Rejected, SignatureStatus.Rejected, EffectiveStatus.NotInForce).Allowed);

        Assert.Equal("not-draft", ContractRules.CanEditContent(PreparationStatus.InReview, ApprovalStatus.NotRequired, SignatureStatus.NotSent, EffectiveStatus.NotInForce).Code);
        Assert.Equal("approval-pending", ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.Pending, SignatureStatus.NotSent, EffectiveStatus.NotInForce).Code);
        Assert.Equal("signature-in-progress", ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.Approved, SignatureStatus.PartiallySigned, EffectiveStatus.NotInForce).Code);
        Assert.Equal("signature-in-progress", ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.Approved, SignatureStatus.Completed, EffectiveStatus.NotInForce).Code);
        Assert.Equal("in-force", ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.NotRequired, SignatureStatus.NotSent, EffectiveStatus.Active).Code);
        Assert.Equal("in-force", ContractRules.CanEditContent(PreparationStatus.Draft, ApprovalStatus.NotRequired, SignatureStatus.NotSent, EffectiveStatus.Terminated).Code);
    }

    [Fact]
    public void Signed_or_in_force_contracts_cannot_be_deleted()
    {
        Assert.True(ContractRules.CanDelete(SignatureStatus.NotSent, EffectiveStatus.NotInForce).Allowed);
        Assert.Equal("signed", ContractRules.CanDelete(SignatureStatus.Completed, EffectiveStatus.NotInForce).Code);
        Assert.Equal("signed", ContractRules.CanDelete(SignatureStatus.PartiallySigned, EffectiveStatus.NotInForce).Code);
        Assert.Equal("in-force", ContractRules.CanDelete(SignatureStatus.NotSent, EffectiveStatus.AwaitingStart).Code);
        Assert.Equal("in-force", ContractRules.CanDelete(SignatureStatus.Cancelled, EffectiveStatus.Cancelled).Code);
    }

    [Fact]
    public void Number_has_prefix_date_and_six_unambiguous_characters()
    {
        var number = ContractRules.NewNumber(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Matches("^SZ-20261031-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{6}$", number);
        Assert.NotEqual(number, ContractRules.NewNumber(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Binding_effective_statuses_are_awaiting_start_active_and_suspended()
    {
        Assert.True(EffectiveStatus.IsBinding(EffectiveStatus.AwaitingStart));
        Assert.True(EffectiveStatus.IsBinding(EffectiveStatus.Active));
        Assert.True(EffectiveStatus.IsBinding(EffectiveStatus.Suspended));
        Assert.False(EffectiveStatus.IsBinding(EffectiveStatus.NotInForce));
        Assert.False(EffectiveStatus.IsBinding(EffectiveStatus.Expired));
    }
}
