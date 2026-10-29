using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Fırsat durum geçişleri, aşama–durum tutarlılığı, olasılık ve ağırlıklı tutar, ayar çözümleme, alan doğrulaması (veritabanı gerektirmez).</summary>
public class OpportunityRulesTests
{
    [Theory]
    [InlineData(OpportunityStatus.Open, true)]
    [InlineData(OpportunityStatus.OnHold, false)]
    [InlineData(OpportunityStatus.Won, false)]
    [InlineData(OpportunityStatus.Lost, false)]
    [InlineData(OpportunityStatus.Cancelled, false)]
    public void Only_open_opportunities_change_stage_or_close(string status, bool allowed)
    {
        Assert.Equal(allowed, OpportunityRules.CanChangeStage(status) is null);
        Assert.Equal(allowed, OpportunityRules.CanClose(status) is null);
        Assert.Equal(allowed, OpportunityRules.CanHold(status) is null);
    }

    [Theory]
    [InlineData(OpportunityStatus.Open, false)]
    [InlineData(OpportunityStatus.OnHold, false)]
    [InlineData(OpportunityStatus.Won, true)]
    [InlineData(OpportunityStatus.Lost, true)]
    [InlineData(OpportunityStatus.Cancelled, true)]
    public void Only_closed_opportunities_reopen_and_only_held_ones_resume(string status, bool reopens)
    {
        Assert.Equal(reopens, OpportunityRules.CanReopen(status) is null);
        Assert.Equal(status == OpportunityStatus.OnHold, OpportunityRules.CanResume(status) is null);
        Assert.Equal(reopens, OpportunityStatus.IsClosed(status));
        Assert.Equal(!reopens, OpportunityStatus.IsLive(status));
    }

    [Theory]
    [InlineData(OpportunityStatus.Open, "open", true)]
    [InlineData(OpportunityStatus.OnHold, "open", true)]
    [InlineData(OpportunityStatus.Cancelled, "open", true)]
    [InlineData(OpportunityStatus.Won, "won", true)]
    [InlineData(OpportunityStatus.Lost, "lost", true)]
    [InlineData(OpportunityStatus.Won, "open", false)]
    [InlineData(OpportunityStatus.Lost, "won", false)]
    [InlineData(OpportunityStatus.Open, "won", false)]
    [InlineData(OpportunityStatus.Open, "lost", false)]
    [InlineData(OpportunityStatus.Cancelled, "lost", false)]
    public void Status_and_stage_category_must_agree(string status, string category, bool consistent) =>
        Assert.Equal(consistent, OpportunityRules.IsConsistent(status, category));

    [Fact]
    public void Probability_follows_closing_manual_input_and_stage_defaults()
    {
        Assert.Equal(100, OpportunityRules.ProbabilityFor(OpportunityStatus.Won, manual: true, current: 20, stageDefault: 50));
        Assert.Equal(0, OpportunityRules.ProbabilityFor(OpportunityStatus.Lost, false, 80, 50));
        Assert.Equal(0, OpportunityRules.ProbabilityFor(OpportunityStatus.Cancelled, false, 80, 50));
        Assert.Equal(33, OpportunityRules.ProbabilityFor(OpportunityStatus.Open, manual: true, current: 33, stageDefault: 50)); // elle girilen korunur
        Assert.Equal(50, OpportunityRules.ProbabilityFor(OpportunityStatus.Open, manual: false, current: 33, stageDefault: 50));
        Assert.Equal(33, OpportunityRules.ProbabilityFor(OpportunityStatus.Open, manual: false, current: 33, stageDefault: null)); // varsayılan yoksa değişmez
        Assert.Null(OpportunityRules.ProbabilityFor(OpportunityStatus.Open, manual: false, current: null, stageDefault: null));
    }

    [Fact]
    public void Weighted_amount_is_amount_times_probability_and_undefined_when_unknown()
    {
        Assert.Equal(500m, OpportunityRules.Weighted(1_000m, 50));
        Assert.Equal(33m, OpportunityRules.Weighted(100m, 33));
        Assert.Equal(0m, OpportunityRules.Weighted(0m, 80)); // sıfır tutar bilinen tutardır
        Assert.Equal(0m, OpportunityRules.Weighted(1_000m, 0));
        Assert.Null(OpportunityRules.Weighted(null, 50)); // bilinmeyen tutar sıfır değildir
        Assert.Null(OpportunityRules.Weighted(1_000m, null));
        Assert.Equal(12.35m, OpportunityRules.Weighted(24.69m, 50)); // 12.345 kuruşa yarım yukarı yuvarlanır
    }

    [Fact]
    public void Settings_are_parsed_and_malformed_entries_are_skipped()
    {
        var requirements = OpportunityRules.ParseRequirements("Proposal=amount|expectedCloseDate; Negotiation = contactId ;bozuk;=x");
        Assert.Equal(["amount", "expectedCloseDate"], requirements["Proposal"]);
        Assert.Equal(["contactId"], requirements["negotiation"]); // anahtar büyük/küçük harfe duyarsız
        Assert.Equal(2, requirements.Count);
        Assert.Empty(OpportunityRules.ParseRequirements(null));

        var probabilities = OpportunityRules.ParseProbabilities("Lead=10;Proposal=50;Bad=101;Odd=x;Neg=-1");
        Assert.Equal(10, probabilities["lead"]);
        Assert.Equal(50, probabilities["Proposal"]);
        Assert.Equal(2, probabilities.Count);

        var policy = new OpportunityPolicy(requirements, probabilities, 30);
        Assert.Equal(["contactId"], policy.RequirementsOf("Negotiation"));
        Assert.Empty(policy.RequirementsOf("Unknown"));
        Assert.Equal(50, policy.ProbabilityOf("Proposal"));
        Assert.Null(policy.ProbabilityOf("Won"));
    }

    [Fact]
    public void Opportunity_numbers_are_prefixed_by_date_and_do_not_repeat()
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var numbers = Enumerable.Range(0, 200).Select(_ => OpportunityRules.NewNumber(at)).ToList();
        Assert.All(numbers, n => Assert.Matches(@"^OP-20261029-[A-Z2-9]{6}$", n));
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public void Closing_fields_and_status_cannot_be_written_by_generic_calls()
    {
        var fields = new[] { "status", "statusReason", "opportunityNumber", "actualCloseDate", "wonAmount", "lossReason", "winningCompetitorId", "closureCycle" };
        var values = new JsonObject();
        foreach (var field in fields)
        {
            values[field] = "x";
        }

        // Sistem alanları tanınan alanlardır (bilinmeyen alan hatası yok); yazımları depoda iş akışı kapısı kapalıyken yok sayılır.
        var errors = new OpportunityRecordStore(CrmRecordStoreValidationTests.NewContext()).Validate(RecordOperation.Update, values);
        Assert.DoesNotContain(errors, e => e.Message == "Bilinmeyen alan.");
    }
}
