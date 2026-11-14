using Modulix.Core.Contracts.Approvals;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 5: onay kuralı eşleşmesi, süre hesabı, çok adımlı akış, vekâlet ve karar yetkisi (saf; veritabanı gerekmez).</summary>
public class ContractApprovalTests
{
    private static readonly Guid Ayse = Guid.CreateVersion7();
    private static readonly Guid Burak = Guid.CreateVersion7();
    private static readonly Guid Cem = Guid.CreateVersion7();

    private static ContractApprovalRuleInfo Rule(
        string name = "Kural", int step = 1, decimal? gross = null, decimal? discount = null, int? term = null, bool nonStandard = false, string? risk = null,
        string? type = null, string? currency = null, Guid? approver = null) =>
        new(Guid.CreateVersion7(), 3, name, 0, step, type, currency, gross, discount, term, nonStandard, risk, approver);

    private static ContractApprovalFacts Facts(
        decimal gross = 1000m, decimal discount = 0m, int? term = 12, bool indefinite = false, bool nonStandard = false, string risk = "Low", string type = "Service", string currency = "TRY") =>
        new(type, currency, gross, discount, term, indefinite, nonStandard, risk);

    [Fact]
    public void Amount_rule_matches_only_above_the_threshold_and_in_its_currency()
    {
        var rules = new[] { Rule(gross: 50_000m, currency: "TRY") };

        Assert.Empty(ContractApprovalEvaluator.Match(rules, Facts(gross: 50_000m)));
        Assert.Single(ContractApprovalEvaluator.Match(rules, Facts(gross: 50_000.01m)));
        Assert.Empty(ContractApprovalEvaluator.Match(rules, Facts(gross: 90_000m, currency: "USD")));
    }

    [Fact]
    public void Conditions_in_one_rule_are_and_while_separate_rules_are_or()
    {
        var both = Rule("İkisi de", gross: 10_000m, discount: 10m);
        var either = new[] { Rule("Tutar", gross: 10_000m), Rule("İndirim", discount: 10m) };

        Assert.Empty(ContractApprovalEvaluator.Match([both], Facts(gross: 20_000m, discount: 5m)));
        Assert.Single(ContractApprovalEvaluator.Match([both], Facts(gross: 20_000m, discount: 15m)));
        Assert.Equal(["Tutar"], ContractApprovalEvaluator.Match(either, Facts(gross: 20_000m, discount: 5m)).Select(m => m.Name));
        Assert.Equal(2, ContractApprovalEvaluator.Match(either, Facts(gross: 20_000m, discount: 15m)).Count);
    }

    [Fact]
    public void Contract_type_only_narrows_and_is_not_a_condition_by_itself()
    {
        var rules = new[] { Rule(gross: 1m, type: "Framework") };

        Assert.Empty(ContractApprovalEvaluator.Match(rules, Facts(type: "Service")));
        Assert.Single(ContractApprovalEvaluator.Match(rules, Facts(type: "Framework")));
        Assert.False(ContractApprovalEvaluator.HasCriterion(null, null, null, false, null));
        Assert.True(ContractApprovalEvaluator.HasCriterion(null, null, null, true, null));
    }

    [Fact]
    public void Term_rule_treats_indefinite_contracts_as_longest_and_missing_dates_as_not_matching()
    {
        var rules = new[] { Rule(term: 12) };

        Assert.Empty(ContractApprovalEvaluator.Match(rules, Facts(term: 12)));
        Assert.Single(ContractApprovalEvaluator.Match(rules, Facts(term: 13)));
        Assert.Single(ContractApprovalEvaluator.Match(rules, Facts(term: null, indefinite: true)));
        Assert.Empty(ContractApprovalEvaluator.Match(rules, Facts(term: null, indefinite: false)));
    }

    [Fact]
    public void Non_standard_and_risk_indicators_match_by_flag_and_minimum_level()
    {
        var nonStandard = new[] { Rule(nonStandard: true) };
        var risk = new[] { Rule(risk: "Medium") };

        Assert.Empty(ContractApprovalEvaluator.Match(nonStandard, Facts(nonStandard: false)));
        Assert.Single(ContractApprovalEvaluator.Match(nonStandard, Facts(nonStandard: true)));
        Assert.Empty(ContractApprovalEvaluator.Match(risk, Facts(risk: "Low")));
        Assert.Single(ContractApprovalEvaluator.Match(risk, Facts(risk: "Medium")));
        Assert.Single(ContractApprovalEvaluator.Match(risk, Facts(risk: "High")));
    }

    [Fact]
    public void Match_reasons_never_contain_amounts_or_percentages()
    {
        var match = Assert.Single(ContractApprovalEvaluator.Match([Rule(gross: 12_345m, discount: 7m)], Facts(gross: 99_999m, discount: 30m)));

        Assert.DoesNotContain("99", match.Reason);
        Assert.DoesNotContain("12.345", match.Reason);
        Assert.DoesNotContain("30", match.Reason);
        Assert.Equal(3, match.RuleVersion);
    }

    [Theory]
    [InlineData("2026-11-01", "2027-10-31", 12)]
    [InlineData("2026-11-01", "2027-11-01", 13)]
    [InlineData("2026-11-01", "2026-11-30", 1)]
    [InlineData("2027-03-01", "2028-02-29", 12)]
    public void Term_months_counts_calendar_months_inclusive(string start, string end, int expected)
    {
        Assert.Equal(expected, ContractApprovalEvaluator.TermMonths(DateOnly.Parse(start), DateOnly.Parse(end), false));
    }

    [Fact]
    public void Term_months_is_null_for_indefinite_or_incomplete_dates()
    {
        Assert.Null(ContractApprovalEvaluator.TermMonths(new DateOnly(2026, 11, 1), null, false));
        Assert.Null(ContractApprovalEvaluator.TermMonths(new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31), true));
        Assert.Null(ContractApprovalEvaluator.TermMonths(new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 1), false));
    }

    [Fact]
    public void Without_a_matching_rule_there_is_one_unnamed_step_and_any_approver_completes_it()
    {
        var plan = new ContractApprovalPlan([]);
        var steps = ApprovalFlow.Steps(plan.Specs(), []);

        var only = Assert.Single(steps);
        Assert.Empty(only.Approvers);
        Assert.False(only.Completed);
        Assert.True(ApprovalFlow.CompletesAll(plan.Specs(), [], new ApprovalGiven(Ayse, new HashSet<Guid> { Ayse })));
    }

    [Fact]
    public void Steps_run_in_order_and_named_approvers_in_one_step_run_in_parallel()
    {
        var specs = new[] { new ApprovalStepSpec(1, Ayse), new ApprovalStepSpec(1, Burak), new ApprovalStepSpec(2, Cem) };
        static ApprovalGiven By(Guid who) => new(who, new HashSet<Guid> { who });

        var afterAyse = ApprovalFlow.Steps(specs, [By(Ayse)]);
        Assert.False(afterAyse[0].Completed);
        Assert.Equal(1, ApprovalFlow.Current(afterAyse)!.Step);

        var afterBoth = ApprovalFlow.Steps(specs, [By(Ayse), By(Burak)]);
        Assert.True(afterBoth[0].Completed);
        Assert.Equal(2, ApprovalFlow.Current(afterBoth)!.Step);

        Assert.False(ApprovalFlow.CompletesAll(specs, [By(Ayse), By(Burak)], By(Ayse)));
        Assert.True(ApprovalFlow.CompletesAll(specs, [By(Ayse), By(Burak)], By(Cem)));
    }

    [Fact]
    public void A_delegate_counts_as_the_named_approver_on_whose_behalf_the_decision_is_given()
    {
        var specs = new[] { new ApprovalStepSpec(1, Ayse) };
        var viaDelegate = new ApprovalGiven(Burak, new HashSet<Guid> { Burak, Ayse });
        var stranger = new ApprovalGiven(Cem, new HashSet<Guid> { Cem });

        Assert.True(ApprovalFlow.CompletesAll(specs, [], viaDelegate));
        Assert.False(ApprovalFlow.CompletesAll(specs, [], stranger));
        Assert.False(ApprovalFlow.CanDecide(ApprovalFlow.Current(ApprovalFlow.Steps(specs, []))!, new HashSet<Guid> { Cem }));
        Assert.True(ApprovalFlow.CanDecide(ApprovalFlow.Current(ApprovalFlow.Steps(specs, []))!, new HashSet<Guid> { Burak, Ayse }));
    }

    private static ContractApprovalDecision Decision(int step, Guid actor, string kind = ApprovalDecisionKind.Approved, int round = 1, int versionNo = 1, params Guid[] onBehalf)
    {
        var d = new ContractApprovalDecision { VersionNo = versionNo, Round = round, StepNo = step, ActorId = actor, Decision = kind };
        d.OnBehalfOf = string.Join(',', onBehalf.Append(actor));
        return d;
    }

    private static ContractVersion VersionWith(ContractApprovalPlan plan, int round = 1) => new() { VersionNo = 1, ApprovalRound = round, ApprovalPlan = ContractApprovalPlans.Serialize(plan) };

    [Fact]
    public void Decision_is_refused_for_a_user_who_decided_in_an_earlier_step()
    {
        var plan = new ContractApprovalPlan(
        [
            new ContractApprovalMatch(Guid.CreateVersion7(), 1, "Tutar", 1, null, "x"),
            new ContractApprovalMatch(Guid.CreateVersion7(), 1, "Risk", 2, null, "y"),
        ]);
        var state = ContractApprovalEngine.Compute(VersionWith(plan), 1, [Decision(1, Ayse)], [], Ayse);

        var (allowed, reason) = ContractApprovalEngine.CanDecide(state);
        Assert.False(allowed);
        Assert.Contains("önceki bir adımında", reason);
        Assert.True(ContractApprovalEngine.CanDecide(ContractApprovalEngine.Compute(VersionWith(plan), 1, [Decision(1, Ayse)], [], Burak)).Allowed);
    }

    [Fact]
    public void Named_step_refuses_strangers_and_a_second_approval_by_someone_who_already_approved()
    {
        var plan = new ContractApprovalPlan(
        [
            new ContractApprovalMatch(Guid.CreateVersion7(), 1, "A", 1, Ayse, "x"),
            new ContractApprovalMatch(Guid.CreateVersion7(), 1, "B", 1, Burak, "x"),
        ]);

        Assert.False(ContractApprovalEngine.CanDecide(ContractApprovalEngine.Compute(VersionWith(plan), 1, [], [], Cem)).Allowed);
        var stateAfterAyse = ContractApprovalEngine.Compute(VersionWith(plan), 1, [Decision(1, Ayse)], [], Ayse);
        var (allowed, reason) = ContractApprovalEngine.CanDecide(stateAfterAyse);
        Assert.False(allowed);
        Assert.Contains("zaten verildi", reason);
        Assert.True(ContractApprovalEngine.CanDecide(ContractApprovalEngine.Compute(VersionWith(plan), 1, [Decision(1, Ayse)], [], Burak)).Allowed);
    }

    [Fact]
    public void Active_delegation_lets_the_delegate_decide_and_flags_it_as_acting_for_someone_else()
    {
        var plan = new ContractApprovalPlan([new ContractApprovalMatch(Guid.CreateVersion7(), 1, "A", 1, Ayse, "x")]);
        var delegation = new ContractApprovalDelegation { FromUserId = Ayse, ToUserId = Cem, IsActive = true, StartsAt = DateTimeOffset.UtcNow.AddDays(-1) };

        var asDelegate = ContractApprovalEngine.Compute(VersionWith(plan), 1, [], [delegation], Cem);
        Assert.True(ContractApprovalEngine.CanDecide(asDelegate).Allowed);
        Assert.True(asDelegate.ActingAsDelegate);

        var own = ContractApprovalEngine.Compute(VersionWith(plan), 1, [], [delegation], Ayse);
        Assert.False(own.ActingAsDelegate);
        Assert.False(ContractApprovalEngine.CanDecide(ContractApprovalEngine.Compute(VersionWith(plan), 1, [], [], Cem)).Allowed);
    }

    [Fact]
    public void Decisions_of_an_earlier_round_or_version_do_not_count()
    {
        var plan = new ContractApprovalPlan([new ContractApprovalMatch(Guid.CreateVersion7(), 1, "A", 1, Ayse, "x")]);
        var version = VersionWith(plan, round: 2);

        var state = ContractApprovalEngine.Compute(version, 1, [Decision(1, Ayse, round: 1), Decision(1, Ayse, round: 2, versionNo: 0)], [], Burak);

        Assert.Empty(state.Decisions);
        Assert.False(state.Steps[0].Completed);
    }

    [Fact]
    public void Plan_without_a_stored_json_or_with_corrupt_json_falls_back_to_a_single_unnamed_step()
    {
        Assert.Empty(ContractApprovalPlans.Deserialize(null).Matches);
        Assert.Empty(ContractApprovalPlans.Deserialize("{bozuk").Matches);
        Assert.Empty(ContractApprovalPlans.Deserialize("{}").Matches);
        Assert.Empty(ContractApprovalPlans.Deserialize("""{"matches":null,"note":"eski"}""").Specs());

        var plan = new ContractApprovalPlan([new ContractApprovalMatch(Guid.CreateVersion7(), 5, "A", 2, Ayse, "neden")], "not");
        var back = ContractApprovalPlans.Deserialize(ContractApprovalPlans.Serialize(plan));
        Assert.Equal("A", Assert.Single(back.Matches).Name);
        Assert.Equal(2, back.Matches[0].Step);
        Assert.Equal(Ayse, back.Matches[0].ApproverId);
    }

    [Fact]
    public void Return_for_changes_needs_a_reason_and_a_pending_approval()
    {
        Assert.True(ContractTransitions.RequiresReason(ContractAction.ReturnForChanges));
        var pending = new ContractState(PreparationStatus.ReadyToPublish, ApprovalStatus.Pending, SignatureStatus.NotSent, EffectiveStatus.NotInForce, true, true);

        Assert.True(ContractTransitions.Can(ContractAction.ReturnForChanges, pending).Allowed);
        Assert.False(ContractTransitions.Can(ContractAction.ReturnForChanges, pending with { Approval = ApprovalStatus.Approved }).Allowed);
        Assert.Contains(ContractAction.ReturnForChanges, ContractAction.All);
    }
}
