using Modulix.Modules.Support.Lifecycle;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024: talep durum makinesi, öncelik matrisi, çözüm ve ilk yanıt kuralları (saf kurallar; sabit saatle).</summary>
public class TicketRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static TicketState State(string status, bool hasOwner = true, DateTimeOffset? closedAt = null, int window = 7) =>
        new(status, hasOwner, closedAt, Now, window);

    [Theory]
    [InlineData(TicketStatus.New, TicketAction.Triage, true)]
    [InlineData(TicketStatus.Triage, TicketAction.Triage, false)]
    [InlineData(TicketStatus.New, TicketAction.Assign, true)]
    [InlineData(TicketStatus.InProgress, TicketAction.Assign, true)]
    [InlineData(TicketStatus.Resolved, TicketAction.Assign, false)]
    [InlineData(TicketStatus.Closed, TicketAction.Assign, false)]
    [InlineData(TicketStatus.Cancelled, TicketAction.Take, false)]
    [InlineData(TicketStatus.Assigned, TicketAction.Start, true)]
    [InlineData(TicketStatus.InProgress, TicketAction.Start, false)]
    [InlineData(TicketStatus.InProgress, TicketAction.WaitCustomer, true)]
    [InlineData(TicketStatus.WaitingCustomer, TicketAction.WaitCustomer, false)]
    [InlineData(TicketStatus.WaitingCustomer, TicketAction.WaitInternal, true)]
    [InlineData(TicketStatus.New, TicketAction.WaitCustomer, false)]
    [InlineData(TicketStatus.WaitingInternal, TicketAction.Resume, true)]
    [InlineData(TicketStatus.InProgress, TicketAction.Resume, false)]
    [InlineData(TicketStatus.InProgress, TicketAction.Resolve, true)]
    [InlineData(TicketStatus.New, TicketAction.Resolve, false)]
    [InlineData(TicketStatus.Resolved, TicketAction.Resolve, false)]
    [InlineData(TicketStatus.Resolved, TicketAction.Close, true)]
    [InlineData(TicketStatus.InProgress, TicketAction.Close, false)]
    [InlineData(TicketStatus.Resolved, TicketAction.Reopen, true)]
    [InlineData(TicketStatus.InProgress, TicketAction.Reopen, false)]
    [InlineData(TicketStatus.Assigned, TicketAction.Cancel, true)]
    [InlineData(TicketStatus.Resolved, TicketAction.Cancel, false)]
    [InlineData(TicketStatus.Closed, TicketAction.Cancel, false)]
    [InlineData(TicketStatus.Resolved, TicketAction.SetPriority, true)]
    [InlineData(TicketStatus.Closed, TicketAction.SetPriority, false)]
    [InlineData(TicketStatus.Cancelled, TicketAction.SetPriority, false)]
    public void Transition_table_allows_only_the_supported_moves(string status, string action, bool allowed)
    {
        Assert.Equal(allowed, TicketTransitions.Can(action, State(status)).Allowed);
    }

    [Fact]
    public void Starting_work_requires_an_owner()
    {
        var rule = TicketTransitions.Can(TicketAction.Start, State(TicketStatus.New, hasOwner: false));

        Assert.False(rule.Allowed);
        Assert.Equal("no-owner", rule.Code);
        Assert.True(TicketTransitions.Can(TicketAction.Start, State(TicketStatus.New, hasOwner: true)).Allowed);
    }

    [Fact]
    public void Unknown_actions_are_denied()
    {
        Assert.False(TicketTransitions.Can("delete", State(TicketStatus.New)).Allowed);
    }

    [Fact]
    public void Every_action_has_a_target_and_a_reason_policy()
    {
        foreach (var action in TicketAction.All)
        {
            Assert.Contains(TicketTransitions.TargetStatus(action, TicketStatus.InProgress), TicketStatus.All);
        }

        Assert.True(TicketTransitions.RequiresReason(TicketAction.Cancel));
        Assert.True(TicketTransitions.RequiresReason(TicketAction.Reopen));
        Assert.True(TicketTransitions.RequiresReason(TicketAction.Transfer));
        Assert.True(TicketTransitions.RequiresReason(TicketAction.SetPriority));
        Assert.False(TicketTransitions.RequiresReason(TicketAction.Resolve));
        Assert.False(TicketTransitions.RequiresReason(TicketAction.Close));
    }

    [Fact]
    public void Resolved_and_closed_are_different_states()
    {
        Assert.NotEqual(TicketStatus.Resolved, TicketStatus.Closed);
        Assert.False(TicketStatus.IsFinal(TicketStatus.Resolved));
        Assert.True(TicketStatus.IsFinal(TicketStatus.Closed));
        Assert.Equal(TicketStatus.Resolved, TicketTransitions.TargetStatus(TicketAction.Resolve, TicketStatus.InProgress));
        Assert.Equal(TicketStatus.Closed, TicketTransitions.TargetStatus(TicketAction.Close, TicketStatus.Resolved));
    }

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Assigned)]
    [InlineData(TicketStatus.Triage, TicketStatus.Assigned)]
    [InlineData(TicketStatus.Reopened, TicketStatus.Assigned)]
    [InlineData(TicketStatus.InProgress, TicketStatus.InProgress)]
    [InlineData(TicketStatus.WaitingCustomer, TicketStatus.WaitingCustomer)]
    public void Assigning_moves_unworked_tickets_to_assigned_and_keeps_work_in_flight(string current, string expected)
    {
        Assert.Equal(expected, TicketTransitions.TargetStatus(TicketAction.Assign, current));
        Assert.Equal(expected, TicketTransitions.TargetStatus(TicketAction.Take, current));
    }

    [Fact]
    public void Closed_ticket_can_be_reopened_only_inside_the_window()
    {
        var closedAt = Now.AddDays(-7);

        Assert.True(TicketTransitions.Can(TicketAction.Reopen, State(TicketStatus.Closed, closedAt: closedAt)).Allowed);

        var late = TicketTransitions.Can(TicketAction.Reopen, State(TicketStatus.Closed, closedAt: closedAt.AddSeconds(-1)));
        Assert.False(late.Allowed);
        Assert.Equal("reopen-window", late.Code);

        Assert.False(TicketTransitions.Can(TicketAction.Reopen, State(TicketStatus.Closed, closedAt: null)).Allowed);
        Assert.True(TicketTransitions.Can(TicketAction.Reopen, State(TicketStatus.Closed, closedAt: Now.AddDays(-20), window: 30)).Allowed);
    }

    [Theory]
    [InlineData(TicketLevel.High, TicketLevel.High, TicketPriority.Critical)]
    [InlineData(TicketLevel.High, TicketLevel.Medium, TicketPriority.High)]
    [InlineData(TicketLevel.Medium, TicketLevel.High, TicketPriority.High)]
    [InlineData(TicketLevel.High, TicketLevel.Low, TicketPriority.Normal)]
    [InlineData(TicketLevel.Medium, TicketLevel.Medium, TicketPriority.Normal)]
    [InlineData(TicketLevel.Low, TicketLevel.High, TicketPriority.Normal)]
    [InlineData(TicketLevel.Medium, TicketLevel.Low, TicketPriority.Low)]
    [InlineData(TicketLevel.Low, TicketLevel.Medium, TicketPriority.Low)]
    [InlineData(TicketLevel.Low, TicketLevel.Low, TicketPriority.Low)]
    public void Impact_and_urgency_decide_the_priority(string impact, string urgency, string expected)
    {
        Assert.Equal(expected, PriorityMatrix.Compute(impact, urgency));
    }

    [Fact]
    public void Priority_matrix_is_defined_for_every_combination_and_is_symmetric_in_effect()
    {
        foreach (var impact in TicketLevel.All)
        {
            foreach (var urgency in TicketLevel.All)
            {
                Assert.Contains(PriorityMatrix.Compute(impact, urgency), TicketPriority.All);
                Assert.Equal(PriorityMatrix.Compute(impact, urgency), PriorityMatrix.Compute(urgency, impact));
            }
        }
    }

    [Fact]
    public void Resolution_requires_a_code_and_a_note()
    {
        var errors = ResolutionRules.Validate(null, "  ", null, null, isComplaint: false, TicketPolicySettings.Default);

        Assert.Equal(["resolutionCode", "resolutionNote"], errors.Select(e => e.Field));
        Assert.Empty(ResolutionRules.Validate("Resolved", "Sorun giderildi.", null, null, isComplaint: false, TicketPolicySettings.Default));
    }

    [Fact]
    public void Complaint_resolution_requires_root_cause_and_corrective_action_unless_the_policy_relaxes_it()
    {
        var strict = ResolutionRules.Validate("Resolved", "Telafi edildi.", null, null, isComplaint: true, TicketPolicySettings.Default);
        Assert.Equal(["rootCause", "correctiveAction"], strict.Select(e => e.Field));

        Assert.Empty(ResolutionRules.Validate("Resolved", "Telafi edildi.", "Paketleme hatası", "Süreç düzeltildi", isComplaint: true, TicketPolicySettings.Default));
        Assert.Empty(ResolutionRules.Validate("Resolved", "Telafi edildi.", null, null, isComplaint: true, new TicketPolicySettings(7, RequireRootCauseForComplaint: false)));
    }

    [Theory]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, false, DeliveryStatus.Queued, true)]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, false, DeliveryStatus.Sent, true)]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, false, DeliveryStatus.Recorded, true)]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, true, DeliveryStatus.Queued, false)]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, false, DeliveryStatus.Failed, false)]
    [InlineData(MessageVisibility.Public, MessageDirection.Outbound, false, DeliveryStatus.NotRequired, false)]
    [InlineData(MessageVisibility.Public, MessageDirection.Inbound, false, DeliveryStatus.Recorded, false)]
    [InlineData(MessageVisibility.Internal, MessageDirection.Note, false, DeliveryStatus.NotRequired, false)]
    public void First_response_counts_only_accepted_human_replies_to_the_customer(
        string visibility, string direction, bool automatic, string delivery, bool counts)
    {
        Assert.Equal(counts, FirstResponseRules.Counts(visibility, direction, automatic, delivery));
    }

    [Fact]
    public void Ticket_numbers_are_unambiguous_and_well_formed()
    {
        var number = TicketNumbers.New(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Matches("^TK-20261102-[A-HJ-NP-Z2-9]{6}$", number);
        Assert.NotEqual(number, TicketNumbers.New(DateTimeOffset.UtcNow));
    }
}
