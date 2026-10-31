using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modulix.Api.Events;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;
using Modulix.Modules.Crm;
using Modulix.Modules.Logistics;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;

namespace Modulix.IntegrationTests;

/// <summary>
/// Olay işleyicisi (ADR-0007, kabul testleri). İşleyici testte kendiliğinden çalışmaz; testler dağıtıcıyı çağırır ve
/// saati elle ilerletir. Abonelikler testlere özel modüldedir (<see cref="EventTestModule"/>).
/// </summary>
[Collection(EventTestCollection.Name)]
public sealed class EventTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private static readonly string[] AllCrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite, CrmModule.Permissions.OpportunityAmountsRead,
        CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite,
        CrmModule.Permissions.LeadsRead, CrmModule.Permissions.LeadsUpdate,
    ];

    private readonly ModulixApiFactory factory;
    private readonly MutableTimeProvider time = new(DateTimeOffset.UtcNow);
    private readonly CapturingLoggerProvider logs = new();
    private readonly WebApplicationFactory<Program> host;

    // ADR-0020: outbox/teslimler tek veritabanında tenant bağlamıyla okunur (ADR-0020). Test, kullandığı tenant'ları izler; işleyici ve
    // veri okuma yardımcıları bu tenant'ların bağlamında (oturum tenant'ı yazılarak) çalışır.
    private readonly List<Guid> tenantIds = [];

    public EventTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        EventProbe.Reset();
        host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("App:PublicUrl", "https://app.modulix.test")
            .ConfigureTestServices(services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                var module = new EventTestModule();
                services.AddSingleton(ModuleCatalog.Create(new CrmModule(), new LogisticsModule(), module));
                module.ConfigureServices(services);
                services.AddSingleton<TimeProvider>(time);
            }));
    }

    /// <summary>
    /// ADR-0020: her test kendi tenant'ını oluşturur; outbox ve teslimler o tenant'ın bağlamında (TenantId ile)
    /// izole olduğundan önceki testlerden kalan olay bu testin işleyicisine karışmaz. Eski genel sıfırlama (tek paylaşımlı
    /// veritabanı varsayımı) gerekmez.
    /// </summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Event_is_delivered_once_per_subscription_in_its_tenant_with_system_identity()
    {
        var actor = await ActorAsync();
        var other = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Acme");

        await DrainAsync();
        await DrainAsync();

        foreach (var subscription in new[] { EventTestModule.Recorder, EventTestModule.Unordered })
        {
            var call = Assert.Single(EventProbe.CallsOf(subscription, company.Id));
            var envelope = call.Envelope;
            Assert.Equal(actor.TenantId, envelope.TenantId);
            Assert.Equal(DomainEventTypes.RecordCreated, envelope.EventType);
            Assert.Equal(CrmModule.Entities.Company, envelope.EntityKey);
            Assert.Equal(company.Version, envelope.Sequence);
            Assert.Equal(actor.UserId, envelope.UserId);
            Assert.Equal(1, envelope.SchemaVersion);
            Assert.Equal("Acme", envelope.Payload["name"]!["new"]!.GetValue<string>());

            // Sistem kimliği: yalnızca aboneliğin bildirdiği izinler, tenant geneli kapsam; kişinin CRM izinleri yok.
            Assert.Equal(subscription == EventTestModule.Recorder ? [EventTestModule.PartiesWrite] : [], call.Permissions.Order());
            Assert.Equal(RecordScope.All, call.Scope);
            Assert.Null(call.Owners);

            // Başka tenant'ın satırları RLS ile görünmez.
            Assert.Equal([actor.TenantId], call.VisibleTenants);
        }

        Assert.DoesNotContain(EventProbe.Calls, c => c.Envelope.TenantId == other.TenantId);
        var deliveries = await TestDeliveriesAsync(company.Id);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal(EventDeliveryStatus.Succeeded, d.Status));
        Assert.All(deliveries, d => Assert.Equal(1, d.AttemptCount));
        Assert.All(deliveries, d => Assert.Null(d.LeaseToken));
        Assert.Equal(2, await EmailCountAsync(actor.TenantId));
    }

    [Fact]
    public async Task Event_is_not_delivered_where_the_subscribing_module_is_off()
    {
        var actor = await ActorAsync(eventModule: false);
        var company = await CreateCompanyAsync(actor, "Kapalı");

        await DrainAsync();

        Assert.Empty(await TestDeliveriesAsync(company.Id));
        var message = await OutboxAsync(company.Id);
        Assert.NotNull(message.DispatchedAt);
    }

    /// <summary>Ç1.2: iki işleyici aynı teslimi bir kez başarılı yazar.</summary>
    [Fact]
    public async Task Concurrent_dispatchers_complete_each_delivery_once()
    {
        var actor = await ActorAsync();
        var companies = new List<RecordData>();
        for (var i = 0; i < 3; i++)
        {
            companies.Add(await CreateCompanyAsync(actor, $"Firma {i}"));
        }

        await DispatchAsync();
        EventProbe.Delay = TimeSpan.FromMilliseconds(50);
        await Task.WhenAll(DeliverAsync(), DeliverAsync(), DeliverAsync());

        foreach (var company in companies)
        {
            Assert.Single(EventProbe.CallsOf(EventTestModule.Recorder, company.Id));
            Assert.All(await DeliveriesAsync(company.Id), d => Assert.Equal((EventDeliveryStatus.Succeeded, 1), (d.Status, d.AttemptCount)));
        }

        Assert.Equal(6, await EmailCountAsync(actor.TenantId));
    }

    /// <summary>Ç1.2: kirası dolan işleyicinin geç sonucu yazılmaz, teslim kaybolmaz; iş etkisi bir kez oluşur.</summary>
    [Fact]
    public async Task Late_result_of_an_expired_lease_is_discarded()
    {
        var actor = await ActorAsync(eventModule: true);
        var company = await CreateCompanyAsync(actor, "Kira");
        await DispatchAsync();
        await SkipAsync(company.Id, EventTestModule.Unordered);
        await SkipAsync(company.Id, CoreModule.Subscriptions.AssignmentEmail);

        EventProbe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = DeliverAsync();
        await EventProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // İlk işleyici takılı kaldı; kira doldu, ikinci işleyici teslimi alıp bitirir.
        time.Advance(EventDispatcher.Lease + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await DeliverAsync());
        var delivery = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder));
        Assert.Equal((EventDeliveryStatus.Succeeded, 2), (delivery.Status, delivery.AttemptCount));

        EventProbe.Gate.SetResult();
        await slow;

        delivery = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder));
        Assert.Equal((EventDeliveryStatus.Succeeded, 2), (delivery.Status, delivery.AttemptCount));
        Assert.Equal(2, EventProbe.CallsOf(EventTestModule.Recorder, company.Id).Count);
        Assert.Equal(1, await EmailCountAsync(actor.TenantId));
        Assert.Contains(logs.Messages, m => m.Contains("kirası dolduğu için", StringComparison.Ordinal));
    }

    /// <summary>Ç1.3: bir abone başarısız, diğeri başarılı; yalnızca başarısız olan yeniden çalışır, sonunda dead-letter.</summary>
    [Fact]
    public async Task Failed_subscriber_is_retried_alone_with_backoff_then_dead_lettered()
    {
        var actor = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Gizli Firma Adı 7391");
        EventProbe.Fails = (subscription, envelope) => subscription == EventTestModule.Unordered && envelope.RecordId == company.Id;

        await DrainAsync();
        for (var attempt = 1; attempt <= EventDispatcher.MaxAttempts; attempt++)
        {
            var failing = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Unordered));
            Assert.Equal(attempt, failing.AttemptCount);
            Assert.Contains("Geçici test hatası", failing.LastError);

            if (attempt < EventDispatcher.MaxAttempts)
            {
                Assert.Equal(EventDeliveryStatus.Pending, failing.Status);
                var delay = failing.NextAttemptAt - time.GetUtcNow();
                var expected = EventDispatcher.RetryDelay(attempt);
                Assert.InRange(delay, expected * 0.8, expected * 1.2);

                // Zamanı gelmeden denenmez.
                await DrainAsync();
                Assert.Equal(attempt, EventProbe.CallsOf(EventTestModule.Unordered, company.Id).Count);

                time.Advance(delay);
                await DrainAsync();
            }
            else
            {
                Assert.Equal(EventDeliveryStatus.Failed, failing.Status);
                Assert.NotNull(failing.CompletedAt);
            }
        }

        // Başarılı abone bir kez çalıştı; e-postası bir tane.
        Assert.Single(EventProbe.CallsOf(EventTestModule.Recorder, company.Id));
        Assert.Equal(EventDeliveryStatus.Succeeded, Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder)).Status);
        Assert.Equal(1, await EmailCountAsync(actor.TenantId));

        // Dead-letter bir daha denenmez; loglarda kayıt değeri yok (sayı değil ad aranır: GUID metinlerinde rastgele geçebilir).
        time.Advance(TimeSpan.FromDays(1));
        await DrainAsync();
        Assert.Equal(EventDispatcher.MaxAttempts, EventProbe.CallsOf(EventTestModule.Unordered, company.Id).Count);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("Gizli Firma Adı", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains(company.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Permanent_failure_is_dead_lettered_without_retry()
    {
        var actor = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Kalıcı");
        EventProbe.Fails = (subscription, envelope) => subscription == EventTestModule.Recorder && envelope.RecordId == company.Id;
        EventProbe.PermanentFailure = true;

        await DrainAsync();
        time.Advance(TimeSpan.FromDays(1));
        await DrainAsync();

        var delivery = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder));
        Assert.Equal((EventDeliveryStatus.Failed, 1), (delivery.Status, delivery.AttemptCount));
        Assert.StartsWith(nameof(PermanentEventFailureException), delivery.LastError);
    }

    /// <summary>
    /// Ç1.4: aynı kaydın olayları sırayla; ilki başarısızken ikincisi bekler, başka kaydın ve başka tenant'ın olayı
    /// beklemez. Sırasız abone beklemez.
    /// </summary>
    [Fact]
    public async Task Failed_event_holds_back_only_later_events_of_the_same_record_and_subscriber()
    {
        var actor = await ActorAsync();
        var neighbour = await ActorAsync();
        var poisoned = await CreateCompanyAsync(actor, "Zehirli");
        var renamed = await UpdateCompanyAsync(actor, poisoned.Id, "Zehirli 2");
        var healthy = await CreateCompanyAsync(actor, "Sağlam");
        var foreign = await CreateCompanyAsync(neighbour, "Komşu");
        EventProbe.Fails = (subscription, envelope) =>
            subscription == EventTestModule.Recorder && envelope.RecordId == poisoned.Id && envelope.Sequence == poisoned.Version;
        EventProbe.PermanentFailure = true;

        await DrainAsync();

        var recorder = await DeliveriesAsync(poisoned.Id, EventTestModule.Recorder);
        Assert.Equal(EventDeliveryStatus.Failed, recorder.Single(d => d.Sequence == poisoned.Version).Status);
        var waiting = recorder.Single(d => d.Sequence == renamed.Version);
        Assert.Equal((EventDeliveryStatus.Pending, 0), (waiting.Status, waiting.AttemptCount));
        Assert.Single(EventProbe.CallsOf(EventTestModule.Recorder, poisoned.Id));

        // Sırasız abone, başka kayıt ve başka tenant beklemez.
        Assert.Equal(2, EventProbe.CallsOf(EventTestModule.Unordered, poisoned.Id).Count);
        Assert.Single(EventProbe.CallsOf(EventTestModule.Recorder, healthy.Id));
        Assert.Single(EventProbe.CallsOf(EventTestModule.Recorder, foreign.Id));

        // Zehirli teslim atlanınca bekleyen teslim sırayla çalışır.
        await SkipAsync(poisoned.Id, EventTestModule.Recorder, poisoned.Version);
        await DrainAsync();
        Assert.Equal(EventDeliveryStatus.Succeeded, (await DeliveriesAsync(poisoned.Id, EventTestModule.Recorder)).Single(d => d.Sequence == renamed.Version).Status);
    }

    [Fact]
    public async Task Events_of_a_record_reach_an_ordered_subscriber_in_sequence()
    {
        var actor = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Sıra 1");
        await UpdateCompanyAsync(actor, company.Id, "Sıra 2");
        await UpdateCompanyAsync(actor, company.Id, "Sıra 3");

        await DrainAsync();

        var sequences = EventProbe.CallsOf(EventTestModule.Recorder, company.Id).Select(c => c.Envelope.Sequence!.Value).ToList();
        Assert.Equal(3, sequences.Count);
        Assert.Equal(sequences.Order(), sequences);
    }

    /// <summary>Ç1.5 / Y8b: aboneliğin izni olmayan hassas alan zarfta yoktur; yalnızca adı verilir.</summary>
    [Fact]
    public async Task Sensitive_fields_are_removed_from_the_envelope()
    {
        var actor = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Tutarlı");
        // API'deki gibi JSON'dan okunmuş değerler.
        var values = JsonNode.Parse($$"""
            { "title": "Büyük iş", "companyId": "{{company.Id}}", "amount": 1250000, "probability": 40, "currency": "TRY" }
            """)!.AsObject();
        var opportunity = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Opportunity, null, values));

        await DrainAsync();

        var envelope = EventProbe.CallsOf(EventTestModule.Recorder, opportunity.Id).Single().Envelope;
        Assert.Equal("Büyük iş", envelope.Payload["title"]!["new"]!.GetValue<string>());
        Assert.False(envelope.Payload.ContainsKey("amount"));
        Assert.False(envelope.Payload.ContainsKey("probability"));
        Assert.Equal(["amount", "probability"], envelope.HiddenFields);

        // Outbox tam farkı saklar; süzgeç teslimde uygulanır.
        var stored = await OutboxAsync(opportunity.Id);
        Assert.Contains("1250000", stored.Payload);
    }

    [Fact]
    public async Task Event_beyond_the_depth_limit_is_written_but_not_dispatched()
    {
        var actor = await ActorAsync();
        var trigger = new EventEnvelope(
            Guid.NewGuid(), actor.TenantId, DomainEventTypes.RecordUpdated, 1, CrmModule.Entities.Company, Guid.NewGuid(), 1,
            actor.UserId, ExecutionSource.Api, "zincir", CausationId: null, EventHandlingScope.MaxDepth, new JsonObject(), time.GetUtcNow());

        // Abone, aboneliğin izniyle ortak firma kaydı yazar (ADR-0006); olay tetikleyen olaya bağlanır.
        var party = await SaveAsync(actor, new RecordCommand(SharedEntities.Party, null, new JsonObject { ["name"] = "Döngü" }), trigger);
        await DrainAsync();

        var message = await OutboxAsync(party.Id);
        Assert.Equal((EventHandlingScope.MaxDepth + 1, trigger.Id, "zincir"), (message.Depth, message.CausationId, message.CorrelationId));
        Assert.Equal(ExecutionSource.BackgroundJob, message.Source);
        Assert.NotNull(message.DispatchedAt);
        Assert.Contains("Derinlik sınırı", message.LastError);
        Assert.Empty(await DeliveriesAsync(party.Id));

        // Denetim kaydında kişi "sistem", kaynak arka plan işi ve abonelik görünür.
        using var scope = TenantScope(actor.TenantId);
        var audit = await Db(scope).AuditLog.SingleAsync(a => a.RecordId == party.Id);
        Assert.Equal((Guid.Empty, ExecutionSource.BackgroundJob, EventTestModule.Recorder), (audit.UserId, audit.Source, audit.Subscription));
    }

    [Fact]
    public async Task Deliveries_of_a_closed_tenant_or_a_disabled_module_are_skipped()
    {
        var closing = await ActorAsync();
        var disabling = await ActorAsync();
        var a = await CreateCompanyAsync(closing, "Kapanan");
        var b = await CreateCompanyAsync(disabling, "Modülü kapanan");
        await DispatchAsync();

        await factory.CloseTenantAsync(closing.TenantId);
        await SetModuleAsync(disabling.TenantId, EventTestModule.ModuleKey, enabled: false);
        await DrainAsync();

        Assert.Empty(EventProbe.Calls);
        Assert.All(await DeliveriesAsync(a.Id), d => Assert.Equal((EventDeliveryStatus.Skipped, "Firma kapalı."), (d.Status, d.LastError)));
        Assert.All(await TestDeliveriesAsync(b.Id), d => Assert.Equal((EventDeliveryStatus.Skipped, "Aboneliğin modülü firmada kapalı."), (d.Status, d.LastError)));
    }

    [Fact]
    public async Task Completed_deliveries_and_their_events_are_removed_after_retention()
    {
        var actor = await ActorAsync();
        var done = await CreateCompanyAsync(actor, "Bitti");
        var failed = await CreateCompanyAsync(actor, "Başarısız");
        EventProbe.Fails = (subscription, envelope) => subscription == EventTestModule.Recorder && envelope.RecordId == failed.Id;
        EventProbe.PermanentFailure = true;
        await DrainAsync();

        time.Advance(EventDispatcher.CompletedRetention + TimeSpan.FromHours(1));
        await CleanupAsync();

        Assert.Empty(await DeliveriesAsync(done.Id));
        Assert.False(await EventExistsAsync(done.Id));

        // Başarısız teslim ve olayı 30 gün saklanır.
        Assert.Equal(EventDeliveryStatus.Failed, Assert.Single(await DeliveriesAsync(failed.Id)).Status);
        Assert.True(await EventExistsAsync(failed.Id));

        time.Advance(EventDispatcher.FailedRetention);
        await CleanupAsync();
        Assert.Empty(await DeliveriesAsync(failed.Id));
        Assert.False(await EventExistsAsync(failed.Id));
    }

    [Fact]
    public async Task Health_is_degraded_when_events_wait_too_long()
    {
        var actor = await ActorAsync();
        await CreateCompanyAsync(actor, "Bekleyen");

        Assert.False((await MeasureAsync()).IsDispatchDelayed);
        time.Advance(EventBacklog.DispatchDelayLimit + TimeSpan.FromMinutes(1));
        Assert.True((await MeasureAsync()).IsDispatchDelayed);

        using (var scope = host.Services.CreateScope())
        {
            var health = await ActivatorUtilities.CreateInstance<EventHealthCheck>(scope.ServiceProvider)
                .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
            Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, health.Status);
        }

        await DrainAsync();
        Assert.False((await MeasureAsync()).IsDispatchDelayed);
    }

    /// <summary>Atama e-postası (core.assignment-email): yeni sorumluya bir kez, kodlanmış içerikle ve bağlantıyla.</summary>
    [Fact]
    public async Task Assignment_email_goes_to_the_new_owner_once()
    {
        var actor = await ActorAsync();
        var owner = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead);
        var company = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
        {
            ["name"] = "Acme <b>& Ortakları</b>", [OwnerField] = owner.UserId.ToString(),
        }));

        await DrainAsync();
        await DrainAsync();

        var email = Assert.Single(await AssignmentEmailsAsync(owner.Email));
        Assert.Equal(actor.TenantId, email.TenantId);
        Assert.Contains("Acme &lt;b&gt;&amp; Ortakları&lt;/b&gt;", email.HtmlBody);
        Assert.DoesNotContain("<b>&", email.HtmlBody);
        Assert.Contains($"https://app.modulix.test/crm/firmalar/{company.Id}", email.HtmlBody);
        Assert.Contains("Firma (CRM)", email.HtmlBody);
        var delivery = Assert.Single(await DeliveriesAsync(company.Id, CoreModule.Subscriptions.AssignmentEmail));
        Assert.Equal((EventDeliveryStatus.Succeeded, 1), (delivery.Status, delivery.AttemptCount));

        // Sorumlu değişmeyen güncelleme e-posta göndermez; başkasına atama yeni e-posta gönderir.
        await UpdateCompanyAsync(actor, company.Id, "Acme Yeni");
        await DrainAsync();
        Assert.Single(await AssignmentEmailsAsync(owner.Email));
    }

    [Fact]
    public async Task Self_assignment_and_unassignment_send_no_email()
    {
        var actor = await ActorAsync();
        var owner = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead);
        var own = await CreateCompanyAsync(actor, "Kendime");
        var assigned = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
        {
            ["name"] = "Önce atanan", [OwnerField] = owner.UserId.ToString(),
        }));
        await DrainAsync();
        Assert.Single(await AssignmentEmailsAsync(owner.Email));

        await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, assigned.Id, new JsonObject { [OwnerField] = actor.UserId.ToString() }));
        await DrainAsync();

        Assert.Empty(await AssignmentEmailsAsync(await EmailOfAsync(actor.UserId)));
        Assert.Single(await AssignmentEmailsAsync(owner.Email));
        Assert.All(await DeliveriesAsync(own.Id, CoreModule.Subscriptions.AssignmentEmail), d => Assert.Equal(EventDeliveryStatus.Succeeded, d.Status));
    }

    [Fact]
    public async Task Recipient_who_cannot_read_the_record_or_left_gets_no_email()
    {
        var actor = await ActorAsync();
        var blind = await MemberAsync(actor);
        var leaving = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead);
        var hidden = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
        {
            ["name"] = "Göremez", [OwnerField] = blind.UserId.ToString(),
        }));
        var left = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
        {
            ["name"] = "Ayrıldı", [OwnerField] = leaving.UserId.ToString(),
        }));
        await factory.DisableMembershipAsync(leaving.UserId, actor.TenantId);

        await DrainAsync();

        Assert.Empty(await AssignmentEmailsAsync(blind.Email));
        Assert.Empty(await AssignmentEmailsAsync(leaving.Email));
        Assert.Equal(EventDeliveryStatus.Succeeded, Assert.Single(await DeliveriesAsync(hidden.Id, CoreModule.Subscriptions.AssignmentEmail)).Status);
        Assert.Equal(EventDeliveryStatus.Succeeded, Assert.Single(await DeliveriesAsync(left.Id, CoreModule.Subscriptions.AssignmentEmail)).Status);
    }

    /// <summary>ADR-0004 ile: devir olaylara "transfer" yazar; atama e-postası kayıt başına gitmez, tek özet gider.</summary>
    [Fact]
    public async Task Transfer_sends_one_summary_instead_of_an_email_per_record()
    {
        var actor = await ActorAsync(administrator: true);
        var previous = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite);
        var receiver = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead);
        for (var i = 0; i < 3; i++)
        {
            await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
            {
                ["name"] = $"Devredilen {i}", [OwnerField] = previous.UserId.ToString(),
            }));
        }

        await DrainAsync();
        Assert.Equal(3, (await AssignmentEmailsAsync(previous.Email)).Count);

        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(actor.TenantId, actor.UserId, actor.MembershipId);
            var moved = await scope.ServiceProvider.GetRequiredService<IUserAdministration>()
                .TransferRecordsAsync(previous.MembershipId, receiver.MembershipId, CancellationToken.None);
            Assert.Equal(3, moved);
        }

        await DrainAsync();

        Assert.Empty(await AssignmentEmailsAsync(receiver.Email));
        var summary = Assert.Single(await EmailsAsync(receiver.Email));
        Assert.Equal("Kayıtlar size devredildi", summary.Subject);
        Assert.Contains("3 firma", summary.HtmlBody);

        using var check = TenantScope(actor.TenantId);
        var transfers = await Db(check).OutboxMessages
            .Where(o => o.TenantId == actor.TenantId && o.Reason == EventReasons.Transfer)
            .CountAsync();
        Assert.Equal(3, transfers);
    }

    /// <summary>Ç1.3: bir abone başarısız, diğeri başarılı; yeniden denemede ikinci atama e-postası oluşmaz.</summary>
    [Fact]
    public async Task Retrying_a_failed_subscriber_does_not_repeat_the_assignment_email()
    {
        var actor = await ActorAsync();
        var owner = await MemberAsync(actor, CrmModule.Permissions.CompaniesRead);
        var company = await SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject
        {
            ["name"] = "Tekrar", [OwnerField] = owner.UserId.ToString(),
        }));
        var failures = 0;
        EventProbe.Fails = (subscription, envelope) =>
            subscription == EventTestModule.Recorder && envelope.RecordId == company.Id && Interlocked.Increment(ref failures) == 1;

        await DrainAsync();
        Assert.Equal(EventDeliveryStatus.Pending, Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder)).Status);
        time.Advance(EventDispatcher.RetryDelay(1) * 1.2);
        await DrainAsync();

        Assert.Equal(EventDeliveryStatus.Succeeded, Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder)).Status);
        var assignment = Assert.Single(await DeliveriesAsync(company.Id, CoreModule.Subscriptions.AssignmentEmail));
        Assert.Equal((EventDeliveryStatus.Succeeded, 1), (assignment.Status, assignment.AttemptCount));
        Assert.Single(await AssignmentEmailsAsync(owner.Email));
    }

    /// <summary>
    /// Platform ekranı: başarısız ve bekletilen teslimler payload olmadan listelenir; yeniden deneme sayaçları sıfırlar,
    /// teslimin firmasının denetim kaydına yazılır ve bekletilen teslimi de serbest bırakır.
    /// </summary>
    [Fact]
    public async Task Platform_screen_lists_failures_without_payload_and_retries_with_audit()
    {
        var actor = await ActorAsync();
        var company = await CreateCompanyAsync(actor, "Gizli Değer 4242");
        await UpdateCompanyAsync(actor, company.Id, "Gizli Değer 4243");
        EventProbe.Fails = (subscription, envelope) => subscription == EventTestModule.Recorder && envelope.RecordId == company.Id;
        EventProbe.PermanentFailure = true;
        await DrainAsync();

        var admin = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(admin.Id);
        using var client = await LoginAsync(admin.Email!);

        var summary = (await client.GetFromJsonAsync<EventSummaryResponse>("/api/platform/events/summary"))!;
        Assert.True(summary.Failed >= 1 && summary.Held >= 1);
        Assert.Contains(EventTestModule.Recorder, summary.Subscriptions);

        var body = await client.GetStringAsync($"/api/platform/events/deliveries?tenantId={actor.TenantId}&subscription={EventTestModule.Recorder}");
        Assert.DoesNotContain("4242", body, StringComparison.Ordinal);
        Assert.DoesNotContain("4243", body, StringComparison.Ordinal);
        var page = JsonSerializer.Deserialize<EventDeliveryPageResponse>(body, JsonSerializerOptions.Web)!;
        Assert.Equal(2, page.TotalCount);
        var failed = Assert.Single(page.Items, d => d.State == EventDeliveryStateDto.Failed);
        Assert.Equal(1, failed.HoldingBack);
        Assert.Equal(DomainEventTypes.RecordCreated, failed.EventType);
        Assert.Equal(actor.Tenant.Name, failed.TenantName);
        var held = Assert.Single(page.Items, d => d.State == EventDeliveryStateDto.Held);

        var onlyFailed = (await client.GetFromJsonAsync<EventDeliveryPageResponse>(
            $"/api/platform/events/deliveries?tenantId={actor.TenantId}&state={EventDeliveryStateDto.Failed}"))!;
        Assert.Equal([failed.Id], onlyFailed.Items.Select(d => d.Id));

        // Çalışmakta veya bitmiş teslim değişmez; başarısız olan sıfırlanır ve hemen zamanlanır.
        var succeeded = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Unordered), d => d.Sequence == company.Version);
        var retry = await client.PostAsJsonAsync("/api/platform/events/deliveries/retry", new EventDeliveryActionRequest([failed.Id, succeeded.Id], null));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, (await retry.Content.ReadFromJsonAsync<EventDeliveryActionResponse>())!.Changed);
        var reset = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder), d => d.Id == failed.Id);
        Assert.Equal((EventDeliveryStatus.Pending, 0, (string?)null), (reset.Status, reset.AttemptCount, reset.LastError));

        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(actor.TenantId, actor.UserId, actor.MembershipId);
            var audit = await Db(scope).AuditLog.SingleAsync(a => a.EntityKey == CoreModule.AuditedEntityKeys.EventDelivery && a.RecordId == failed.Id);
            Assert.Equal(admin.Id, audit.UserId);
            Assert.Contains(EventTestModule.Recorder, audit.Changes);
            Assert.Contains(failed.EventId.ToString(), audit.Changes);
            Assert.DoesNotContain("4242", audit.Changes, StringComparison.Ordinal);
        }

        // Abone düzelince yeniden denenen ve bekletilen teslim sırayla çalışır.
        EventProbe.Fails = (_, _) => false;
        await DrainAsync();
        Assert.All(await DeliveriesAsync(company.Id, EventTestModule.Recorder), d => Assert.Equal(EventDeliveryStatus.Succeeded, d.Status));
        Assert.Contains(EventProbe.CallsOf(EventTestModule.Recorder, company.Id), c => c.Envelope.Id == held.EventId);
    }

    [Fact]
    public async Task Platform_screen_skips_filtered_deliveries_and_is_closed_to_others()
    {
        var actor = await ActorAsync();
        var a = await CreateCompanyAsync(actor, "Atlanacak 1");
        var b = await CreateCompanyAsync(actor, "Atlanacak 2");
        EventProbe.Fails = (subscription, _) => subscription == EventTestModule.Recorder;
        EventProbe.PermanentFailure = true;
        await DrainAsync();

        var admin = await factory.CreateUserAsync();
        await SetPlatformAdministratorAsync(admin.Id);
        using var client = await LoginAsync(admin.Email!);
        var skip = await client.PostAsJsonAsync(
            "/api/platform/events/deliveries/skip",
            new EventDeliveryActionRequest(null, new EventDeliveryFilterDto(actor.TenantId, EventTestModule.Recorder, State: EventDeliveryStateDto.Failed)));
        Assert.Equal(2, (await skip.Content.ReadFromJsonAsync<EventDeliveryActionResponse>())!.Changed);
        foreach (var company in new[] { a, b })
        {
            var delivery = Assert.Single(await DeliveriesAsync(company.Id, EventTestModule.Recorder));
            Assert.Equal((EventDeliveryStatus.Skipped, "Platform yöneticisi atladı."), (delivery.Status, delivery.LastError));
        }

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/platform/events/deliveries/skip", new EventDeliveryActionRequest(null, null))).StatusCode);

        // Firma yöneticisi dahil, platform yöneticisi olmayan kimse göremez.
        var member = await MemberAsync(actor, CoreModule.Permissions.ManageUsers);
        using var memberClient = await LoginAsync(member.Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/platform/events/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.PostAsJsonAsync("/api/platform/events/deliveries/retry", new EventDeliveryActionRequest([Guid.NewGuid()], null))).StatusCode);
    }

    private const string OwnerField = "ownerId";

    private async Task SetPlatformAdministratorAsync(Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(userId.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private sealed record Actor(Tenant Tenant, Guid UserId, Guid MembershipId)
    {
        public Guid TenantId => Tenant.Id;
    }

    private sealed record Member(Guid UserId, Guid MembershipId, string Email);

    private async Task<Member> MemberAsync(Actor actor, params string[] permissions)
    {
        var user = await factory.CreateUserAsync(actor.Tenant);
        if (permissions.Length > 0)
        {
            await factory.CreateRoleAsync(actor.TenantId, permissions, user);
        }

        return new Member(user.Id, await factory.GetMembershipIdAsync(user.Id, actor.TenantId), user.Email!);
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await Platform(scope).Users.Where(u => u.Id == userId).Select(u => u.Email!).SingleAsync();
    }

    private async Task<List<Modulix.Infrastructure.Email.QueuedEmail>> EmailsAsync(string address)
    {
        using var scope = host.Services.CreateScope();
        return await Platform(scope).EmailMessages.AsNoTracking().Where(e => e.ToAddress == address).ToListAsync();
    }

    private async Task<List<Modulix.Infrastructure.Email.QueuedEmail>> AssignmentEmailsAsync(string address) =>
        [.. (await EmailsAsync(address)).Where(e => e.Subject == "Size bir kayıt atandı")];

    private async Task<Actor> ActorAsync(bool eventModule = true, bool administrator = false)
    {
        var tenant = await factory.CreateTenantAsync();
        tenantIds.Add(tenant.Id);
        await SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        if (eventModule)
        {
            await SetModuleAsync(tenant.Id, EventTestModule.ModuleKey, enabled: true);
        }

        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, administrator ? [.. AllCrmPermissions, CoreModule.Permissions.ManageUsers] : AllCrmPermissions, user);
        return new Actor(tenant, user.Id, await factory.GetMembershipIdAsync(user.Id, tenant.Id));
    }

    private async Task SetModuleAsync(Guid tenantId, string moduleKey, bool enabled)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModuleEntitlements>().SetEnabledAsync(tenantId, moduleKey, enabled, CancellationToken.None);
    }

    private Task<RecordData> CreateCompanyAsync(Actor actor, string name) =>
        SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, null, new JsonObject { ["name"] = name }));

    private Task<RecordData> UpdateCompanyAsync(Actor actor, Guid id, string name) =>
        SaveAsync(actor, new RecordCommand(CrmModule.Entities.Company, id, new JsonObject { ["name"] = name }));

    /// <summary>Kişinin isteği gibi (veya <paramref name="cause"/> verilirse bir abonenin değişikliği gibi) kayıt yazar.</summary>
    private async Task<RecordData> SaveAsync(Actor actor, RecordCommand command, EventEnvelope? cause = null)
    {
        using var scope = host.Services.CreateScope();

        // Abone dağıtıcıdaki gibi sistem kimliğiyle çalışır.
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = cause is null
            ? new TenantContext(actor.TenantId, actor.UserId, actor.MembershipId)
            : new TenantContext(actor.TenantId, Guid.Empty, Guid.Empty);
        if (cause is not null)
        {
            scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(cause, EventTestModule.Recorder);
        }

        var pipeline = scope.ServiceProvider.GetRequiredService<IRecordPipeline>();
        var result = command.Id is null
            ? await pipeline.CreateAsync(command, CancellationToken.None)
            : await pipeline.UpdateAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, $"{result.Error?.Message} {string.Join(" ", result.Error?.FieldErrors?.Select(e => $"{e.Field}: {e.Message}") ?? [])}");
        return result.Value!;
    }

    private Task<int> DispatchAsync() => WithDispatcherAsync(d => d.DispatchAsync(CancellationToken.None));

    private Task<int> DeliverAsync() => WithDispatcherAsync(d => d.DeliverAsync(CancellationToken.None));

    private Task<int> CleanupAsync() => WithDispatcherAsync(d => d.CleanupAsync(CancellationToken.None));

    /// <summary>Tenant bağlamlı host kapsamı (ADR-0020): işleyici/veri o tenant'ın bağlamında (oturum tenant'ı) çalışır.</summary>
    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.Empty, Guid.Empty);
        return scope;
    }

    private async Task<EventBacklog> MeasureAsync()
    {
        // Herhangi bir tenant gecikmişse o backlog döner (health degraded); yoksa en son ölçülen.
        EventBacklog? last = null;
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            var backlog = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().MeasureAsync(CancellationToken.None);
            if (backlog.IsDispatchDelayed)
            {
                return backlog;
            }

            last = backlog;
        }

        return last ?? new EventBacklog(time.GetUtcNow(), null, null, 0);
    }

    /// <summary>Zamanı gelmiş olay ve teslim kalmayana kadar işleyiciyi (her tenant'ın bağlamında) çalıştırır.</summary>
    private async Task DrainAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            var dispatched = 0;
            var delivered = 0;
            foreach (var id in tenantIds.Distinct())
            {
                using var scope = TenantScope(id);
                var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
                dispatched += round.Dispatched;
                delivered += round.Delivered;
            }

            if (dispatched == 0 && delivered == 0)
            {
                return;
            }
        }
    }

    private async Task<int> WithDispatcherAsync(Func<EventDispatcher, Task<int>> action)
    {
        var total = 0;
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            total += await action(scope.ServiceProvider.GetRequiredService<EventDispatcher>());
        }

        return total;
    }

    // Platform ekranındaki "Atla" gibi; testte doğrudan. Kayıt hangi tenant'taysa orada güncellenir.
    private async Task SkipAsync(Guid recordId, string subscription, long? sequence = null)
    {
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            await Db(scope).EventDeliveries
                .Where(d => d.RecordId == recordId && d.Subscription == subscription && (sequence == null || d.Sequence == sequence))
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.Status, EventDeliveryStatus.Skipped));
        }
    }

    private async Task<List<EventDelivery>> DeliveriesAsync(Guid recordId, string? subscription = null)
    {
        var all = new List<EventDelivery>();
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            all.AddRange(await Db(scope).EventDeliveries.AsNoTracking()
                .Where(d => d.RecordId == recordId && (subscription == null || d.Subscription == subscription))
                .ToListAsync());
        }

        return all.OrderBy(d => d.Sequence).ThenBy(d => d.Subscription).ToList();
    }

    // Test modülünün abonelikleri (çekirdeğin atama aboneliği hariç).
    private async Task<List<EventDelivery>> TestDeliveriesAsync(Guid recordId) =>
        [.. (await DeliveriesAsync(recordId)).Where(d => d.Subscription.StartsWith(EventTestModule.ModuleKey + ".", StringComparison.Ordinal))];

    private async Task<bool> EventExistsAsync(Guid recordId)
    {
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            if (await Db(scope).OutboxMessages.AnyAsync(o => o.RecordId == recordId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Kaydın olay (outbox) iletisini, tenant bağlamıyla okur (ADR-0020).</summary>
    private async Task<OutboxMessage> OutboxAsync(Guid recordId)
    {
        foreach (var id in tenantIds.Distinct())
        {
            using var scope = TenantScope(id);
            if (await Db(scope).OutboxMessages.AsNoTracking().SingleOrDefaultAsync(o => o.RecordId == recordId) is { } message)
            {
                return message;
            }
        }

        throw new InvalidOperationException($"Olay bulunamadı: {recordId}");
    }

    private async Task<int> EmailCountAsync(Guid tenantId)
    {
        using var scope = host.Services.CreateScope();
        return await Platform(scope).EmailMessages.CountAsync(e => e.TenantId == tenantId && e.ToAddress.StartsWith("ev-"));
    }

    private static ModulixDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

    private static ModulixDbContext Platform(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
}
