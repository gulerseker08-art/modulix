using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024 dilim 5: sözleşmeye bağlı politika seçimi (saf kural).</summary>
public class SlaContractRulesTests
{
    private static readonly Guid Contract = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherContract = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Party = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SlaPolicyCandidate Policy(string name, bool isDefault = false, Guid? contractId = null, Guid? partyId = null, int precedence = 100) =>
        new(Guid.NewGuid(), name, precedence, isDefault, true, null, null, null, partyId, contractId);

    private static SlaTicketFacts Facts(params Guid[] contracts) => new("Support", [], "Normal", Party, contracts);

    [Fact]
    public void A_contract_policy_only_matches_tickets_linked_to_that_contract()
    {
        var general = Policy("Genel", isDefault: true);
        var premium = Policy("Premium", contractId: Contract);
        Assert.Equal("Premium", SlaPolicySelector.Select([general, premium], Facts(Contract))!.Name);
        Assert.Equal("Genel", SlaPolicySelector.Select([general, premium], Facts())!.Name);
        Assert.Equal("Genel", SlaPolicySelector.Select([general, premium], Facts(OtherContract))!.Name);
        Assert.Equal("Genel", SlaPolicySelector.Select([general, premium], new SlaTicketFacts("Support", [], "Normal", Party))!.Name);
    }

    [Fact]
    public void A_contract_constraint_counts_as_a_constraint_and_without_a_default_unlinked_tickets_get_no_policy()
    {
        var byParty = Policy("Müşteri", partyId: Party);
        var byPartyAndContract = Policy("Müşteri + sözleşme", partyId: Party, contractId: Contract);
        Assert.Equal("Müşteri + sözleşme", SlaPolicySelector.Select([byParty, byPartyAndContract], Facts(Contract))!.Name);
        Assert.Equal("Müşteri", SlaPolicySelector.Select([byParty, byPartyAndContract], Facts())!.Name);
        Assert.Null(SlaPolicySelector.Select([Policy("Yalnız sözleşme", contractId: Contract)], Facts()));
    }

    [Fact]
    public void Several_linked_contracts_pick_the_most_specific_then_the_lowest_precedence_deterministically()
    {
        var a = Policy("A sözleşmesi", contractId: Contract, precedence: 50);
        var b = Policy("B sözleşmesi", contractId: OtherContract, precedence: 10);
        var chosen = SlaPolicySelector.Select([a, b], Facts(Contract, OtherContract))!;
        Assert.Equal("B sözleşmesi", chosen.Name);
        Assert.Equal(chosen.Name, SlaPolicySelector.Select([b, a], Facts(OtherContract, Contract))!.Name);
    }
}
