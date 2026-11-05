using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Automation;

namespace Modulix.Modules.Crm.Tests;

/// <summary>
/// "Takip aktivitesi oluştur" aksiyonunun yazma yoluna girmeyen davranışı (ADR-0027, Dilim 3): parametre doğrulaması ve dry-run.
/// Yazma yolu (idempotency, kayıt boru hattı) entegrasyon testlerinde gerçek veritabanıyla sınanır.
/// </summary>
public class CreateFollowUpActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly AutomationCallContext Context = new(AutomationCallerKind.Automation, Guid.Empty);

    private sealed class FixedClock : ITenantClock
    {
        public Task<TenantDay> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new TenantDay(Now, TimeZoneInfo.Utc));
    }

    // Yazma yoluna girmeyen testlerde bağımlılıklar kullanılmaz.
    private static CreateFollowUpAction Action() => new(null!, null!, new FixedClock());

    private static AutomationActionRequest Request(JsonObject parameters, bool withTarget = true, bool dryRun = false) =>
        new(CrmAutomation.FollowUpAction, parameters, withTarget ? new RecordRef(CrmModule.Entities.Lead, Guid.NewGuid()) : null, "anahtar", dryRun);

    [Fact]
    public async Task A_missing_target_or_blank_subject_fails_without_writing()
    {
        var noTarget = await Action().ExecuteAsync(Request(new JsonObject { ["subject"] = "Ara" }, withTarget: false), Context, CancellationToken.None);
        Assert.Equal(AutomationActionStatus.Failed, noTarget.Status);
        Assert.False(noTarget.Retryable);

        var blank = await Action().ExecuteAsync(Request(new JsonObject { ["subject"] = "  " }), Context, CancellationToken.None);
        Assert.Equal(AutomationActionStatus.Failed, blank.Status);
        Assert.Contains("Konu", blank.Summary);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task A_due_date_outside_zero_to_365_days_fails(int days)
    {
        var result = await Action().ExecuteAsync(
            Request(new JsonObject { ["subject"] = "Ara", ["dueInDays"] = days, ["assigneeId"] = Guid.NewGuid().ToString() }), Context, CancellationToken.None);

        Assert.Equal(AutomationActionStatus.Failed, result.Status);
        Assert.Contains("365", result.Summary);
    }

    [Fact]
    public async Task Dry_run_plans_the_follow_up_with_the_tenant_day_and_never_touches_the_pipeline()
    {
        var parameters = new JsonObject { ["subject"] = "Ara", ["dueInDays"] = 3, ["assigneeId"] = Guid.NewGuid().ToString() };

        var result = await Action().ExecuteAsync(Request(parameters, dryRun: true), Context, CancellationToken.None);

        Assert.Equal(AutomationActionStatus.Planned, result.Status);
        Assert.Contains("2026-11-08", result.Summary);
        Assert.DoesNotContain("Ara", result.Summary); // konu metni özete yazılmaz
        Assert.Null(result.Effect);
    }

    [Fact]
    public async Task Days_may_arrive_as_text_and_default_to_one_day()
    {
        var assignee = Guid.NewGuid().ToString();
        var text = await Action().ExecuteAsync(
            Request(new JsonObject { ["subject"] = "Ara", ["dueInDays"] = "5", ["assigneeId"] = assignee }, dryRun: true), Context, CancellationToken.None);
        var missing = await Action().ExecuteAsync(
            Request(new JsonObject { ["subject"] = "Ara", ["assigneeId"] = assignee }, dryRun: true), Context, CancellationToken.None);

        Assert.Contains("2026-11-10", text.Summary);
        Assert.Contains("2026-11-06", missing.Summary);
    }
}

/// <summary>Tarama kaynağının tarih sütunları ile beyan edilen tarih alanları aynı tablodan beslenir; ayrışırsa tarama çalışma anında düşer.</summary>
public class CrmAutomationDateColumnTests
{
    [Fact]
    public void Every_declared_date_field_has_a_scan_column_and_every_scan_column_is_a_declared_date_field()
    {
        foreach (var (entity, columns) in new[]
        {
            (CrmModule.Entities.Lead, CrmAutomation.LeadDateColumns.Keys.ToList()),
            (CrmModule.Entities.Opportunity, CrmAutomation.OpportunityDateColumns.Keys.ToList()),
        })
        {
            var declared = CrmAutomation.Capabilities.Fields
                .Where(f => f.EntityKey == entity && f.Type == AutomationFieldType.Date)
                .Select(f => f.Name)
                .Order()
                .ToList();
            Assert.Equal(declared, columns.Order().ToList());
        }
    }
}
