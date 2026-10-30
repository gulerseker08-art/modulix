using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Çok adımlı onay değerlendirmesi (saf): sıralı adımlar, aynı adımda birden çok onaylayan, vekâlet, adsız adım.</summary>
public class QuoteApprovalStepsTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static QuoteApprovalMatch Rule(string name, Guid? approver, int step = 1) => new(Guid.NewGuid(), 1, name, "eşleşti", approver, step);

    private static QuoteApprovalGiven By(Guid actor, params Guid[] onBehalfOf) => new(actor, new HashSet<Guid>(onBehalfOf) { actor });

    [Fact]
    public void Steps_complete_in_order_and_later_step_approvals_do_not_count_early()
    {
        var matches = new[] { Rule("Müdür", A, 1), Rule("Direktör", B, 2) };

        var none = QuoteApprovalEvaluator.Steps(matches, []);
        Assert.Equal([1, 2], none.Select(s => s.Step).ToArray());
        Assert.Equal(1, QuoteApprovalEvaluator.Current(none)!.Step);

        // B önce onaylarsa sayılmaz: 1. adım hâlâ bekliyor.
        var early = QuoteApprovalEvaluator.Steps(matches, [By(B)]);
        Assert.False(early[0].Completed);
        Assert.Empty(early[0].ApprovedBy);

        var first = QuoteApprovalEvaluator.Steps(matches, [By(A)]);
        Assert.True(first[0].Completed);
        Assert.False(first[1].Completed);
        Assert.Equal(2, QuoteApprovalEvaluator.Current(first)!.Step);

        var both = QuoteApprovalEvaluator.Steps(matches, [By(A), By(B)]);
        Assert.All(both, s => Assert.True(s.Completed));
        Assert.Null(QuoteApprovalEvaluator.Current(both));
        Assert.True(QuoteApprovalEvaluator.CompletesAll(matches, [By(A)], By(B)));
        Assert.False(QuoteApprovalEvaluator.CompletesAll(matches, [], By(A)));
    }

    [Fact]
    public void All_named_approvers_in_the_same_step_must_approve_and_anyone_completes_an_unnamed_step()
    {
        var parallel = new[] { Rule("Satış", A, 1), Rule("Finans", B, 1), Rule("Eşik", null, 1) };
        var one = QuoteApprovalEvaluator.Steps(parallel, [By(A)]);
        Assert.False(one[0].Completed);
        Assert.Equal([A], one[0].ApprovedBy);
        Assert.True(QuoteApprovalEvaluator.Steps(parallel, [By(A), By(B)])[0].Completed);

        // Adsız kural adlı kuralla aynı adımdaysa adlılar belirleyicidir; "başkası" ilerletmez.
        Assert.False(QuoteApprovalEvaluator.Steps(parallel, [By(Other)])[0].Completed);
        Assert.False(QuoteApprovalEvaluator.CanDecide(QuoteApprovalEvaluator.Current(one), new HashSet<Guid> { Other }));

        // Yalnızca adsız kural: herhangi bir onay yeter; eşleşme yoksa da tek adsız adım vardır.
        var unnamed = QuoteApprovalEvaluator.Steps([Rule("Eşik", null)], [By(Other)]);
        Assert.True(Assert.Single(unnamed).Completed);
        var threshold = QuoteApprovalEvaluator.Steps([], []);
        Assert.Equal(1, Assert.Single(threshold).Step);
        Assert.True(QuoteApprovalEvaluator.CanDecide(QuoteApprovalEvaluator.Current(threshold), new HashSet<Guid> { Other }));
    }

    [Fact]
    public void A_delegate_counts_on_behalf_of_the_approver()
    {
        var matches = new[] { Rule("Müdür", A, 1), Rule("Direktör", B, 2) };

        // C, A adına vekil: onayı A için sayılır, 1. adım tamamlanır.
        var steps = QuoteApprovalEvaluator.Steps(matches, [By(C, A)]);
        Assert.True(steps[0].Completed);
        Assert.Equal([A], steps[0].ApprovedBy);
        Assert.True(QuoteApprovalEvaluator.CanDecide(QuoteApprovalEvaluator.Current(QuoteApprovalEvaluator.Steps(matches, [])), new HashSet<Guid> { C, A }));

        // C'nin vekâleti yoksa onayı 1. adımı ilerletmez.
        Assert.False(QuoteApprovalEvaluator.Steps(matches, [By(C)])[0].Completed);
    }

    [Fact]
    public void Matching_sorts_by_step_and_history_note_mentions_steps_only_when_there_are_several()
    {
        var rules = new[]
        {
            new QuoteApprovalRuleInfo(Guid.NewGuid(), 2, "İkinci", 0, null, null, 1m, B, 2),
            new QuoteApprovalRuleInfo(Guid.NewGuid(), 1, "Birinci", 5, null, null, 1m, A, 1),
        };
        var matches = QuoteApprovalEvaluator.Match(rules, "TRY", 100m, 0m, 100m);
        Assert.Equal(["Birinci", "İkinci"], matches.Select(m => m.Name).ToArray());
        Assert.Contains("adım 1", QuoteApprovalEvaluator.HistoryNote(matches, null));
        Assert.DoesNotContain("adım", QuoteApprovalEvaluator.HistoryNote(matches.Take(1), null));
    }
}
