using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 2b: tarama tabanlı tetikleyiciler (zamanlanmış, tarihe bağlı, hareketsizlik). Kayıt kaynağı test modülündedir
/// (<see cref="AutomationTestModule.Source"/>); veritabanı, Row-Level Security, tenant izolasyonu, API izinleri, imleç, tekilleştirme ve
/// çalıştırıcı gerçektir. Seçenekler (tur sınırı, bekleyen sınırı) bu sınıfa özel konakta değiştirilir ve her testte varsayılana döner.
/// </summary>
[Collection(AutomationTestCollection.Name)]
public class AutomationScanTests(AutomationFixture fixture) : IClassFixture<AutomationFixture>, IDisposable
{
    private const string Item = AutomationTestModule.ItemEntity;

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, HttpClient Client);

    private static AutomationActionStep Note() => new("note", AutomationTestModule.NoteAction, new JsonObject { ["text"] = "Tarama notu" });

    private static AutomationDefinition Scheduled(ConditionNode? condition = null) => new(new ScheduledTrigger("00:00"), condition, [Note()]);

    private static ConditionNode Open() => new ConditionLeaf($"{Item}.status", AutomationOperator.Eq, ["open"]);

    public void Dispose()
    {
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;
        options.ScanRecordsPerTenantPerRound = 500;
        options.ScanBacklogLimit = 1000;
        options.ScanPageSize = 200;
        AutomationTestModule.Source.Fail = null;
    }

    [Fact]
    public async Task A_scheduled_rule_scans_every_record_and_runs_only_where_the_condition_holds()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled(Open()));
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A");
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "B");
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "C");

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(3, round.Scanned);
        Assert.Equal(3, round.Evaluated);
        Assert.Equal(1, round.NotMatched);
        Assert.Equal(2, round.StepsRun);
        Assert.Equal(2, AutomationProbe.CallsOf(setup.Tenant.Id).Count);
        var runs = await RunsAsync(setup.Tenant.Id);
        Assert.All(runs, r => Assert.Equal(AutomationRunOrigin.Scan, r.Origin));
        Assert.All(runs, r => Assert.Null(r.EventId));
        Assert.Equal(2, runs.Count(r => r.Status == AutomationRunStatus.Succeeded));
        Assert.Equal(1, runs.Count(r => r.Status == AutomationRunStatus.NotMatched));
    }

    [Fact]
    public async Task The_second_scan_on_the_same_tenant_day_opens_nothing_and_the_next_day_scans_again()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled(Open()));
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A");

        Assert.Equal(1, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        var callsBefore = AutomationTestModule.Source.Calls;
        Assert.Equal(0, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Equal(callsBefore, AutomationTestModule.Source.Calls); // tamamlanan günün imleci kaynağı yeniden sormaz
        Assert.Equal(1, await CountRunsAsync(setup.Tenant.Id));

        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Equal(2, await CountRunsAsync(setup.Tenant.Id));
        Assert.Equal(2, AutomationProbe.CallsOf(setup.Tenant.Id).Count);
    }

    [Fact]
    public async Task A_date_relative_rule_runs_only_for_records_whose_date_falls_on_the_target_day()
    {
        var setup = await SetUpAsync();
        var today = await TodayAsync(setup.Tenant.Id);
        await PublishRuleAsync(setup, new AutomationDefinition(new DateRelativeTrigger($"{Item}.due", -3), null, [Note()]));
        var match = AutomationRecords.Add(setup.Tenant.Id, Item, "open", "vadesi 3 gün sonra", due: today.AddDays(3));
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "vadesi 2 gün sonra", due: today.AddDays(2));
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "vadesi bugün", due: today);

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Scanned);
        Assert.Equal(match, Assert.Single(await RunsAsync(setup.Tenant.Id)).RecordId);
    }

    [Fact]
    public async Task An_inactivity_rule_runs_only_for_records_without_recent_activity()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, new AutomationDefinition(new InactivityTrigger(AutomationTestModule.ActivitiesRelation, 7), null, [Note()]));
        var stale = AutomationRecords.Add(setup.Tenant.Id, Item, "open", "eski", lastActivity: fixture.Time.GetUtcNow().AddDays(-30));
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "taze", lastActivity: fixture.Time.GetUtcNow().AddDays(-1));

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Scanned);
        Assert.Equal(stale, Assert.Single(await RunsAsync(setup.Tenant.Id)).RecordId);
    }

    [Fact]
    public async Task The_scan_pages_across_rounds_resumes_from_the_cursor_and_never_duplicates_a_run()
    {
        var setup = await SetUpAsync();
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;
        options.ScanRecordsPerTenantPerRound = 2;
        await PublishRuleAsync(setup, Scheduled());
        for (var i = 0; i < 5; i++)
        {
            AutomationRecords.Add(setup.Tenant.Id, Item, "open", $"kayıt {i}");
        }

        var scanned = new List<int>();
        for (var round = 0; round < 4; round++)
        {
            scanned.Add((await RunRoundAsync(setup.Tenant.Id)).Scanned);
        }

        Assert.Equal([2, 2, 1, 0], scanned);
        var runs = await RunsAsync(setup.Tenant.Id);
        Assert.Equal(5, runs.Count);
        Assert.Equal(5, runs.Select(r => r.RecordId).Distinct().Count());
        using var scope = fixture.Factory.OpenScope(setup.Tenant.Id);
        var cursor = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationScanCursors.AsNoTracking().SingleAsync();
        Assert.True(cursor.Completed);
        Assert.Equal(5, cursor.RecordsEnqueued);
    }

    [Fact]
    public async Task A_full_backlog_pauses_the_scan_until_the_runner_catches_up()
    {
        var setup = await SetUpAsync();
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;
        await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "A");

        options.ScanBacklogLimit = 0;
        var paused = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(0, paused.Scanned);
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));

        options.ScanBacklogLimit = 1000;
        Assert.Equal(1, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
    }

    [Fact]
    public async Task One_tenants_scan_never_reads_or_runs_another_tenants_records()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        await PublishRuleAsync(first, Scheduled());
        await PublishRuleAsync(second, Scheduled());
        var firstRecord = AutomationRecords.Add(first.Tenant.Id, Item, "open", "A'nın");
        AutomationRecords.Add(second.Tenant.Id, Item, "open", "B'nin");

        var round = await RunRoundAsync(first.Tenant.Id);

        Assert.Equal(1, round.Scanned);
        Assert.Equal(firstRecord, Assert.Single(await RunsAsync(first.Tenant.Id)).RecordId);
        Assert.Equal(0, await CountRunsAsync(second.Tenant.Id));
        Assert.Single(AutomationProbe.CallsOf(first.Tenant.Id));
        Assert.Empty(AutomationProbe.CallsOf(second.Tenant.Id));
    }

    [Fact]
    public async Task The_company_switch_and_a_closed_rule_stop_scanning()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "A");

        await SetCompanySwitchAsync(setup.Tenant.Id, enabled: false);
        Assert.Equal(0, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));

        await SetCompanySwitchAsync(setup.Tenant.Id, enabled: true);
        var close = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/active", new SetAutomationRuleActiveRequest(false));
        Assert.True(close.IsSuccessStatusCode, await close.Content.ReadAsStringAsync());
        Assert.Equal(0, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_failing_record_source_skips_only_its_rule_without_advancing_the_cursor_and_logs_no_message()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, "open", "A");

        AutomationTestModule.Source.Fail = new InvalidOperationException("kişisel veri: 1234567890");
        var failed = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(0, failed.Scanned);
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));
        Assert.Contains(fixture.Logs.Messages, m => m.Contains("taraması başarısız", StringComparison.Ordinal) && m.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Messages, m => m.Contains("1234567890", StringComparison.Ordinal));

        AutomationTestModule.Source.Fail = null;
        Assert.Equal(1, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
    }

    [Fact]
    public async Task Publishing_scan_triggers_is_allowed_and_domain_event_triggers_still_are_not_but_only_with_the_publish_permission()
    {
        var setup = await SetUpAsync();
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, "Domain olayı kuralı", null, 100, false, null, null,
            JsonSerializer.SerializeToElement(new AutomationDefinition(new DomainEventTrigger("autotest.item.escalated"), null, [Note()]), AutomationJson.Options)));
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var refused = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var scanRule = await PublishRuleAsync(setup, Scheduled());
        Assert.Equal("Active", scanRule.Rule.Status);

        // Yetkisiz kullanıcı (yayın izni yok) tarama kuralını yayınlayamaz.
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.CreateRoleAsync(
            setup.Tenant.Id, [CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, AutomationTestModule.ItemsRead], member);
        var memberClient = await LoginAsync(member);
        var draft = await memberClient.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, "Taslak tarama", null, 100, false, null, null,
            JsonSerializer.SerializeToElement(Scheduled(), AutomationJson.Options)));
        var draftRule = (await draft.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var forbidden = await memberClient.PostAsJsonAsync($"/api/automation/rules/{draftRule.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // --- yardımcılar ---

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        await fixture.SetTestModuleAsync(tenant.Id);
        AutomationProbe.Reset();
        return new Setup(tenant, admin, await LoginAsync(admin));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<AutomationRuleDetailDto> PublishRuleAsync(Setup setup, AutomationDefinition definition)
    {
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        return (await publish.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
    }

    private async Task<AutomationRound> RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return round ?? throw new InvalidOperationException("Tur çalışmadı.");
    }

    private async Task<DateOnly> TodayAsync(Guid tenantId)
    {
        await using var scope = fixture.Extended.Services.GetRequiredService<ITenantScopeFactory>().Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        return (await scope.ServiceProvider.GetRequiredService<ITenantClock>().GetAsync(CancellationToken.None)).Today;
    }

    private async Task<IReadOnlyList<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<int> CountRunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.CountAsync();
    }

    private async Task SetCompanySwitchAsync(Guid tenantId, bool enabled)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var existing = await db.TenantSettings.SingleOrDefaultAsync(s => s.Key == CoreModule.SettingKeys.AutomationEnabled);
        if (existing is not null)
        {
            db.TenantSettings.Remove(existing);
            await db.SaveChangesAsync();
        }

        db.TenantSettings.Add(new TenantSetting(CoreModule.SettingKeys.AutomationEnabled, enabled ? CoreModule.SettingValues.Yes : CoreModule.SettingValues.No));
        await db.SaveChangesAsync();
    }
}
