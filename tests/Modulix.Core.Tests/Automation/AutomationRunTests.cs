using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

public class AutomationTriggerMatcherTests
{
    private static JsonObject Change(string field, string? oldValue, string? newValue) =>
        new() { [field] = new JsonObject { ["old"] = oldValue, ["new"] = newValue } };

    [Fact]
    public void Record_created_matches_only_the_created_event()
    {
        var trigger = new RecordCreatedTrigger();

        Assert.True(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordCreated, new JsonObject()));
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordUpdated, new JsonObject()));
    }

    [Fact]
    public void Field_changed_matches_when_any_listed_field_is_in_the_diff_by_its_record_name()
    {
        var trigger = new FieldChangedTrigger([Amount.Key, "sales.deal.custom.region"]);

        Assert.True(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordUpdated, Change("amount", "1", "2")));
        Assert.True(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordUpdated, Change("custom.region", null, "ege")));
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordUpdated, Change("title", "a", "b")));

        // Oluşturma "değişiklik" sayılmaz; yalnızca güncelleme.
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordCreated, Change("amount", null, "2")));
    }

    [Fact]
    public void Status_transition_checks_old_and_new_and_treats_empty_from_as_any()
    {
        var specific = new StatusTransitionTrigger(Stage.Key, "negotiation", "won");
        var anyFrom = new StatusTransitionTrigger(Stage.Key, null, "won");

        Assert.True(AutomationTriggerMatcher.Matches(specific, Entity, DomainEventTypes.RecordUpdated, Change("stage", "negotiation", "won")));
        Assert.False(AutomationTriggerMatcher.Matches(specific, Entity, DomainEventTypes.RecordUpdated, Change("stage", "new", "won")));
        Assert.False(AutomationTriggerMatcher.Matches(specific, Entity, DomainEventTypes.RecordUpdated, Change("stage", "negotiation", "lost")));
        Assert.True(AutomationTriggerMatcher.Matches(anyFrom, Entity, DomainEventTypes.RecordUpdated, Change("stage", "new", "won")));

        // Kayıt doğrudan o durumla oluşturulduysa "herhangi bir durumdan" geçiş sayılır; belirli kaynak durum bunu karşılamaz.
        Assert.True(AutomationTriggerMatcher.Matches(anyFrom, Entity, DomainEventTypes.RecordCreated, Change("stage", null, "won")));
        Assert.False(AutomationTriggerMatcher.Matches(specific, Entity, DomainEventTypes.RecordCreated, Change("stage", null, "won")));

        // Silme olayı hiçbir geçiş değildir; farkta alan yoksa eşleşmez.
        Assert.False(AutomationTriggerMatcher.Matches(anyFrom, Entity, DomainEventTypes.RecordDeleted, Change("stage", "won", null)));
        Assert.False(AutomationTriggerMatcher.Matches(anyFrom, Entity, DomainEventTypes.RecordUpdated, new JsonObject()));
    }

    [Fact]
    public void Triggers_that_are_not_record_events_never_match_an_event()
    {
        AutomationTrigger[] others =
        [
            new ScheduledTrigger(), new DateRelativeTrigger(DueDate.Key, -3), new InactivityTrigger(Activities.Key, 14),
            new DomainEventTrigger("sales.deal.won"), new ManualTrigger(),
        ];

        Assert.All(others, t => Assert.False(AutomationTriggerMatcher.Matches(t, Entity, DomainEventTypes.RecordUpdated, Change("stage", "new", "won"))));
    }
}

public class AutomationExecutableTriggerTests
{
    [Fact]
    public void Record_event_triggers_and_manual_run_are_executable()
    {
        Assert.All(
            new AutomationTrigger[] { new RecordCreatedTrigger(), new FieldChangedTrigger([Amount.Key]), new StatusTransitionTrigger(Stage.Key, null, "won"), new ManualTrigger() },
            t => Assert.True(AutomationTriggerMatcher.IsExecutable(t)));
    }

    [Fact]
    public void Scan_and_domain_event_triggers_are_not_executable_yet()
    {
        Assert.All(
            new AutomationTrigger[] { new ScheduledTrigger(), new DateRelativeTrigger(DueDate.Key, -3), new InactivityTrigger(Activities.Key, 14), new DomainEventTrigger("sales.deal.won") },
            t => Assert.False(AutomationTriggerMatcher.IsExecutable(t)));
    }
}

public class AutomationRuleIdentityTests
{
    private static ModuleCatalog Catalog() => AutomationKit.Catalog();

    private static AutomationDefinition Definition(ConditionNode? condition, params AutomationActionStep[] steps) =>
        new(new RecordCreatedTrigger(), condition, steps);

    private static AutomationActionStep Step(string action, JsonObject? parameters = null) => new("s1", action, parameters ?? new JsonObject());

    [Fact]
    public void Permissions_are_exactly_what_the_definition_needs()
    {
        var definition = Definition(
            Leaf(Stage, AutomationOperator.Eq, "won"),
            Step(FollowUp.Key, new JsonObject { ["subject"] = "Ara" }));

        var permissions = AutomationRuleIdentity.Permissions(Entity, definition, Catalog());

        // Kayıt türünün okuma izni + aksiyonun izni; hassas alanın izni yok çünkü tanım o alana bakmıyor.
        Assert.Equal(["sales.deals.read", WritePermission], permissions.Order());
        Assert.DoesNotContain(SecretPermission, permissions);
    }

    [Fact]
    public void A_sensitive_field_in_the_condition_or_in_a_binding_adds_its_read_permission()
    {
        var inCondition = Definition(Leaf(Secret, AutomationOperator.Gt, "10"));
        var inBinding = Definition(
            null,
            Step(FollowUp.Key, new JsonObject { ["subject"] = new JsonObject { ["$field"] = Secret.Key } }));

        Assert.Contains(SecretPermission, AutomationRuleIdentity.Permissions(Entity, inCondition, Catalog()));
        Assert.Contains(SecretPermission, AutomationRuleIdentity.Permissions(Entity, inBinding, Catalog()));
    }

    [Fact]
    public void Unknown_actions_and_fields_add_nothing()
    {
        var definition = Definition(new ConditionLeaf("sales.deal.yok", AutomationOperator.Eq, ["x"]), Step("sales.yok.yok"));

        Assert.Equal(["sales.deals.read"], AutomationRuleIdentity.Permissions(Entity, definition, Catalog()));
    }
}

public class AutomationRunKeyTests
{
    [Fact]
    public void Dedupe_key_is_stable_and_changes_with_every_component()
    {
        var tenant = Guid.NewGuid();
        var version = Guid.NewGuid();
        var record = Guid.NewGuid();
        var key = AutomationRunKeys.Dedupe(tenant, "ev-1", version, Entity, record);

        Assert.Equal(key, AutomationRunKeys.Dedupe(tenant, "ev-1", version, Entity, record));
        Assert.Equal(AutomationRun.DedupeKeyLength, key.Length);
        Assert.NotEqual(key, AutomationRunKeys.Dedupe(Guid.NewGuid(), "ev-1", version, Entity, record));
        Assert.NotEqual(key, AutomationRunKeys.Dedupe(tenant, "ev-2", version, Entity, record));
        Assert.NotEqual(key, AutomationRunKeys.Dedupe(tenant, "ev-1", Guid.NewGuid(), Entity, record));
        Assert.NotEqual(key, AutomationRunKeys.Dedupe(tenant, "ev-1", version, Activity, record));
        Assert.NotEqual(key, AutomationRunKeys.Dedupe(tenant, "ev-1", version, Entity, Guid.NewGuid()));
    }

    [Fact]
    public void Step_keys_differ_per_step_and_per_run()
    {
        var run = AutomationRunKeys.Dedupe(Guid.NewGuid(), "ev", Guid.NewGuid(), Entity, Guid.NewGuid());

        Assert.NotEqual(AutomationRunKeys.Step(run, "a"), AutomationRunKeys.Step(run, "b"));
        Assert.Equal(AutomationRunKeys.Step(run, "a"), AutomationRunKeys.Step(run, "a"));
        Assert.NotEqual(AutomationRunKeys.Step(run, "a"), AutomationRunKeys.Step(run + "x", "a"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 60)]
    public void Retry_delay_backs_off(int failedAttempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), AutomationRetryPolicy.Delay(failedAttempts));
}

public class AutomationEventSubscriberTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore(params ActiveAutomationRule[] rules) : IAutomationRunStore
    {
        public List<AutomationRun> Added { get; } = [];

        public HashSet<string> Existing { get; } = [];

        public bool ChainBlocked { get; set; }

        public Task<IReadOnlyList<ActiveAutomationRule>> ActiveRulesAsync(string entityKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActiveAutomationRule>>([.. rules]);

        public Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken) => Task.FromResult(Existing.Contains(dedupeKey));

        public Task<bool> ChainExistsAsync(Guid ruleId, Guid recordId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(ChainBlocked);

        public void Add(AutomationRun run) => Added.Add(run);
    }

    private static ActiveAutomationRule Rule(AutomationTrigger trigger, DateTimeOffset? from = null, DateTimeOffset? until = null)
    {
        var definition = new AutomationDefinition(trigger, null, [new AutomationActionStep("s1", FollowUp.Key, new JsonObject())]);
        var rule = AutomationRule.Create("Kural", "sales", Entity, Guid.NewGuid(), Now);
        var version = AutomationRuleVersion.CreateDraft(
            rule.Id, 1, new AutomationVersionContent("Kural", null, 100, false, from, until, definition), Guid.NewGuid(), Now);
        return new ActiveAutomationRule(rule, version);
    }

    private static EventEnvelope Envelope(string type, JsonObject payload, int depth = 0, string? correlation = "trace-1") =>
        new(Guid.NewGuid(), Tenant, type, 1, Entity, Guid.NewGuid(), 1, Guid.NewGuid(), ExecutionSource.Api, correlation, null, depth, payload, Now);

    private static AutomationEventSubscriber Subscriber(FakeStore store) => new(store, new FixedTime(Now));

    [Fact]
    public async Task A_matching_event_creates_one_pending_run_pinned_to_the_published_version()
    {
        var rule = Rule(new RecordCreatedTrigger());
        var store = new FakeStore(rule);
        var envelope = Envelope(DomainEventTypes.RecordCreated, new JsonObject(), depth: 2);

        await Subscriber(store).HandleAsync(envelope, CancellationToken.None);

        var run = Assert.Single(store.Added);
        Assert.Equal(AutomationRunStatus.Pending, run.Status);
        Assert.Equal(rule.Version.Id, run.RuleVersionId);
        Assert.Equal(rule.Rule.Id, run.RuleId);
        Assert.Equal(envelope.Id, run.EventId);
        Assert.Equal(envelope.RecordId, run.RecordId);
        Assert.Equal(AutomationRunOrigin.Event, run.Origin);
        Assert.Equal("trace-1", run.CorrelationId);
        Assert.Equal(2, run.Depth);
        Assert.Equal(AutomationRunKeys.Dedupe(Tenant, envelope.Id.ToString("N"), rule.Version.Id, Entity, envelope.RecordId), run.DedupeKey);
    }

    [Fact]
    public async Task A_redelivered_event_does_not_create_a_second_run()
    {
        var rule = Rule(new RecordCreatedTrigger());
        var store = new FakeStore(rule);
        var envelope = Envelope(DomainEventTypes.RecordCreated, new JsonObject());
        store.Existing.Add(AutomationRunKeys.Dedupe(Tenant, envelope.Id.ToString("N"), rule.Version.Id, Entity, envelope.RecordId));

        await Subscriber(store).HandleAsync(envelope, CancellationToken.None);

        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task Triggers_that_do_not_match_the_diff_create_nothing()
    {
        var store = new FakeStore(Rule(new StatusTransitionTrigger(Stage.Key, null, "won")));

        await Subscriber(store).HandleAsync(
            Envelope(DomainEventTypes.RecordUpdated, new JsonObject { ["stage"] = new JsonObject { ["old"] = "new", ["new"] = "lost" } }),
            CancellationToken.None);

        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task Rules_outside_their_activity_window_are_skipped()
    {
        var store = new FakeStore(
            Rule(new RecordCreatedTrigger(), from: Now.AddDays(1)),
            Rule(new RecordCreatedTrigger(), until: Now.AddDays(-1)),
            Rule(new RecordCreatedTrigger(), from: Now.AddDays(-1), until: Now.AddDays(1)));

        await Subscriber(store).HandleAsync(Envelope(DomainEventTypes.RecordCreated, new JsonObject()), CancellationToken.None);

        Assert.Single(store.Added);
    }

    [Fact]
    public async Task A_rule_triggered_event_in_the_same_trace_does_not_retrigger_the_same_rule_on_the_same_record()
    {
        var store = new FakeStore(Rule(new RecordCreatedTrigger())) { ChainBlocked = true };

        // Derinlik 0 (kullanıcının kendi değişikliği): zincir sınırı uygulanmaz.
        await Subscriber(store).HandleAsync(Envelope(DomainEventTypes.RecordCreated, new JsonObject()), CancellationToken.None);
        Assert.Single(store.Added);

        // Derinlik 1 (kural kaynaklı): aynı iz + aynı kural + aynı kayıt bir kez çalışmıştır, döngü kesilir.
        await Subscriber(store).HandleAsync(Envelope(DomainEventTypes.RecordCreated, new JsonObject(), depth: 1), CancellationToken.None);
        Assert.Single(store.Added);
    }

    [Fact]
    public async Task No_rules_means_no_work()
    {
        var store = new FakeStore();

        await Subscriber(store).HandleAsync(Envelope(DomainEventTypes.RecordCreated, new JsonObject()), CancellationToken.None);

        Assert.Empty(store.Added);
    }
}

public class AutomationParameterResolverTests
{
    private static readonly AutomationFieldSet Visible = Fields(SecretPermission);
    private static readonly AutomationFieldSet Hidden = Fields();

    private static AutomationActionStep Step(JsonObject parameters) => new("s1", FollowUp.Key, parameters);

    [Fact]
    public void Constants_are_copied_and_bindings_read_the_current_record_value()
    {
        var facts = Facts(Visible, new JsonObject { ["title"] = "Anka Lojistik", ["score"] = 7 });
        var step = Step(new JsonObject
        {
            ["subject"] = new JsonObject { ["$field"] = Title.Key },
            ["priority"] = "high",
            ["dueInDays"] = new JsonObject { ["$field"] = Score.Key },
        });

        var resolved = AutomationParameterResolver.Resolve(step, Visible, facts);

        Assert.Equal("Anka Lojistik", resolved.Values["subject"]!.GetValue<string>());
        Assert.Equal("high", resolved.Values["priority"]!.GetValue<string>());
        Assert.Equal(7m, resolved.Values["dueInDays"]!.GetValue<decimal>());
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void An_empty_or_hidden_field_resolves_to_null_with_a_note_that_never_shows_the_value()
    {
        var values = new JsonObject { ["title"] = null, ["margin"] = 42 };
        var step = Step(new JsonObject
        {
            ["subject"] = new JsonObject { ["$field"] = Title.Key },
            ["note"] = new JsonObject { ["$field"] = Secret.Key },
        });

        var resolved = AutomationParameterResolver.Resolve(step, Hidden, Facts(Hidden, values));

        Assert.Null(resolved.Values["subject"]);
        Assert.Null(resolved.Values["note"]);
        Assert.Equal(2, resolved.Notes.Count);
        Assert.Contains("boş", resolved.Notes[0]);
        Assert.Contains("görülemiyor", resolved.Notes[1]);
        Assert.DoesNotContain(resolved.Notes, n => n.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_required_parameters_are_listed_by_label()
    {
        var values = new JsonObject { ["subject"] = "  ", ["dueInDays"] = 3 };

        var missing = AutomationParameterResolver.MissingRequired(FollowUp, values);

        Assert.Equal(["Konu"], missing);
        Assert.Empty(AutomationParameterResolver.MissingRequired(FollowUp, new JsonObject { ["subject"] = "x", ["dueInDays"] = 3 }));
    }
}

public class AutomationSubscriptionCatalogTests
{
    [Fact]
    public void The_rule_subscription_listens_only_to_entities_modules_declared_for_rules()
    {
        var subscription = Catalog().FindSubscription(CoreModule.Subscriptions.Automation);

        Assert.NotNull(subscription);
        Assert.Equal("core", subscription.Value.ModuleKey);
        Assert.Equal([Activity, Entity], subscription.Value.Subscription.EntityKeys);
        Assert.False(subscription.Value.Subscription.Ordered);
        Assert.Empty(subscription.Value.Subscription.Permissions);
        Assert.True(subscription.Value.Subscription.Matches(DomainEventTypes.RecordUpdated, Entity));
        Assert.False(subscription.Value.Subscription.Matches(DomainEventTypes.RecordUpdated, "sales.other"));
    }

    [Fact]
    public void Without_any_rule_capability_there_is_no_subscription_and_no_delivery_overhead()
    {
        var catalog = Catalog(AutomationCapabilities.None);

        Assert.Null(catalog.FindSubscription(CoreModule.Subscriptions.Automation));
        Assert.Empty(catalog.SubscriptionsFor(DomainEventTypes.RecordUpdated, Entity).Where(s => s.Subscription.Key == CoreModule.Subscriptions.Automation));
    }
}
