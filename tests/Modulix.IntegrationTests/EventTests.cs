using System.Text.Json.Nodes;
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
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Modules.Logistics;

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
    ];

    private readonly ModulixApiFactory factory;
    private readonly MutableTimeProvider time = new(DateTimeOffset.UtcNow);
    private readonly CapturingLoggerProvider logs = new();
    private readonly WebApplicationFactory<Program> host;

    public EventTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        EventProbe.Reset();
        host = factory.WithWebHostBuilder(builder => builder
            .ConfigureTestServices(services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                var module = new EventTestModule();
                services.AddSingleton(ModuleCatalog.Create(new CrmModule(), new LogisticsModule(), module));
                module.ConfigureServices(services);
                services.AddSingleton<TimeProvider>(time);
            }));
    }

    /// <summary>Önceki testlerden kalan olay ve teslimler bu testin işleyicisine karışmaz.</summary>
    public async Task InitializeAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await db.Database.ExecuteSqlRawAsync("UPDATE [platform].[OutboxMessages] SET [DispatchedAt] = SYSDATETIMEOFFSET() WHERE [DispatchedAt] IS NULL");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE [platform].[EventDeliveries] SET [Status] = 4, [CompletedAt] = SYSDATETIMEOFFSET() WHERE [Status] IN (1, 3)");
    }

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

        Assert.Empty(EventProbe.Calls.Where(c => c.Envelope.TenantId == other.TenantId));
        var deliveries = await DeliveriesAsync(company.Id);
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

        Assert.Empty(await DeliveriesAsync(company.Id));
        using var scope = host.Services.CreateScope();
        var message = await Db(scope).OutboxMessages.SingleAsync(o => o.RecordId == company.Id);
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

        // Dead-letter bir daha denenmez; loglarda kayıt değeri yok.
        time.Advance(TimeSpan.FromDays(1));
        await DrainAsync();
        Assert.Equal(EventDispatcher.MaxAttempts, EventProbe.CallsOf(EventTestModule.Unordered, company.Id).Count);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("7391", StringComparison.Ordinal));
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
        using var scope = host.Services.CreateScope();
        var stored = await Db(scope).OutboxMessages.SingleAsync(o => o.RecordId == opportunity.Id);
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

        using var scope = host.Services.CreateScope();
        var message = await Db(scope).OutboxMessages.SingleAsync(o => o.RecordId == party.Id);
        Assert.Equal((EventHandlingScope.MaxDepth + 1, trigger.Id, "zincir"), (message.Depth, message.CausationId, message.CorrelationId));
        Assert.Equal(ExecutionSource.BackgroundJob, message.Source);
        Assert.NotNull(message.DispatchedAt);
        Assert.Contains("Derinlik sınırı", message.LastError);
        Assert.Empty(await DeliveriesAsync(party.Id));

        // Denetim kaydında kişi "sistem", kaynak arka plan işi ve abonelik görünür.
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(actor.TenantId, actor.UserId, actor.MembershipId);
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
        Assert.All(await DeliveriesAsync(b.Id), d => Assert.Equal(EventDeliveryStatus.Skipped, d.Status));
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

    private sealed record Actor(Guid TenantId, Guid UserId, Guid MembershipId);

    private async Task<Actor> ActorAsync(bool eventModule = true)
    {
        var tenant = await factory.CreateTenantAsync();
        await SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        if (eventModule)
        {
            await SetModuleAsync(tenant.Id, EventTestModule.ModuleKey, enabled: true);
        }

        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, AllCrmPermissions, user);
        return new Actor(tenant.Id, user.Id, await factory.GetMembershipIdAsync(user.Id, tenant.Id));
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

    private async Task<EventBacklog> MeasureAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EventDispatcher>().MeasureAsync(CancellationToken.None);
    }

    /// <summary>Zamanı gelmiş olay ve teslim kalmayana kadar işleyiciyi çalıştırır.</summary>
    private async Task DrainAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = host.Services.CreateScope();
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round is { Dispatched: 0, Delivered: 0 })
            {
                return;
            }
        }
    }

    private async Task<int> WithDispatcherAsync(Func<EventDispatcher, Task<int>> action)
    {
        using var scope = host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<EventDispatcher>());
    }

    // Platform ekranındaki "Atla" gibi; testte doğrudan.
    private async Task SkipAsync(Guid recordId, string subscription, long? sequence = null)
    {
        using var scope = host.Services.CreateScope();
        await Db(scope).EventDeliveries
            .Where(d => d.RecordId == recordId && d.Subscription == subscription && (sequence == null || d.Sequence == sequence))
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.Status, EventDeliveryStatus.Skipped));
    }

    private async Task<List<EventDelivery>> DeliveriesAsync(Guid recordId, string? subscription = null)
    {
        using var scope = host.Services.CreateScope();
        return await Db(scope).EventDeliveries.AsNoTracking()
            .Where(d => d.RecordId == recordId && (subscription == null || d.Subscription == subscription))
            .OrderBy(d => d.Sequence).ThenBy(d => d.Subscription)
            .ToListAsync();
    }

    private async Task<bool> EventExistsAsync(Guid recordId)
    {
        using var scope = host.Services.CreateScope();
        return await Db(scope).OutboxMessages.AnyAsync(o => o.RecordId == recordId);
    }

    private async Task<int> EmailCountAsync(Guid tenantId)
    {
        using var scope = host.Services.CreateScope();
        return await Db(scope).EmailMessages.CountAsync(e => e.TenantId == tenantId && e.ToAddress.StartsWith("ev-"));
    }

    private static ModulixDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
}
