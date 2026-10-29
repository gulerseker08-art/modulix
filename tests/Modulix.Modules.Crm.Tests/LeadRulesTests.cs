using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Leads;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Lead durum geçişleri, temas kuralı, nitelendirme koşulları ve alan doğrulaması (veritabanı gerektirmez).</summary>
public class LeadRulesTests
{
    [Theory]
    [InlineData(LeadStatus.New, true)]
    [InlineData(LeadStatus.Contacting, true)]
    [InlineData(LeadStatus.Qualified, true)]
    [InlineData(LeadStatus.Nurturing, true)]
    [InlineData(LeadStatus.Disqualified, false)]
    [InlineData(LeadStatus.Converted, false)]
    public void Only_open_leads_can_be_assigned_or_disqualified(string status, bool allowed)
    {
        Assert.Equal(allowed, LeadRules.CanAssign(status) is null);
        Assert.Equal(allowed, LeadRules.CanDisqualify(status) is null);
    }

    [Theory]
    [InlineData(LeadStatus.New, true)]
    [InlineData(LeadStatus.Contacting, true)]
    [InlineData(LeadStatus.Nurturing, true)]
    [InlineData(LeadStatus.Qualified, false)]
    [InlineData(LeadStatus.Disqualified, false)]
    [InlineData(LeadStatus.Converted, false)]
    public void Qualify_is_allowed_only_from_working_states(string status, bool allowed) =>
        Assert.Equal(allowed, LeadRules.CanQualify(status) is null);

    [Theory]
    [InlineData(LeadStatus.Qualified, true)]
    [InlineData(LeadStatus.New, false)]
    [InlineData(LeadStatus.Contacting, false)]
    [InlineData(LeadStatus.Nurturing, false)]
    [InlineData(LeadStatus.Disqualified, false)]
    [InlineData(LeadStatus.Converted, false)]
    public void Convert_is_allowed_only_for_qualified_leads_and_never_twice(string status, bool allowed) =>
        Assert.Equal(allowed, LeadRules.CanConvert(status) is null);

    [Theory]
    [InlineData(LeadStatus.Disqualified, true)]
    [InlineData(LeadStatus.Converted, false)]
    [InlineData(LeadStatus.New, false)]
    public void Only_disqualified_leads_reopen_and_converted_never(string status, bool allowed) =>
        Assert.Equal(allowed, LeadRules.CanReopen(status) is null);

    [Fact]
    public void Reopened_lead_returns_to_contacting_only_if_a_contact_happened()
    {
        Assert.Equal(LeadStatus.New, LeadRules.ReopenedStatus(null));
        Assert.Equal(LeadStatus.Contacting, LeadRules.ReopenedStatus(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("Meeting", null, true, true)]
    [InlineData("Meeting", "Reached", true, true)]
    [InlineData("Meeting", "NoAnswer", true, false)]
    [InlineData("Call", "Reached", true, true)]
    [InlineData("Call", "NoAnswer", true, false)]
    [InlineData("Call", null, true, false)]
    [InlineData("Email", "Sent", true, true)]
    [InlineData("Email", null, true, false)]
    [InlineData("Task", "Reached", true, false)]
    [InlineData("Note", "Sent", true, false)]
    [InlineData("Call", "Reached", false, false)]
    [InlineData("Meeting", null, false, false)]
    public void Only_completed_real_contacts_count_as_first_contact(string type, string? outcome, bool completed, bool expected) =>
        Assert.Equal(expected, LeadRules.CountsAsContact(type, outcome, completed));

    [Fact]
    public void Minimum_rule_requires_need_contact_and_requester()
    {
        var rule = new MinimumQualificationRule();
        var empty = new LeadSnapshot("Konu", null, null, null, null, null, null, null, null, null, null, null, null, null);
        Assert.Equal(3, rule.Check(empty).Count);

        var ok = empty with { Description = "Talep", Email = "a@b.test", LastName = "Kaya" };
        Assert.Empty(rule.Check(ok));

        // Bütçe ve karar verici hiçbir tenant için varsayılan olarak zorunlu değildir.
        Assert.Null(ok.EstimatedBudget);
        Assert.Empty(rule.Check(ok with { EstimatedBudget = null, TargetPurchaseDate = null }));

        // Mevcut kişi hem iletişimi hem talep sahibini karşılar.
        Assert.Empty(rule.Check(empty with { NeedDescription = "x", ContactId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("https://www.Acme.com/iletisim", "acme.com")]
    [InlineData("acme.com", "acme.com")]
    [InlineData("WWW.Acme.com.tr", "acme.com.tr")]
    [InlineData("kelime", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Website_domain_is_extracted_for_matching(string? website, string? expected) =>
        Assert.Equal(expected, LeadNormalization.Domain(website));

    [Fact]
    public void Lead_numbers_are_readable_and_distinct()
    {
        var at = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        var numbers = Enumerable.Range(0, 500).Select(_ => LeadNormalization.NewNumber(at)).ToList();

        Assert.All(numbers, n => Assert.Matches("^LD-20261028-[A-Z2-9]{6}$", n));
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public void Lead_store_requires_subject_and_some_identification_on_create()
    {
        var store = new LeadRecordStore(StoreContext());

        Assert.Equal(["subject"], store.Validate(RecordOperation.Create, new JsonObject { ["lastName"] = "Kaya" }).Select(e => e.Field));
        Assert.Equal(["lastName"], store.Validate(RecordOperation.Create, new JsonObject { ["subject"] = "Konu" }).Select(e => e.Field));
        Assert.Empty(store.Validate(RecordOperation.Create, new JsonObject { ["subject"] = "Konu", ["email"] = "a@b.test" }));
        Assert.Empty(store.Validate(RecordOperation.Create, new JsonObject { ["subject"] = "Konu", ["companyName"] = "Acme" }));
    }

    [Theory]
    [InlineData("email", "gecersiz")]
    [InlineData("phone", "harfli")]
    [InlineData("country", "TUR")]
    [InlineData("website", "bosluk var")]
    [InlineData("estimatedBudget", -5)]
    [InlineData("priority", "Kritik")]
    [InlineData("currency", "TL")]
    [InlineData("targetPurchaseDate", "31.12.2026")]
    public void Invalid_lead_values_are_rejected(string field, object value)
    {
        var values = new JsonObject { ["subject"] = "Konu", ["lastName"] = "Kaya", [field] = JsonValue.Create(value) };

        Assert.Equal([field], new LeadRecordStore(StoreContext()).Validate(RecordOperation.Create, values).Select(e => e.Field));
    }

    [Fact]
    public void Budget_and_target_date_may_stay_empty()
    {
        var values = new JsonObject { ["subject"] = "Konu", ["lastName"] = "Kaya", ["estimatedBudget"] = null, ["targetPurchaseDate"] = null };

        Assert.Empty(new LeadRecordStore(StoreContext()).Validate(RecordOperation.Create, values));
    }

    [Fact]
    public void Gate_grants_only_the_action_permission_for_updates()
    {
        var context = StoreContext();
        var store = new LeadRecordStore(context);
        IRecordStore recordStore = store;

        Assert.Null(recordStore.AnyPermissionFor(RecordOperation.Update));
        Assert.Equal(CrmModule.Permissions.LeadsUpdate, recordStore.PermissionFor(RecordOperation.Update));
        Assert.Equal(CrmModule.Permissions.LeadsCreate, recordStore.PermissionFor(RecordOperation.Create));
        Assert.Equal(CrmModule.Permissions.LeadsDelete, recordStore.PermissionFor(RecordOperation.Delete));

        using (context.LeadGate.Open(CrmModule.Permissions.LeadsAssign))
        {
            Assert.Equal([CrmModule.Permissions.LeadsAssign], recordStore.AnyPermissionFor(RecordOperation.Update));
            Assert.Null(recordStore.AnyPermissionFor(RecordOperation.Read));
        }

        Assert.Null(recordStore.AnyPermissionFor(RecordOperation.Update));
    }

    private static CrmStoreContext StoreContext() => CrmRecordStoreValidationTests.NewContext();
}
