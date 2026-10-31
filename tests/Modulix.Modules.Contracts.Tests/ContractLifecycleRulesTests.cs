using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0021 dilim 2: dönem aritmetiği (ay sonu, artık yıl, tenant günü), tarih değerlendirmesi, geçiş ön koşulları, anlık görüntü özeti ve farkı.</summary>
public class ContractLifecycleRulesTests
{
    [Theory]
    [InlineData("2026-11-01", 12, "2027-10-31")] // örnek senaryo: 1 Kasım'dan on iki ay
    [InlineData("2027-03-01", 12, "2028-02-29")] // artık yıl: Şubat 29'da biter
    [InlineData("2024-02-29", 12, "2025-02-28")] // artık günde başlayan dönem
    [InlineData("2026-01-31", 1, "2026-02-28")]  // ay sonu taşması
    [InlineData("2026-01-15", 1, "2026-02-14")]
    [InlineData("2026-12-01", 1, "2026-12-31")]  // yıl geçişi
    [InlineData("2026-01-31", 3, "2026-04-30")]
    public void Term_end_follows_calendar_months_not_365_days(string start, int months, string expectedEnd)
    {
        Assert.Equal(DateOnly.Parse(expectedEnd), ContractPeriod.TermEnd(DateOnly.Parse(start), months));
    }

    [Fact]
    public void Next_term_starts_the_day_after_the_current_end_without_gap_or_overlap()
    {
        var end = ContractPeriod.TermEnd(new DateOnly(2026, 11, 1), 12);
        var next = ContractPeriod.NextTermStart(end);
        Assert.Equal(new DateOnly(2027, 11, 1), next);
        Assert.Equal(new DateOnly(2028, 10, 31), ContractPeriod.TermEnd(next, 12));
    }

    [Fact]
    public void Today_is_computed_in_the_tenant_time_zone()
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        // 31 Ekim 23:30 UTC = 1 Kasım 02:30 İstanbul.
        var now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 1), ContractPeriod.Today(now, istanbul));
        Assert.Equal(new DateOnly(2026, 10, 31), ContractPeriod.Today(now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Signed_contract_activates_only_when_start_date_arrives_and_expires_after_end_date()
    {
        var start = new DateOnly(2026, 11, 1);
        var end = new DateOnly(2027, 10, 31);
        Assert.Equal(EffectiveStatus.AwaitingStart, ContractDates.StatusOn(new DateOnly(2026, 10, 31), start, end, false));
        Assert.Equal(EffectiveStatus.Active, ContractDates.StatusOn(start, start, end, false));
        Assert.Equal(EffectiveStatus.Active, ContractDates.StatusOn(end, start, end, false)); // bitiş günü dahil
        Assert.Equal(EffectiveStatus.Expired, ContractDates.StatusOn(end.AddDays(1), start, end, false));
        Assert.Equal(EffectiveStatus.Active, ContractDates.StatusOn(new DateOnly(2040, 1, 1), start, null, true)); // belirsiz süreli dolmaz
        Assert.Equal(EffectiveStatus.AwaitingStart, ContractDates.StatusOn(end, null, end, false)); // başlangıç girilmemiş

        Assert.Equal(EffectiveStatus.Active, ContractDates.Evaluate(EffectiveStatus.AwaitingStart, start, start, end, false));
        Assert.Null(ContractDates.Evaluate(EffectiveStatus.Active, start, start, end, false));
        Assert.Equal(EffectiveStatus.Expired, ContractDates.Evaluate(EffectiveStatus.Active, end.AddDays(1), start, end, false));
        Assert.Null(ContractDates.Evaluate(EffectiveStatus.Suspended, end.AddDays(1), start, end, false));
        Assert.Null(ContractDates.Evaluate(EffectiveStatus.Expired, start, start, end, false)); // geri aktifleşmez
    }

    private static ContractState Draft(bool dates = true) =>
        new(PreparationStatus.Draft, ApprovalStatus.NotRequired, SignatureStatus.NotSent, EffectiveStatus.NotInForce, dates, dates);

    [Fact]
    public void Preparation_moves_draft_to_review_to_ready_and_requires_dates_before_ready()
    {
        Assert.True(ContractTransitions.Can(ContractAction.SendToReview, Draft()).Allowed);
        Assert.Equal("not-in-review", ContractTransitions.Can(ContractAction.MarkReady, Draft()).Code);

        var inReview = Draft(dates: false) with { Preparation = PreparationStatus.InReview };
        Assert.Equal("missing-start", ContractTransitions.Can(ContractAction.MarkReady, inReview).Code);
        Assert.Equal("missing-end", ContractTransitions.Can(ContractAction.MarkReady, inReview with { HasStartDate = true }).Code);
        Assert.True(ContractTransitions.Can(ContractAction.MarkReady, inReview with { HasStartDate = true, HasEndOrIndefinite = true }).Allowed);
        Assert.Equal("not-ready", ContractTransitions.Can(ContractAction.SubmitForApproval, inReview).Code);
    }

    [Fact]
    public void Approval_requires_ready_content_and_blocks_edits_until_decided()
    {
        var ready = Draft() with { Preparation = PreparationStatus.ReadyToPublish };
        Assert.True(ContractTransitions.Can(ContractAction.SubmitForApproval, ready).Allowed);

        var pending = ready with { Approval = ApprovalStatus.Pending };
        Assert.Equal("approval-pending", ContractTransitions.Can(ContractAction.ReopenDraft, pending).Code);
        Assert.Equal("approval-pending", ContractTransitions.Can(ContractAction.SubmitForApproval, pending).Code);
        Assert.True(ContractTransitions.Can(ContractAction.Approve, pending).Allowed);
        Assert.True(ContractTransitions.Can(ContractAction.RejectApproval, pending).Allowed);
        Assert.True(ContractTransitions.Can(ContractAction.WithdrawApproval, pending).Allowed);
        Assert.Equal("not-pending", ContractTransitions.Can(ContractAction.Approve, ready).Code);

        var approved = ready with { Approval = ApprovalStatus.Approved };
        Assert.Equal("already-approved", ContractTransitions.Can(ContractAction.SubmitForApproval, approved).Code);
        Assert.True(ContractTransitions.Can(ContractAction.ReopenDraft, approved).Allowed); // onay geçersiz kılınarak taslağa dönülür

        var rejected = ready with { Approval = ApprovalStatus.Rejected };
        Assert.True(ContractTransitions.Can(ContractAction.SubmitForApproval, rejected).Allowed);
        Assert.True(ContractTransitions.Can(ContractAction.ReopenDraft, rejected).Allowed);
    }

    [Fact]
    public void Signed_or_in_force_content_is_revised_not_reopened_and_cannot_be_cancelled()
    {
        var signed = Draft() with { Preparation = PreparationStatus.ReadyToPublish, Approval = ApprovalStatus.Approved, Signature = SignatureStatus.Completed, Effective = EffectiveStatus.AwaitingStart };
        Assert.Equal("signature-in-progress", ContractTransitions.Can(ContractAction.ReopenDraft, signed).Code);
        Assert.Equal("in-force", ContractTransitions.Can(ContractAction.Cancel, signed).Code);
        Assert.True(ContractTransitions.Can(ContractAction.Revise, signed).Allowed);

        var signing = signed with { Signature = SignatureStatus.PartiallySigned, Effective = EffectiveStatus.NotInForce };
        Assert.Equal("in-progress", ContractTransitions.Can(ContractAction.Revise, signing).Code);
        Assert.Equal("signed", ContractTransitions.Can(ContractAction.Cancel, signing).Code);

        Assert.Equal("not-signed", ContractTransitions.Can(ContractAction.Revise, Draft()).Code);
        Assert.True(ContractTransitions.Can(ContractAction.Cancel, Draft()).Allowed);
        Assert.Equal("closed", ContractTransitions.Can(ContractAction.SendToReview, Draft() with { Effective = EffectiveStatus.Cancelled }).Code);
    }

    [Fact]
    public void Reject_cancel_and_revise_require_a_reason()
    {
        Assert.True(ContractTransitions.RequiresReason(ContractAction.RejectApproval));
        Assert.True(ContractTransitions.RequiresReason(ContractAction.Cancel));
        Assert.True(ContractTransitions.RequiresReason(ContractAction.Revise));
        Assert.False(ContractTransitions.RequiresReason(ContractAction.Approve));
        Assert.Equal("unknown-action", ContractTransitions.Can("explode", Draft()).Code);
    }

    private static ContractSnapshot Snapshot(decimal price = 100m, string title = "Hizmet", params SnapshotLine[] extra)
    {
        var key = new Guid("00000000-0000-0000-0000-000000000001");
        var line = new SnapshotLine(key, 1, "Service", null, "Kurulum", null, 2m, "adet", price, 0m, 20m, null, 2 * price, 0.4m * price, 2.4m * price);
        return new ContractSnapshot("SZ-1", 1, title, "Service", null, Guid.Empty, null, new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31), false, "TRY", false,
            null, null, "None", null, null, null, "Internal", 2 * price, 0.4m * price, 2.4m * price, null, [], [line, .. extra]);
    }

    [Fact]
    public void Snapshot_hash_is_stable_for_the_same_content_and_changes_with_it()
    {
        var a = ContractSnapshots.Serialize(Snapshot());
        var b = ContractSnapshots.Serialize(Snapshot());
        var c = ContractSnapshots.Serialize(Snapshot(price: 101m));
        Assert.Equal(ContractSnapshots.Hash(a), ContractSnapshots.Hash(b));
        Assert.NotEqual(ContractSnapshots.Hash(a), ContractSnapshots.Hash(c));
        Assert.Equal(64, ContractSnapshots.Hash(a).Length);
        Assert.Equal(a, ContractSnapshots.Serialize(ContractSnapshots.Deserialize(a)!)); // gidiş-dönüş aynı metni üretir
        Assert.Null(ContractSnapshots.Deserialize("{bozuk"));
    }

    [Fact]
    public void Compare_reports_header_and_line_changes_and_hides_financials_without_permission()
    {
        var before = Snapshot();
        var added = new SnapshotLine(new Guid("00000000-0000-0000-0000-000000000002"), 2, "Service", null, "Eğitim", null, 1m, null, 50m, 0m, 0m, "Monthly", 50m, 0m, 50m);
        var after = Snapshot(price: 120m, title: "Hizmet (güncel)", added);

        var full = SnapshotComparer.Compare(before, after, includeFinancials: true);
        Assert.Contains(full.Header, f => f.Field == "title" && f.Before == "Hizmet" && f.After == "Hizmet (güncel)");
        Assert.Contains(full.Header, f => f.Field == "grossTotal");
        Assert.Contains(full.Lines, l => l.Change == "Changed" && l.Fields.Any(f => f.Field == "unitPrice" && f.After == "120"));
        Assert.Contains(full.Lines, l => l.Change == "Added" && l.Name == "Eğitim");

        var masked = SnapshotComparer.Compare(before, after, includeFinancials: false);
        Assert.DoesNotContain(masked.Header, f => f.Field is "grossTotal" or "netTotal" or "taxTotal");
        Assert.DoesNotContain(masked.Lines, l => l.Change == "Changed"); // yalnızca fiyat değişen kalem izinsiz okuyucuya değişmemiş görünür
        Assert.Contains(masked.Lines, l => l.Change == "Added");
        Assert.True(SnapshotComparer.Compare(before, before, true).IsEmpty);
        Assert.Null(before.WithoutFinancials().ContractValue);
        Assert.Equal(0m, before.WithoutFinancials().Lines[0].UnitPrice);
    }
}
