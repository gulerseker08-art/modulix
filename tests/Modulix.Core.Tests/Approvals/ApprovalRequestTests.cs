using Modulix.Core.Approvals;

namespace Modulix.Core.Tests.Approvals;

/// <summary>ADR-0027 Dilim 4a: talebin saf ilerleme kuralları (eşik, sıralı/paralel adım, ret, geri çekme).</summary>
public class ApprovalRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static ApprovalStepDefinition Step(string name, int order, decimal? min = null, int? due = null) =>
        new(name, order, Approver, null, min, due);

    private static ApprovalDefinition Definition(string? currency, params ApprovalStepDefinition[] steps) =>
        ApprovalDefinition.Create("Satın alma", null, currency, steps, Requester, Now);

    [Fact]
    public void Steps_below_the_amount_threshold_are_left_out_and_first_order_opens()
    {
        var definition = Definition("TRY", Step("Yönetici", 1), Step("Genel müdür", 2, min: 100_000m));

        var (request, steps) = ApprovalRequest.Open(definition, "Yazıcı", null, 5_000m, "try", Requester, Now);

        Assert.Equal(ApprovalRequestStatus.Pending, request.Status);
        Assert.Equal("TRY", request.Currency);
        ApprovalRequestStep only = Assert.Single(steps);
        Assert.Equal(ApprovalStepStatus.Active, only.Status);
    }

    [Fact]
    public void A_request_under_every_threshold_is_approved_at_once()
    {
        var definition = Definition("TRY", Step("Genel müdür", 1, min: 100_000m));

        var (request, steps) = ApprovalRequest.Open(definition, "Kalem", null, 10m, "TRY", Requester, Now);

        Assert.Empty(steps);
        Assert.Equal(ApprovalRequestStatus.Approved, request.Status);
        Assert.Equal(Now, request.CompletedAt);
    }

    [Fact]
    public void Parallel_steps_must_all_approve_before_the_next_order_opens()
    {
        var definition = Definition(null, Step("Finans", 1), Step("Hukuk", 1), Step("Genel müdür", 2, due: 24));
        var (request, steps) = ApprovalRequest.Open(definition, "Sözleşme dışı alım", null, null, null, Requester, Now);
        Assert.Equal(2, steps.Count(s => s.Status == ApprovalStepStatus.Active));

        request.Approve(steps[0], steps, Approver, null, Now);
        Assert.Equal(ApprovalStepStatus.Waiting, steps[2].Status);

        request.Approve(steps[1], steps, Approver, null, Now.AddHours(1));
        Assert.Equal(ApprovalStepStatus.Active, steps[2].Status);
        Assert.Equal(Now.AddHours(25), steps[2].DueAt);

        request.Approve(steps[2], steps, Approver, "Uygun", Now.AddHours(2));
        Assert.Equal(ApprovalRequestStatus.Approved, request.Status);
    }

    [Fact]
    public void Rejecting_closes_the_request_and_cancels_the_remaining_steps()
    {
        var definition = Definition(null, Step("Yönetici", 1), Step("Genel müdür", 2));
        var (request, steps) = ApprovalRequest.Open(definition, "Masraf", null, null, null, Requester, Now);

        request.Reject(steps[0], steps, Approver, "Bütçe yok", Now);

        Assert.Equal(ApprovalRequestStatus.Rejected, request.Status);
        Assert.Equal(ApprovalStepStatus.Rejected, steps[0].Status);
        Assert.Equal(ApprovalStepStatus.Cancelled, steps[1].Status);
    }

    [Theory]
    [InlineData(null, "TRY")]
    [InlineData(5.0, "USD")]
    public void Amount_must_be_in_the_process_currency(double? amount, string currency)
    {
        var definition = Definition("TRY", Step("Yönetici", 1));

        Assert.Throws<ArgumentException>(() =>
            ApprovalRequest.Open(definition, "Başlık", null, (decimal?)amount, currency, Requester, Now));
    }

    [Fact]
    public void Definition_needs_exactly_one_approver_per_step_and_a_currency_for_thresholds()
    {
        Assert.Throws<ArgumentException>(() => Definition(null, new ApprovalStepDefinition("Boş", 1, null, null, null, null)));
        Assert.Throws<ArgumentException>(() => Definition(null, new ApprovalStepDefinition("İkisi", 1, Approver, Guid.NewGuid(), null, null)));
        Assert.Throws<ArgumentException>(() => Definition(null, Step("Eşik", 1, min: 10m)));
        Assert.Throws<ArgumentException>(() => Definition(null));
    }

    [Fact]
    public void Changing_steps_raises_the_revision_but_renaming_does_not()
    {
        var definition = Definition(null, Step("Yönetici", 1));

        definition.Update("Yeni ad", null, null, [Step("Yönetici", 1)], Requester, Now);
        Assert.Equal(1, definition.Revision);

        definition.Update("Yeni ad", null, null, [Step("Yönetici", 1), Step("Genel müdür", 2)], Requester, Now);
        Assert.Equal(2, definition.Revision);
    }

    [Fact]
    public void Gated_commands_are_trimmed_deduplicated_and_need_a_record_bound_process()
    {
        var definition = ApprovalDefinition.Create("Fırsat onayı", null, "TRY", [Step("Yönetici", 1)], Requester, Now, "crm.opportunity", [" win ", "win", ""]);
        Assert.Equal(["win"], definition.Commands);

        definition.Update("Fırsat onayı", null, "TRY", [Step("Yönetici", 1)], Requester, Now, "crm.opportunity", []);
        Assert.Empty(definition.Commands);
        Assert.Null(definition.GatedCommands);

        Assert.Throws<ArgumentException>(() =>
            ApprovalDefinition.Create("Serbest", null, null, [Step("Yönetici", 1)], Requester, Now, null, ["win"]));
        Assert.Throws<ArgumentException>(() =>
            ApprovalDefinition.Create("Virgüllü", null, null, [Step("Yönetici", 1)], Requester, Now, "crm.opportunity", ["a,b"]));
    }

    [Fact]
    public void An_approved_request_can_be_invalidated_when_its_amount_changes()
    {
        var definition = Definition("TRY", Step("Yönetici", 1));
        var (request, steps) = ApprovalRequest.Open(definition, "Başlık", null, 100m, "TRY", Requester, Now);
        request.Approve(steps[0], steps, Approver, null, Now);
        Assert.Equal(ApprovalRequestStatus.Approved, request.Status);

        request.Invalidate(steps, Now.AddHours(1));

        Assert.Equal(ApprovalRequestStatus.Invalidated, request.Status);
        Assert.Equal(ApprovalStepStatus.Approved, steps[0].Status);
    }
}
