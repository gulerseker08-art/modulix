using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Infrastructure.Records;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 2: kuralların çalışması. Olay → çalışma satırı (tekilleştirme) → kuralın kimliğiyle değerlendirme → aksiyon adımları
/// (idempotency, yeniden deneme, kira) → geçmiş. Olay, dağıtıcının yaptığı gibi abone doğrudan çağrılarak verilir; kayıtlar test
/// okuyucusundan gelir (gerçek CRM henüz kural beyan etmez, Dilim 3). Veritabanı, Row-Level Security, tenant izolasyonu, API izinleri
/// ve çalıştırıcı gerçektir.
/// </summary>
[Collection(AutomationTestCollection.Name)]
public class AutomationRunTests(AutomationFixture fixture) : IClassFixture<AutomationFixture>
{
    private const string Item = AutomationTestModule.ItemEntity;

    private static readonly string[] AuthorPermissions =
    [
        CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CoreModule.Permissions.PublishAutomation,
        AutomationTestModule.ItemsRead,
    ];

    private static JsonElement Json(AutomationDefinition definition) => JsonSerializer.SerializeToElement(definition, AutomationJson.Options);

    private static AutomationActionStep Note(string id = "note", string? text = null, string? boundField = null) => new(
        id,
        AutomationTestModule.NoteAction,
        new JsonObject { ["text"] = boundField is null ? text ?? "Not" : new JsonObject { ["$field"] = boundField } });

    private static AutomationDefinition ClosedDefinition(params AutomationActionStep[] steps) => new(
        new StatusTransitionTrigger($"{Item}.status", null, "closed"),
        new ConditionLeaf($"{Item}.title", AutomationOperator.Contains, ["Anka"]),
        steps.Length == 0 ? [Note(boundField: $"{Item}.title")] : steps);

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, HttpClient Client);

    [Fact]
    public async Task A_matching_event_runs_the_action_once_with_the_rules_own_narrow_identity()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka Lojistik");

        var run = await DeliverAsync(setup.Tenant.Id, record, "closed", correlation: "iz-1", depth: 1);
        Assert.Equal(AutomationRunStatus.Pending, run.Status);
        Assert.Equal(rule.Published!.Id, run.RuleVersionId);

        var round = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(1, round.Evaluated);
        Assert.Equal(1, round.StepsRun);

        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Succeeded, after.Status);
        Assert.NotNull(after.CompletedAt);
        var step = Assert.Single(await StepsAsync(setup.Tenant.Id, run.Id));
        Assert.Equal(AutomationStepStatus.Succeeded, step.Status);
        Assert.Equal(1, step.Attempts);

        var call = Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Equal("Anka Lojistik", call.Request.Parameters["text"]!.GetValue<string>());
        Assert.Equal(new RecordRef(Item, record), call.Request.Target);
        Assert.Equal(step.IdempotencyKey, call.Request.IdempotencyKey);
        Assert.Equal(AutomationRunKeys.Step(run.DedupeKey, "note"), call.Request.IdempotencyKey);
        Assert.Equal(AutomationCallerKind.Automation, call.Context.Kind);
        Assert.Equal(Guid.Empty, call.Context.UserId);
        Assert.Equal(rule.Published.Id, call.Context.RuleVersionId);

        // Kimlik: yalnızca tanımın gerektirdiği izinler; yönetici yetkileri, kâr marjı gibi kullanılmayan hassas alan izni yok.
        Assert.Equal([AutomationTestModule.ItemsRead], call.Permissions.Order());
        Assert.DoesNotContain(AutomationTestModule.MarginRead, call.Permissions);
        Assert.DoesNotContain(CoreModule.Permissions.ManageAutomation, call.Permissions);
        Assert.Equal(RecordScope.All, call.Scope);

        // Kural kaynaklı değişiklik tetikleyen olaya ve izine bağlanır: derinlik + 1, kaynak arka plan, abonelik core.automation.
        Assert.Equal(CoreModule.Subscriptions.Automation, call.Subscription);
        Assert.Equal("iz-1", call.EventCorrelation);
        Assert.Equal(run.EventId, call.ScopeEventId);
        Assert.Equal(1, call.EventDepth);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_never_runs_the_action_twice()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");

        var first = await DeliverAsync(setup.Tenant.Id, record, "closed");

        // Dağıtıcı at-least-once çalışır: aynı olay aynı zarfla yeniden gelir.
        await DeliverAgainAsync(setup.Tenant.Id, first);
        await RunRoundAsync(setup.Tenant.Id);
        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, await CountRunsAsync(setup.Tenant.Id));
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_failed_condition_ends_as_not_matched_and_the_action_is_never_called()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Başka Firma");

        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.NotMatched);
        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.NotMatched, after.Status);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Empty(await StepsAsync(setup.Tenant.Id, run.Id));
    }

    [Fact]
    public async Task Old_values_come_from_the_events_diff_and_a_missing_diff_never_matches()
    {
        var setup = await SetUpAsync();
        var definition = new AutomationDefinition(
            new StatusTransitionTrigger($"{Item}.status", null, "closed"),
            new ConditionLeaf($"{Item}.status", AutomationOperator.Eq, ["open"]) { Source = AutomationValueSource.Old },
            [Note(text: "Açıktan kapandı")]);
        await PublishRuleAsync(setup, definition);

        var fromOpen = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "A");
        var fromOther = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "B");
        var openRun = await DeliverAsync(setup.Tenant.Id, fromOpen, "closed", oldStatus: "open");
        var otherRun = await DeliverAsync(setup.Tenant.Id, fromOther, "closed", oldStatus: "hold");
        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, openRun.Id)).Status);
        Assert.Equal(AutomationRunStatus.NotMatched, (await RunAsync(setup.Tenant.Id, otherRun.Id)).Status);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_sensitive_field_used_by_the_rule_adds_exactly_its_read_permission_to_the_rule_identity()
    {
        var setup = await SetUpAsync();
        var definition = new AutomationDefinition(
            new RecordCreatedTrigger(),
            new ConditionLeaf($"{Item}.margin", AutomationOperator.Gt, ["10"]),
            [Note(text: "Yüksek marj")]);
        await PublishRuleAsync(setup, definition, extraPermissions: [AutomationTestModule.MarginRead]);
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A", margin: 25);

        await DeliverAsync(setup.Tenant.Id, record, "open", created: true);
        await RunRoundAsync(setup.Tenant.Id);

        var call = Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Equal([AutomationTestModule.ItemsRead, AutomationTestModule.MarginRead], call.Permissions.Order());
    }

    [Fact]
    public async Task A_run_leased_by_another_worker_is_left_alone_until_the_lease_expires()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        // Başka bir işleyici çalışmayı kiralamış (kira henüz sürüyor).
        using (var scope = fixture.Factory.OpenScope(setup.Tenant.Id))
        {
            var until = fixture.Time.GetUtcNow() + AutomationRunner.Lease;
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns
                .Where(r => r.Id == run.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.LeaseToken, Guid.NewGuid()).SetProperty(r => r.LeaseUntil, until));
        }

        var during = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(0, during.Evaluated);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));

        // İşleyici çöktü: kira dolunca çalışma devralınır ve bir kez çalışır.
        fixture.Time.Advance(AutomationRunner.Lease + TimeSpan.FromSeconds(1));
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task Parallel_workers_for_the_same_tenant_never_run_a_step_twice()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        AutomationProbe.Delay = TimeSpan.FromMilliseconds(600);
        var runner = fixture.Extended.Services.GetRequiredService<AutomationRunner>();

        var rounds = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => runner.RunTenantAsync(setup.Tenant.Id, CancellationToken.None)));

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_with_backoff_and_the_same_idempotency_key()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        AutomationProbe.Behavior = _ => AutomationProbe.Attempts(setup.Tenant.Id) < 3
            ? new AutomationActionResult(AutomationActionStatus.Failed, "Geçici hata") { Retryable = true }
            : new AutomationActionResult(AutomationActionStatus.Succeeded, "Tamam");

        var first = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(1, first.Retrying);
        var waiting = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Running, waiting.Status);
        Assert.Equal(fixture.Time.GetUtcNow() + AutomationRetryPolicy.Delay(1), waiting.NextAttemptAt);

        // Süre dolmadan tekrar çalıştırmak aksiyonu yeniden çağırmaz.
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));

        fixture.Time.Advance(AutomationRetryPolicy.Delay(1) + TimeSpan.FromSeconds(1));
        await RunRoundAsync(setup.Tenant.Id);
        fixture.Time.Advance(AutomationRetryPolicy.Delay(2) + TimeSpan.FromSeconds(1));
        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        var calls = AutomationProbe.CallsOf(setup.Tenant.Id);
        Assert.Equal(3, calls.Count);
        Assert.Single(calls.Select(c => c.Request.IdempotencyKey).Distinct());
        Assert.Equal(3, Assert.Single(await StepsAsync(setup.Tenant.Id, run.Id)).Attempts);
    }

    [Fact]
    public async Task A_step_that_keeps_failing_ends_the_run_as_failed_and_cancels_the_rest()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition(Note("first", "Bir"), Note("second", "İki")));
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        AutomationProbe.Behavior = request => request.Parameters["text"]!.GetValue<string>() == "Bir"
            ? new AutomationActionResult(AutomationActionStatus.Failed, "Kalıcı hata") { Retryable = false }
            : new AutomationActionResult(AutomationActionStatus.Succeeded, "Tamam");

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Failed);
        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Failed, after.Status);
        Assert.Contains("autotest.note.add", after.Note);
        var steps = await StepsAsync(setup.Tenant.Id, run.Id);
        Assert.Equal([AutomationStepStatus.Failed, AutomationStepStatus.Cancelled], steps.Select(s => s.Status));
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task Retry_attempts_are_capped_and_an_authorized_manual_retry_resumes_without_repeating_finished_steps()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition(Note("first", "Bir"), Note("second", "İki")));
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        AutomationProbe.Behavior = request => request.Parameters["text"]!.GetValue<string>() == "İki"
            ? new AutomationActionResult(AutomationActionStatus.Failed, "Servis yok") { Retryable = true }
            : new AutomationActionResult(AutomationActionStatus.Succeeded, "Tamam");

        for (var attempt = 1; attempt <= AutomationRetryPolicy.MaxAttempts; attempt++)
        {
            await RunRoundAsync(setup.Tenant.Id);
            fixture.Time.Advance(AutomationRetryPolicy.Delay(attempt) + TimeSpan.FromSeconds(1));
        }

        var failed = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Failed, failed.Status);
        var steps = await StepsAsync(setup.Tenant.Id, run.Id);
        Assert.Equal([AutomationStepStatus.Succeeded, AutomationStepStatus.Failed], steps.Select(s => s.Status));
        Assert.Equal(AutomationRetryPolicy.MaxAttempts, steps[1].Attempts);
        var callsBefore = AutomationProbe.CallsOf(setup.Tenant.Id).Count;
        Assert.Equal(1 + AutomationRetryPolicy.MaxAttempts, callsBefore);

        // Servis düzeldi; yetkili yeniden dener: başarılı birinci adım tekrar çalışmaz, ikincisi aynı anahtarla yeniden gönderilir.
        AutomationProbe.Behavior = null;
        using var freshClient = await LoginAsync(setup.Admin);
        var retried = await freshClient.PostAsync($"/api/automation/runs/{run.Id}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        var calls = AutomationProbe.CallsOf(setup.Tenant.Id);
        Assert.Equal(callsBefore + 1, calls.Count);
        Assert.Equal(steps[1].IdempotencyKey, calls[^1].Request.IdempotencyKey);
    }

    [Fact]
    public async Task An_exception_in_the_action_is_a_retryable_failure_and_its_message_is_never_stored()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        AutomationProbe.Behavior = _ => throw new InvalidOperationException("Müşteri Anka Lojistik'in vergi no 1234567890 hatalı");

        await RunRoundAsync(setup.Tenant.Id);

        var step = Assert.Single(await StepsAsync(setup.Tenant.Id, run.Id));
        Assert.Equal(AutomationStepStatus.Pending, step.Status);
        Assert.Equal(1, step.Attempts);
        Assert.Contains("InvalidOperationException", step.Summary);
        Assert.DoesNotContain("1234567890", step.Summary);
        Assert.DoesNotContain(fixture.Logs.Messages, m => m.Contains("1234567890", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_company_switch_cancels_queued_runs_and_nothing_runs_while_it_is_off()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        await SetCompanySwitchAsync(setup.Tenant.Id, enabled: false);
        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Cancelled);
        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Cancelled, after.Status);
        Assert.Contains("firma ayarıyla", after.Note);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));

        // Anahtar kapalıyken gelen yeni olay da çalışmaz (çalışma satırı yazılır, tur iptal eder).
        var second = await DeliverAsync(setup.Tenant.Id, record, "closed");
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Cancelled, (await RunAsync(setup.Tenant.Id, second.Id)).Status);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_trial_company_is_served_like_an_active_one_and_a_suspended_company_is_skipped()
    {
        // ADR-0028: erişim kararı TenantStatusRules.Operational'dadır (Active ve Trial); deneme süresindeki firmanın kuralları da çalışır.
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var runner = fixture.Extended.Services.GetRequiredService<AutomationRunner>();

        await SetTenantStatusAsync(setup.Tenant.Id, Modulix.Core.Platform.TenantStatus.Trial);
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        Assert.Contains(setup.Tenant.Id, await runner.TenantIdsAsync(CancellationToken.None));
        var round = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(1, round.StepsRun);
        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));

        // Askıdaki firma listede yoktur; doğrudan çağrılan tur da çalışmaz, kuyruktaki çalışma olduğu gibi bekler.
        var queued = await DeliverAsync(setup.Tenant.Id, record, "closed");
        await SetTenantStatusAsync(setup.Tenant.Id, Modulix.Core.Platform.TenantStatus.Suspended);
        Assert.DoesNotContain(setup.Tenant.Id, await runner.TenantIdsAsync(CancellationToken.None));
        var (skippedRound, _) = await runner.RunTenantAsync(setup.Tenant.Id, CancellationToken.None);
        Assert.Null(skippedRound);
        Assert.Equal(AutomationRunStatus.Pending, (await RunAsync(setup.Tenant.Id, queued.Id)).Status);
        Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    private async Task SetTenantStatusAsync(Guid tenantId, Modulix.Core.Platform.TenantStatus status)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Tenants
            .Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status));
    }

    [Fact]
    public async Task The_platform_switch_pauses_everything_without_cancelling_and_work_resumes_when_it_is_back()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;

        options.Enabled = false;
        try
        {
            var (round, _) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(setup.Tenant.Id, CancellationToken.None);
            Assert.Null(round);
            Assert.Equal(AutomationRunStatus.Pending, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
            Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
        }
        finally
        {
            options.Enabled = true;
        }

        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
    }

    [Fact]
    public async Task Deactivating_the_rule_cancels_its_queued_runs()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        var off = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/active", new SetAutomationRuleActiveRequest(false));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        await RunRoundAsync(setup.Tenant.Id);

        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Cancelled, after.Status);
        Assert.Contains("Kural kapatıldığı", after.Note);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_run_stays_bound_to_the_version_it_started_with_even_if_the_rule_is_republished()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, ClosedDefinition(Note(text: "Eski sürüm")));
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        var edited = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}", new UpdateAutomationRuleRequest(
            "Yeni", null, 100, false, null, null, Json(ClosedDefinition(Note(text: "Yeni sürüm"))), null));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var published = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        Assert.Equal("Eski sürüm", Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id)).Request.Parameters["text"]!.GetValue<string>());

        // Yeni olay yeni sürümle çalışır.
        var next = await DeliverAsync(setup.Tenant.Id, record, "closed");
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(2, await VersionNoAsync(setup.Tenant.Id, (await RunAsync(setup.Tenant.Id, next.Id)).RuleVersionId));
        Assert.Equal("Yeni sürüm", AutomationProbe.CallsOf(setup.Tenant.Id)[^1].Request.Parameters["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_module_closed_after_the_run_was_queued_fails_the_run_without_calling_the_action()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        await fixture.SetTestModuleAsync(setup.Tenant.Id, enabled: false);
        await RunRoundAsync(setup.Tenant.Id);

        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Failed, after.Status);
        Assert.Contains("kapalı", after.Note);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_deleted_record_cancels_the_run()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var run = await DeliverAsync(setup.Tenant.Id, Guid.NewGuid(), "closed");

        await RunRoundAsync(setup.Tenant.Id);

        var after = await RunAsync(setup.Tenant.Id, run.Id);
        Assert.Equal(AutomationRunStatus.Cancelled, after.Status);
        Assert.Contains("Kayıt bulunamadı", after.Note);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_required_parameter_that_is_empty_on_the_record_skips_the_step_instead_of_failing()
    {
        var setup = await SetUpAsync();
        var definition = new AutomationDefinition(
            new StatusTransitionTrigger($"{Item}.status", null, "closed"), null, [Note(boundField: $"{Item}.title")]);
        await PublishRuleAsync(setup, definition);
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: null);
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
        var step = Assert.Single(await StepsAsync(setup.Tenant.Id, run.Id));
        Assert.Equal(AutomationStepStatus.Skipped, step.Status);
        Assert.Contains("Not", step.Summary);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
    }

    [Fact]
    public async Task One_tenants_runs_are_invisible_and_untouchable_for_another_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        await PublishRuleAsync(a, ClosedDefinition());
        await PublishRuleAsync(b, ClosedDefinition());
        var recordA = AutomationRecords.Add(a.Tenant.Id, Item, status: "closed", title: "Anka");
        var runA = await DeliverAsync(a.Tenant.Id, recordA, "closed");

        // B'nin turu A'nın çalışmasını işlemez.
        var roundB = await RunRoundAsync(b.Tenant.Id);
        Assert.Equal(0, roundB.Evaluated);
        Assert.Equal(AutomationRunStatus.Pending, (await RunAsync(a.Tenant.Id, runA.Id)).Status);
        Assert.Empty(AutomationProbe.CallsOf(b.Tenant.Id));

        await RunRoundAsync(a.Tenant.Id);

        // API: B ne listede görür ne okur ne dokunur.
        var list = await b.Client.GetFromJsonAsync<AutomationRunPageDto>("/api/automation/runs");
        Assert.Empty(list!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/automation/runs/{runA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsync($"/api/automation/runs/{runA.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsync($"/api/automation/runs/{runA.Id}/cancel", null)).StatusCode);
        var ruleOfA = (await a.Client.GetFromJsonAsync<AutomationRunPageDto>("/api/automation/runs"))!.Items.Single().RuleId;
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/automation/rules/{ruleOfA}/run", new RunAutomationRuleRequest(recordA))).StatusCode);

        // Row-Level Security: B'nin oturumunda (ya da oturumsuz) satır yoktur.
        const string RunSql = "SELECT COUNT(*) FROM [core].[AutomationRuns] WHERE [Id] = @id";
        const string StepSql = "SELECT COUNT(*) FROM [core].[AutomationRunSteps] WHERE [RunId] = @id";
        var id = ("@id", (object)runA.Id);
        Assert.Equal(1, await fixture.Factory.ScalarAsync<int>(a.Tenant.Id, RunSql, id));
        Assert.Equal(1, await fixture.Factory.ScalarAsync<int>(a.Tenant.Id, StepSql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(b.Tenant.Id, RunSql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(b.Tenant.Id, StepSql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(null, RunSql, id));
    }

    [Fact]
    public async Task Run_history_and_interventions_need_their_own_permissions()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Başka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");
        await RunRoundAsync(setup.Tenant.Id);

        var reader = await fixture.Factory.CreateUserAsync(setup.Tenant);
        var operatorUser = await fixture.Factory.CreateUserAsync(setup.Tenant);
        var ruleManager = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.CreateRoleAsync(setup.Tenant.Id, [CoreModule.Permissions.ReadAutomationLogs], reader);
        await fixture.CreateRoleAsync(setup.Tenant.Id, [CoreModule.Permissions.ReadAutomationLogs, CoreModule.Permissions.RunAutomation, AutomationTestModule.ItemsRead], operatorUser);
        await fixture.CreateRoleAsync(setup.Tenant.Id, AuthorPermissions, ruleManager);

        using var readerClient = await LoginAsync(reader);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/automation/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync($"/api/automation/runs/{run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsync($"/api/automation/runs/{run.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsync($"/api/automation/runs/{run.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/run", new RunAutomationRuleRequest(record))).StatusCode);

        // Kural yönetmek (yayın dahil) geçmişi görmeyi ya da çalıştırmayı vermez.
        using var managerClient = await LoginAsync(ruleManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.GetAsync("/api/automation/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.GetAsync($"/api/automation/runs/{run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsync($"/api/automation/runs/{run.Id}/retry", null)).StatusCode);

        // Çalıştırma izni olan, geçmiş izni olmadan da müdahale edemez; ikisi birlikte olunca bitmiş çalışma hakkında anlamlı hata alır.
        using var operatorClient = await LoginAsync(operatorUser);
        var cancelFinished = await operatorClient.PostAsync($"/api/automation/runs/{run.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, cancelFinished.StatusCode);
        var retryNotFailed = await operatorClient.PostAsync($"/api/automation/runs/{run.Id}/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, retryNotFailed.StatusCode);
    }

    [Fact]
    public async Task A_queued_run_can_be_cancelled_and_never_executes()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, ClosedDefinition());
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var run = await DeliverAsync(setup.Tenant.Id, record, "closed");

        var cancelled = await setup.Client.PostAsync($"/api/automation/runs/{run.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var dto = await cancelled.Content.ReadFromJsonAsync<AutomationRunDetailDto>();
        Assert.Equal("Cancelled", dto!.Run.Status);

        await RunRoundAsync(setup.Tenant.Id);
        Assert.Empty(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Equal(AutomationRunStatus.Cancelled, (await RunAsync(setup.Tenant.Id, run.Id)).Status);
    }

    [Fact]
    public async Task A_manual_rule_runs_on_a_record_the_caller_can_see_once_per_day_and_the_intervention_is_audited()
    {
        var setup = await SetUpAsync();
        var definition = new AutomationDefinition(new ManualTrigger(), null, [Note(text: "Elle")]);
        var rule = await PublishRuleAsync(setup, definition);
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Anka");

        var response = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/run", new RunAutomationRuleRequest(record));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AutomationRunDetailDto>();
        Assert.Equal("Manual", created!.Run.Origin);
        Assert.Equal("Pending", created.Run.Status);

        // İkinci tıklama aynı gün yeni etki üretmez: 409 ve mevcut çalışmanın kimliği.
        var again = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/run", new RunAutomationRuleRequest(record));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(created.Run.Id.ToString(), await again.Content.ReadAsStringAsync());

        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Succeeded, (await RunAsync(setup.Tenant.Id, created.Run.Id)).Status);
        var call = Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Equal("Elle", call.Request.Parameters["text"]!.GetValue<string>());
        Assert.Null(call.Context.CausationId);

        using var scope = fixture.Factory.OpenScope(setup.Tenant.Id);
        var audit = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.AutomationRule && a.RecordId == rule.Rule.Id && a.Changes.Contains("\"run\""))
            .ToListAsync();
        Assert.Single(audit);
    }

    [Fact]
    public async Task Manual_run_is_refused_for_event_rules_unknown_records_and_unknown_rules()
    {
        var setup = await SetUpAsync();
        var eventRule = await PublishRuleAsync(setup, ClosedDefinition());
        var manualRule = await PublishRuleAsync(setup, new AutomationDefinition(new ManualTrigger(), null, [Note()]));
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Anka");

        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.PostAsJsonAsync($"/api/automation/rules/{eventRule.Rule.Id}/run", new RunAutomationRuleRequest(record))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.PostAsJsonAsync($"/api/automation/rules/{manualRule.Rule.Id}/run", new RunAutomationRuleRequest(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Client.PostAsJsonAsync($"/api/automation/rules/{Guid.NewGuid()}/run", new RunAutomationRuleRequest(record))).StatusCode);

        // Kapalı kural elle de çalışmaz.
        await setup.Client.PutAsJsonAsync($"/api/automation/rules/{manualRule.Rule.Id}/active", new SetAutomationRuleActiveRequest(false));
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.PostAsJsonAsync($"/api/automation/rules/{manualRule.Rule.Id}/run", new RunAutomationRuleRequest(record))).StatusCode);
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Run_list_filters_by_rule_status_and_record_and_validates_the_status()
    {
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, ClosedDefinition());
        var matching = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Anka");
        var other = AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Başka");
        await DeliverAsync(setup.Tenant.Id, matching, "closed");
        await DeliverAsync(setup.Tenant.Id, other, "closed");
        await RunRoundAsync(setup.Tenant.Id);

        var all = await setup.Client.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?ruleId={rule.Rule.Id}");
        Assert.Equal(2, all!.TotalCount);
        var succeeded = await setup.Client.GetFromJsonAsync<AutomationRunPageDto>("/api/automation/runs?status=Succeeded");
        Assert.Equal(matching, Assert.Single(succeeded!.Items).RecordId);
        var notMatched = await setup.Client.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?status=NotMatched&recordId={other}");
        Assert.Equal(other, Assert.Single(notMatched!.Items).RecordId);
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Client.GetAsync("/api/automation/runs?status=Bogus")).StatusCode);

        var detail = await setup.Client.GetFromJsonAsync<AutomationRunDetailDto>($"/api/automation/runs/{succeeded.Items[0].Id}");
        var step = Assert.Single(detail!.Steps);
        Assert.Equal("Succeeded", step.Status);
        Assert.Equal("autotest.note.add", step.Action);
    }

    [Fact]
    public async Task The_active_rule_limit_blocks_opening_one_more_rule_but_not_closing_or_other_tenants()
    {
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;
        options.MaxActiveRulesPerTenant = 2;
        try
        {
            var setup = await SetUpAsync();
            var first = await PublishRuleAsync(setup, ClosedDefinition());
            await PublishRuleAsync(setup, ClosedDefinition());
            var third = await PublishRuleAsync(setup, ClosedDefinition(), activate: false);

            // Üçüncü kural açılamaz (yayınla-aç yolu da aynı sınırı uygular); kapatmak her zaman serbesttir.
            var blocked = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{third.Rule.Id}/active", new SetAutomationRuleActiveRequest(true));
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("(2)", await blocked.Content.ReadAsStringAsync());
            var closed = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{first.Rule.Id}/active", new SetAutomationRuleActiveRequest(false));
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            var opened = await setup.Client.PutAsJsonAsync($"/api/automation/rules/{third.Rule.Id}/active", new SetAutomationRuleActiveRequest(true));
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

            // Sınır tenant başınadır: başka firmanın kuralları sayılmaz.
            var other = await SetUpAsync();
            Assert.Equal("Active", (await PublishRuleAsync(other, ClosedDefinition())).Rule.Status);
        }
        finally
        {
            options.MaxActiveRulesPerTenant = AutomationLimits.MaxActiveRulesPerTenant;
        }
    }

    [Fact]
    public async Task A_rule_with_a_domain_event_trigger_is_published_and_runs_only_on_its_declared_business_event()
    {
        // ADR-0039: modülün beyan ettiği iş olayı (autotest.item.escalated) tetikleyici olarak yayınlanabilir.
        var setup = await SetUpAsync();
        var rule = await PublishRuleAsync(setup, new AutomationDefinition(new DomainEventTrigger("autotest.item.escalated"), null, [Note(text: "Yükseltildi")]));
        var record = AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Anka");

        // Kayıt güncelleme olayı bu kuralı tetiklemez; iş olayı tek çalışma açar ve aksiyon bir kez çalışır.
        await DeliverEnvelopeAsync(setup.Tenant.Id, Envelope(setup.Tenant.Id, record, DomainEventTypes.RecordUpdated, new JsonObject { ["status"] = new JsonObject { ["old"] = "open", ["new"] = "hold" } }));
        Assert.Equal(0, await CountRunsAsync(setup.Tenant.Id));

        var escalated = Envelope(setup.Tenant.Id, record, "autotest.item.escalated", new JsonObject { ["status"] = "hold", ["title"] = "Anka" });
        await DeliverEnvelopeAsync(setup.Tenant.Id, escalated);
        await DeliverEnvelopeAsync(setup.Tenant.Id, escalated);
        Assert.Equal(1, await CountRunsAsync(setup.Tenant.Id));
        await RunRoundAsync(setup.Tenant.Id);

        var call = Assert.Single(AutomationProbe.CallsOf(setup.Tenant.Id));
        Assert.Equal("Yükseltildi", call.Request.Parameters["text"]!.GetValue<string>());
        Assert.Equal(rule.Published!.Id, call.Context.RuleVersionId);

        // Kayıt olayı anahtarı iş olayı tetikleyicisi değildir; yayın reddedilir.
        var recordKey = new AutomationDefinition(new DomainEventTrigger("autotest.item.updated"), null, [Note()]);
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, "Kayıt olayı anahtarı", null, 100, false, null, null, Json(recordKey)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
        Assert.Contains("eventKey", await publish.Content.ReadAsStringAsync());
    }

    private EventEnvelope Envelope(Guid tenantId, Guid recordId, string eventType, JsonObject payload) =>
        new(Guid.NewGuid(), tenantId, eventType, 1, Item, recordId, null, Guid.NewGuid(), ExecutionSource.Api, null, null, 0, payload, fixture.Time.GetUtcNow());

    [Fact]
    public async Task The_rule_subscription_is_registered_for_the_declared_entities_only()
    {
        var catalog = fixture.Extended.Services.GetRequiredService<ModuleCatalog>();

        var subscription = catalog.FindSubscription(CoreModule.Subscriptions.Automation);

        Assert.NotNull(subscription);
        Assert.Contains(Item, subscription.Value.Subscription.EntityKeys);
        Assert.DoesNotContain("crm.companies", subscription.Value.Subscription.EntityKeys);
        await Task.CompletedTask;
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

    private async Task<AutomationRuleDetailDto> PublishRuleAsync(
        Setup setup, AutomationDefinition definition, bool activate = true, string[]? extraPermissions = null)
    {
        // Yayınlayan, kuralın gerektirdiği izinlere (yayın doğrulaması) sahip olmalıdır; yönetici hepsine sahiptir.
        _ = extraPermissions;
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null, Json(definition)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(activate, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        return (await publish.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
    }

    // Dağıtıcının yaptığı gibi: olay outbox'a yazılır, abone olayın tenant bağlamında çalışır, çalışma satırı yazılır.
    private async Task<AutomationRun> DeliverAsync(
        Guid tenantId, Guid recordId, string newStatus, string? oldStatus = null, bool created = false, string? correlation = null, int depth = 0)
    {
        var payload = new JsonObject { ["status"] = new JsonObject { ["old"] = oldStatus, ["new"] = newStatus } };
        var type = created ? DomainEventTypes.RecordCreated : DomainEventTypes.RecordUpdated;
        OutboxMessage message;
        using (var scope = fixture.Factory.OpenScope(tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            message = new OutboxMessage(
                new DomainEvent(tenantId, type, Item, recordId, payload.DeepClone().AsObject())
                {
                    Sequence = 1,
                    UserId = Guid.NewGuid(),
                    Source = ExecutionSource.Api,
                    CorrelationId = correlation,
                    Depth = depth,
                },
                fixture.Time.GetUtcNow());
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
        }

        var envelope = new EventEnvelope(
            message.Id, tenantId, type, 1, Item, recordId, 1, Guid.NewGuid(), ExecutionSource.Api, correlation, null, depth, payload, fixture.Time.GetUtcNow());
        await DeliverEnvelopeAsync(tenantId, envelope);
        using var read = fixture.Factory.OpenScope(tenantId);
        return await read.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking()
            .Where(r => r.EventId == message.Id).OrderByDescending(r => r.CreatedAt).FirstAsync();
    }

    private async Task DeliverAgainAsync(Guid tenantId, AutomationRun run)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var message = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == run.EventId);
        var envelope = new EventEnvelope(
            message.Id, tenantId, message.EventType, 1, message.EntityKey, message.RecordId, 1, message.UserId, ExecutionSource.Api,
            message.CorrelationId, null, message.Depth, JsonNode.Parse(message.Payload)!.AsObject(), message.OccurredAt);
        await DeliverEnvelopeAsync(tenantId, envelope);
    }

    private async Task DeliverEnvelopeAsync(Guid tenantId, EventEnvelope envelope)
    {
        await using var scope = fixture.Extended.Services.GetRequiredService<ITenantScopeFactory>().Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(envelope, CoreModule.Subscriptions.Automation);
        await scope.ServiceProvider.GetRequiredService<AutomationEventSubscriber>().HandleAsync(envelope, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().SaveChangesAsync();
    }

    private async Task<AutomationRound> RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return round ?? throw new InvalidOperationException("Tur çalışmadı.");
    }

    private async Task<AutomationRun> RunAsync(Guid tenantId, Guid runId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task<IReadOnlyList<AutomationRunStep>> StepsAsync(Guid tenantId, Guid runId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRunSteps.AsNoTracking()
            .Where(s => s.RunId == runId).OrderBy(s => s.StepNo).ToListAsync();
    }

    private async Task<int> VersionNoAsync(Guid tenantId, Guid versionId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuleVersions.AsNoTracking()
            .Where(v => v.Id == versionId).Select(v => v.VersionNo).SingleAsync();
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
        db.TenantSettings.Add(new TenantSetting(CoreModule.SettingKeys.AutomationEnabled, enabled ? CoreModule.SettingValues.Yes : CoreModule.SettingValues.No));
        await db.SaveChangesAsync();
    }
}
