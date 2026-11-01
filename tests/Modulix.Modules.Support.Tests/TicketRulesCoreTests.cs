using Modulix.Modules.Support.Lifecycle;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024 dilim 3: yönetilebilir öncelik matrisi, yönlendirme kuralı seçimi, kategori alan kuralları, mükerrer benzerliği ve birleştirme kuralları.</summary>
public class TicketRulesCoreTests
{
    [Fact]
    public void Tenant_matrix_cells_override_the_built_in_matrix_and_missing_cells_fall_back_to_it()
    {
        var custom = new Dictionary<(string, string), string> { [(TicketLevel.Low, TicketLevel.Low)] = TicketPriority.Critical };

        Assert.Equal(TicketPriority.Critical, PriorityMatrixRules.Compute(custom, TicketLevel.Low, TicketLevel.Low));
        Assert.Equal(PriorityMatrix.Compute(TicketLevel.High, TicketLevel.High), PriorityMatrixRules.Compute(custom, TicketLevel.High, TicketLevel.High));
        Assert.Equal(PriorityMatrix.Compute(TicketLevel.Medium, TicketLevel.Low), PriorityMatrixRules.Compute(new Dictionary<(string, string), string>(), TicketLevel.Medium, TicketLevel.Low));
    }

    [Fact]
    public void Matrix_validation_rejects_unknown_levels_priorities_and_duplicate_cells()
    {
        Assert.Empty(PriorityMatrixRules.Validate([(TicketLevel.Low, TicketLevel.High, TicketPriority.Normal)]));
        Assert.Contains(PriorityMatrixRules.Validate([("Huge", TicketLevel.High, TicketPriority.Normal)]), e => e.Field == "impact");
        Assert.Contains(PriorityMatrixRules.Validate([(TicketLevel.Low, "Soon", TicketPriority.Normal)]), e => e.Field == "urgency");
        Assert.Contains(PriorityMatrixRules.Validate([(TicketLevel.Low, TicketLevel.Low, "Urgent")]), e => e.Field == "priority");
        Assert.Contains(PriorityMatrixRules.Validate([(TicketLevel.Low, TicketLevel.Low, TicketPriority.Low), (TicketLevel.Low, TicketLevel.Low, TicketPriority.High)]), e => e.Field == "cells");
    }

    private static RoutingRule Rule(string name, int order = 100, bool active = true, string? channel = null, string? type = null, Guid? category = null, string? priority = null, Guid? party = null, Guid? queue = null) =>
        new(Guid.NewGuid(), name, order, active, channel, type, category, priority, party, queue ?? Guid.NewGuid(), null);

    [Fact]
    public void Routing_picks_the_first_matching_rule_by_order_then_specificity_then_name_then_id()
    {
        var category = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var party = Guid.NewGuid();
        var facts = new RoutingFacts("Email", "Complaint", [category, parent], "High", party);

        var broad = Rule("Geniş", order: 50);
        var byChannel = Rule("E-posta", order: 10, channel: "Email");
        var byParent = Rule("Ana kategori", order: 10, category: parent);
        var specific = Rule("Özel", order: 10, channel: "Email", type: "Complaint", priority: "High");
        var late = Rule("Geç", order: 200, channel: "Email", type: "Complaint");
        var inactive = Rule("Pasif", order: 1, active: false);
        var mismatch = Rule("Telefon", order: 1, channel: "Phone");

        // En küçük sıra kazanır; eşit sırada daha özel kural; pasif ve uymayan kural aday değildir.
        Assert.Equal(specific.Id, RoutingRuleSelector.Select([broad, byChannel, byParent, specific, late, inactive, mismatch], facts)!.Id);
        Assert.Equal(byChannel.Id, RoutingRuleSelector.Select([broad, byChannel, late], facts)!.Id);
        Assert.Equal(broad.Id, RoutingRuleSelector.Select([broad, late], facts)!.Id);
        Assert.Null(RoutingRuleSelector.Select([inactive, mismatch], facts));
        Assert.Null(RoutingRuleSelector.Select([], facts));
    }

    [Fact]
    public void Routing_ties_resolve_by_name_then_id_and_never_depend_on_input_order()
    {
        var facts = new RoutingFacts("Web", "Support", [], "Normal", null);
        var rules = Enumerable.Range(0, 6).Select(i => Rule($"Kural {i % 3}", order: 10, channel: "Web")).ToList();
        var expected = RoutingRuleSelector.Select(rules, facts)!;

        Assert.Equal("Kural 0", expected.Name);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected.Id, RoutingRuleSelector.Select(rules.OrderBy(_ => Guid.NewGuid()), facts)!.Id);
        }
    }

    [Fact]
    public void Category_required_fields_accept_only_known_fields_and_report_the_missing_ones()
    {
        Assert.Equal(["description", "eventDate"], CategoryFieldRules.Parse(" description, eventDate ,description"));
        Assert.Empty(CategoryFieldRules.Validate("description,expectedOutcome"));
        Assert.NotEmpty(CategoryFieldRules.Validate("description,partyId"));
        Assert.Empty(CategoryFieldRules.Parse(null));

        var filled = new HashSet<string> { "description" };
        Assert.Equal(["eventDate"], CategoryFieldRules.Missing(["description", "eventDate"], filled.Contains));
    }

    [Theory]
    [InlineData("Fatura tutarı yanlış çıktı", "fatura tutarı yanlış", 0.5, true)]
    [InlineData("Teslimat gecikti", "Fatura hatalı", 0.5, false)]
    [InlineData("", "Teslimat gecikti", 0.1, false)]
    public void Subject_similarity_uses_normalised_word_overlap(string a, string b, double threshold, bool similar)
    {
        Assert.Equal(similar, SubjectSimilarity.Score(a, b) >= threshold);
    }

    [Fact]
    public void Similarity_ignores_case_punctuation_short_words_and_turkish_dotted_letters()
    {
        Assert.Equal(1.0, SubjectSimilarity.Score("İADE talebi!!", "iade, TALEBİ"));
        Assert.Equal(0, SubjectSimilarity.Score("ve bu bir", "ile için gibi"));
    }

    [Fact]
    public void Possible_duplicates_need_the_same_applicant_and_a_similar_subject_or_category()
    {
        var category = Guid.NewGuid();
        Assert.True(DuplicateRules.IsPossibleDuplicate("Kargo gelmedi", null, "Kargo gelmedi hâlâ", null, sameApplicant: true));
        Assert.False(DuplicateRules.IsPossibleDuplicate("Kargo gelmedi", null, "Kargo gelmedi hâlâ", null, sameApplicant: false));
        Assert.False(DuplicateRules.IsPossibleDuplicate("Kargo gelmedi", null, "Fatura sorusu", null, sameApplicant: true));
        Assert.True(DuplicateRules.IsPossibleDuplicate("Kargo sorunu", category, "Kargo hasarlı geldi", category, sameApplicant: true));
        Assert.False(DuplicateRules.IsPossibleDuplicate("Kargo sorunu", category, "Fatura", category, sameApplicant: true));
    }

    private static MergeFacts Facts(string status = TicketStatus.InProgress, Guid? party = null, string? email = null, string? phone = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), status, party, email, phone);

    [Fact]
    public void Merge_rules_block_self_final_and_different_customer_merges()
    {
        var party = Guid.NewGuid();
        var source = Facts(party: party);
        var target = Facts(party: party);

        Assert.True(MergeRules.Can(source, target).Allowed);
        Assert.Equal("same-ticket", MergeRules.Can(source, source).Code);
        Assert.Equal("source-closed", MergeRules.Can(Facts(TicketStatus.Resolved, party), target).Code);
        Assert.Equal("source-closed", MergeRules.Can(Facts(TicketStatus.Merged, party), target).Code);
        Assert.Equal("target-closed", MergeRules.Can(source, Facts(TicketStatus.Closed, party)).Code);
        Assert.Equal("target-closed", MergeRules.Can(source, Facts(TicketStatus.Merged, party)).Code);
        Assert.Equal("different-customer", MergeRules.Can(source, Facts(party: Guid.NewGuid())).Code);
        Assert.Equal("different-customer", MergeRules.Can(source, Facts(party: null)).Code);
    }

    [Fact]
    public void Unidentified_applicants_merge_only_when_email_or_phone_match()
    {
        Assert.True(MergeRules.Can(Facts(email: "ali@x.test"), Facts(email: "ALI@x.test")).Allowed);
        Assert.True(MergeRules.Can(Facts(phone: "0532"), Facts(phone: "0532")).Allowed);
        Assert.False(MergeRules.Can(Facts(email: "ali@x.test"), Facts(email: "veli@x.test")).Allowed);
        Assert.False(MergeRules.Can(Facts(), Facts()).Allowed);
    }

    [Fact]
    public void Merged_is_a_final_status_outside_the_working_set_and_has_no_transitions()
    {
        Assert.True(TicketStatus.IsFinal(TicketStatus.Merged));
        Assert.False(TicketStatus.IsWorking(TicketStatus.Merged));
        Assert.Contains(TicketStatus.Merged, TicketStatus.All);
        var state = new TicketState(TicketStatus.Merged, true, null, DateTimeOffset.UtcNow, 7);
        Assert.All(TicketAction.All, action => Assert.False(TicketTransitions.Can(action, state).Allowed, action));
    }
}
