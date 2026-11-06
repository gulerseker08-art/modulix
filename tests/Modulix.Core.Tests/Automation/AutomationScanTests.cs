using System.Globalization;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationScanPlanTests
{
    // Tenant saat dilimi sabit UTC+3 (Türkiye).
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("tr", TimeSpan.FromHours(3), "tr", "tr");

    private static TenantDay Day(string utc) => new(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture), Zone);

    [Fact]
    public void Scheduled_scan_is_due_from_the_tenant_local_time_of_day()
    {
        var trigger = new ScheduledTrigger("06:00");

        Assert.False(AutomationScanPlan.IsDue(trigger, Day("2026-11-04T02:59:00Z")));
        Assert.True(AutomationScanPlan.IsDue(trigger, Day("2026-11-04T03:00:00Z")));
        Assert.True(AutomationScanPlan.IsDue(trigger, Day("2026-11-04T20:00:00Z")));
    }

    [Fact]
    public void Date_and_inactivity_scans_are_due_in_the_first_round_of_the_day_and_event_triggers_never()
    {
        var day = Day("2026-11-04T00:05:00Z");

        Assert.True(AutomationScanPlan.IsDue(new DateRelativeTrigger(DueDate.Key, -3), day));
        Assert.True(AutomationScanPlan.IsDue(new InactivityTrigger(Activities.Key, 7), day));
        Assert.False(AutomationScanPlan.IsDue(new RecordCreatedTrigger(), day));
        Assert.False(AutomationScanPlan.IsDue(new ManualTrigger(), day));
    }

    [Fact]
    public void Invalid_time_of_day_is_never_due()
    {
        Assert.False(AutomationScanPlan.IsDue(new ScheduledTrigger("yarın"), Day("2026-11-04T12:00:00Z")));
    }

    [Theory]
    [InlineData(-3, "2026-11-07")] // tarihe 3 gün kala: bugün + 3
    [InlineData(0, "2026-11-04")] // tarihinde: bugün
    [InlineData(2, "2026-11-02")] // tarihinden 2 gün sonra: bugün - 2
    public void Date_relative_query_targets_the_day_the_field_must_fall_on(int offset, string expected)
    {
        var query = AutomationScanPlan.QueryFor(new DateRelativeTrigger(DueDate.Key, offset), Entity, Day("2026-11-04T09:00:00Z"), null, 100)!;

        Assert.Equal(AutomationScanKind.DateRelative, query.Kind);
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), query.TargetDate);
        Assert.Equal("dueDate", query.Field); // kayıt değerlerindeki ad, "kayıt türü." öneki olmadan
    }

    [Fact]
    public void Day_boundary_follows_the_tenant_time_zone_not_utc()
    {
        // UTC 22:00 = İstanbul ertesi gün 01:00.
        var query = AutomationScanPlan.QueryFor(new DateRelativeTrigger(DueDate.Key, 0), Entity, Day("2026-11-04T22:00:00Z"), null, 10)!;

        Assert.Equal(new DateOnly(2026, 11, 5), query.TargetDate);
    }

    [Fact]
    public void Inactivity_query_cuts_off_whole_days_before_the_start_of_the_tenant_day()
    {
        var query = AutomationScanPlan.QueryFor(new InactivityTrigger(Activities.Key, 7), Entity, Day("2026-11-04T09:00:00Z"), Guid.Empty, 50)!;

        Assert.Equal(AutomationScanKind.Inactivity, query.Kind);
        Assert.Equal(Activities.Key, query.Relation);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T00:00:00+03:00", CultureInfo.InvariantCulture), query.InactiveBefore);
        Assert.Equal(50, query.PageSize);
    }

    [Fact]
    public void Scheduled_query_asks_for_all_records_and_event_triggers_have_no_query()
    {
        var scheduled = AutomationScanPlan.QueryFor(new ScheduledTrigger(), Entity, Day("2026-11-04T09:00:00Z"), null, 10)!;

        Assert.Equal(AutomationScanKind.Scheduled, scheduled.Kind);
        Assert.Null(AutomationScanPlan.QueryFor(new RecordCreatedTrigger(), Entity, Day("2026-11-04T09:00:00Z"), null, 10));
    }

    [Fact]
    public void Dedupe_key_is_stable_within_a_tenant_day_and_differs_across_days()
    {
        var first = AutomationScanCursor.PeriodOf(new DateOnly(2026, 11, 4));
        var second = AutomationScanCursor.PeriodOf(new DateOnly(2026, 11, 5));
        var version = Guid.NewGuid();
        var record = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        Assert.Equal("2026-11-04", first);
        Assert.Equal(
            AutomationRunKeys.Dedupe(tenant, AutomationScanCursor.DedupeTrigger(first), version, Entity, record),
            AutomationRunKeys.Dedupe(tenant, AutomationScanCursor.DedupeTrigger(first), version, Entity, record));
        Assert.NotEqual(
            AutomationRunKeys.Dedupe(tenant, AutomationScanCursor.DedupeTrigger(first), version, Entity, record),
            AutomationRunKeys.Dedupe(tenant, AutomationScanCursor.DedupeTrigger(second), version, Entity, record));
    }

    [Fact]
    public void Cursor_advances_and_remembers_the_last_record()
    {
        var cursor = AutomationScanCursor.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2026-11-04", DateTimeOffset.UnixEpoch);
        var last = Guid.NewGuid();

        cursor.Advance(last, 3, finished: false, DateTimeOffset.UnixEpoch.AddMinutes(1));
        cursor.Advance(null, 0, finished: true, DateTimeOffset.UnixEpoch.AddMinutes(2));

        Assert.Equal(last, cursor.AfterRecordId);
        Assert.Equal(3, cursor.RecordsEnqueued);
        Assert.True(cursor.Completed);
    }
}

public class AutomationRecordSourceDeclarationTests
{
    private sealed class NotASource;

    private static AutomationCapabilities With(Type? source) => new()
    {
        Entities = [new(Entity, "Fırsat", "sales.deals.read") { RecordSource = source }],
    };

    [Fact]
    public void Entity_with_a_valid_record_source_is_accepted()
    {
        var catalog = Catalog(With(typeof(NoopSource)));

        Assert.Equal(typeof(NoopSource), catalog.FindAutomationEntity(Entity)!.Value.Entity.RecordSource);
    }

    [Fact]
    public void Record_source_must_implement_the_contract_and_be_concrete()
    {
        Assert.Contains("IAutomationRecordSource", Assert.Throws<InvalidOperationException>(() => Catalog(With(typeof(NotASource)))).Message);
        Assert.Throws<InvalidOperationException>(() => Catalog(With(typeof(IAutomationRecordSource))));
    }

    [Fact]
    public void Scan_trigger_is_rejected_for_an_entity_without_a_record_source()
    {
        var catalog = Catalog(With(null));
        var definition = ValidDefinition(trigger: new ScheduledTrigger("06:00"));
        var access = new FakeAccess(["sales.deals.read", WritePermission, SecretPermission], ["sales", "billing"]);

        var issues = Validator(catalog, access).ValidateAsync("sales", Entity, definition, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Contains(issues, i => i.Path == "trigger" && i.Message.Contains("desteklemiyor"));
    }
}
