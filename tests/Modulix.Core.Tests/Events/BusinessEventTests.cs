using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Tests.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Events;

/// <summary>
/// ADR-0039: iş olayı beyanı katalogda doğrulanır, abonelik ve otomasyon bu türleri tanır; yayıncı zarfı beyandan doldurur ve beyan
/// dışı olayı reddeder. Veritabanı yok; "sales" ve "finance" yalnızca test modülleridir.
/// </summary>
public class BusinessEventCatalogTests
{
    private const string Won = "sales.deal.won";

    private static Action Build(params IModule[] more) => () => Catalog(null, more);

    private sealed class Subscriber : IEventSubscriber
    {
        public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TestModule Finance(IReadOnlyList<BusinessEventDefinition>? events = null, IReadOnlyList<EventSubscription>? subscriptions = null) =>
        new("finance", AutomationCapabilities.None, "finance.invoices.read")
        {
            BusinessEvents = events ?? [],
            EventSubscriptions = subscriptions ?? [],
        };

    [Fact]
    public void Declared_business_events_are_found_with_their_version_and_module()
    {
        var catalog = Catalog();

        var (moduleKey, definition) = catalog.FindBusinessEvent(Won)!.Value;
        Assert.Equal("sales", moduleKey);
        Assert.Equal(1, definition.SchemaVersion);
        Assert.Equal(Entity, definition.EntityKey);
        Assert.Equal(1, catalog.EventSchemaVersions[Won]);
        Assert.Equal(1, catalog.EventSchemaVersions[DomainEventTypes.RecordUpdated]);
        Assert.Null(catalog.FindBusinessEvent("sales.deal.lost"));
        Assert.Contains(catalog.BusinessEvents, e => e.Definition.EventType == Won);
    }

    [Fact]
    public void Business_event_declaration_is_validated_at_startup()
    {
        // Başka modülün öneki, kayıt olayı biçimi, sürüm 0, başka modülün kayıt türü, boş alan adı, çift tanım.
        Assert.Throws<InvalidOperationException>(Build(Finance([new("sales.invoice.paid", 1, "finance.invoice", "Ödendi", [])])));
        Assert.Throws<InvalidOperationException>(Build(Finance([new("finance.InvoicePaid", 1, "finance.invoice", "Ödendi", [])])));
        Assert.Throws<InvalidOperationException>(Build(Finance([new("finance.invoice.paid", 0, "finance.invoice", "Ödendi", [])])));
        Assert.Throws<InvalidOperationException>(Build(Finance([new("finance.invoice.paid", 1, Entity, "Ödendi", [])])));
        Assert.Throws<InvalidOperationException>(Build(Finance([new("finance.invoice.paid", 1, "finance.invoice", "Ödendi", ["amount", ""])])));
        Assert.Throws<InvalidOperationException>(Build(Finance(
        [
            new("finance.invoice.paid", 1, "finance.invoice", "Ödendi", ["amount"]),
            new("finance.invoice.paid", 1, "finance.invoice", "Ödendi", ["amount"]),
        ])));

        // Geçerli beyan.
        var catalog = Catalog(null, Finance([new("finance.invoice.paid", 2, "finance.invoice", "Fatura ödendi", ["amount", "paidAt"])]));
        Assert.Equal(2, catalog.EventSchemaVersions["finance.invoice.paid"]);
    }

    [Fact]
    public void A_module_can_subscribe_to_another_modules_business_event_but_not_to_an_undeclared_or_newer_one()
    {
        // finance, sales'in iş olayına abone olur (kayıt türü sales'in): asıl kullanım.
        var ok = Catalog(null, Finance(subscriptions: [EventSubscription.For<Subscriber>("finance.on-won", [Won], [Entity], [])]));
        Assert.Contains(ok.SubscriptionsFor(Won, Entity), s => s.Subscription.Key == "finance.on-won");
        Assert.DoesNotContain(ok.SubscriptionsFor(DomainEventTypes.RecordUpdated, Entity), s => s.Subscription.Key == "finance.on-won");

        Assert.Throws<InvalidOperationException>(Build(Finance(subscriptions: [EventSubscription.For<Subscriber>("finance.on-lost", ["sales.deal.lost"], [Entity], [])])));

        var newer = new TestModule("sales", Capabilities, "sales.deals.read", WritePermission, SecretPermission)
        {
            BusinessEvents = [new(Won, 2, Entity, "Fırsat kazanıldı", ["amount"])],
        };
        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog(
        [
            new CoreModule(), newer, Finance(subscriptions: [EventSubscription.For<Subscriber>("finance.on-won", [Won], [Entity], [])]),
        ]));
    }

    [Fact]
    public void An_automation_event_with_a_module_prefixed_type_must_be_a_declared_business_event_of_the_same_entity()
    {
        var entities = new AutomationEntityDefinition[] { new(Entity, "A"), new(Activity, "B") };

        // Beyansız tür ve beyanlı türün başka kayıt türüyle sunulması reddedilir.
        Assert.Throws<InvalidOperationException>(() => Catalog(new AutomationCapabilities
        {
            Entities = entities,
            Events = [new("sales.deal.lost", Entity, "sales.deal.lost", "Kaybedildi")],
        }));
        Assert.Throws<InvalidOperationException>(() => Catalog(new AutomationCapabilities
        {
            Entities = entities,
            Events = [new("sales.activity.won", Activity, Won, "Kazanıldı")],
        }));

        // core.automation aboneliği kayıt olayları + otomasyona beyan edilmiş iş olaylarını alır.
        var catalog = Catalog();
        var automation = catalog.FindSubscription(CoreModule.Subscriptions.Automation)!.Value.Subscription;
        Assert.Contains(Won, automation.EventTypes);
        Assert.Contains(DomainEventTypes.RecordUpdated, automation.EventTypes);
        Assert.Contains(catalog.SubscriptionsFor(Won, Entity), s => s.Subscription.Key == CoreModule.Subscriptions.Automation);
    }

    [Fact]
    public void Domain_event_trigger_matches_only_its_declared_business_event_on_its_entity()
    {
        var catalog = Catalog();
        var trigger = new DomainEventTrigger(Won);
        var payload = new JsonObject { ["amount"] = 10 };

        Assert.True(AutomationTriggerMatcher.Matches(trigger, Entity, Won, payload, catalog.FindAutomationEvent));
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Activity, Won, payload, catalog.FindAutomationEvent));
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Entity, DomainEventTypes.RecordUpdated, payload, catalog.FindAutomationEvent));
        Assert.False(AutomationTriggerMatcher.Matches(trigger, Entity, Won, payload));

        // Kayıt olayı anahtarı iş olayı tetikleyicisiyle eşleşmez; kayıt oluşturma tetikleyicisi de iş olayıyla eşleşmez.
        Assert.False(AutomationTriggerMatcher.Matches(new DomainEventTrigger("sales.deal.updated"), Entity, DomainEventTypes.RecordUpdated, payload, catalog.FindAutomationEvent));
        Assert.False(AutomationTriggerMatcher.Matches(new RecordCreatedTrigger(), Entity, Won, payload, catalog.FindAutomationEvent));
    }
}

public class BusinessEventPublisherTests
{
    private const string Won = "sales.deal.won";
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private sealed class TestTenant(TenantContext? current) : ITenantContextAccessor
    {
        public TenantContext? Current => current;
    }

    private sealed class FakeOutbox : IDomainEventOutbox
    {
        public List<DomainEvent> Events { get; } = [];

        public void Add(DomainEvent domainEvent) => Events.Add(domainEvent);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static (BusinessEventPublisher Publisher, FakeOutbox Outbox, FakeUnitOfWork Work) Publisher(TenantContext? tenant, EventHandlingScope? handling = null)
    {
        var outbox = new FakeOutbox();
        var work = new FakeUnitOfWork();
        return (new BusinessEventPublisher(new TestTenant(tenant), Catalog(), outbox, work, handling), outbox, work);
    }

    [Fact]
    public async Task The_envelope_comes_from_the_declaration_the_session_and_the_command()
    {
        var (publisher, outbox, work) = Publisher(new TenantContext(Tenant, User, Guid.NewGuid()));
        var recordId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        await publisher.PublishAsync(new BusinessEvent(Won, recordId, new JsonObject { ["amount"] = 1500, ["stage"] = "won" })
        {
            RecordVersion = 7,
            CompanyId = companyId,
            Reason = "deal.won",
        }, CancellationToken.None);

        var domainEvent = Assert.Single(outbox.Events);
        Assert.Equal(Won, domainEvent.EventType);
        Assert.Equal(1, domainEvent.SchemaVersion);
        Assert.Equal(Entity, domainEvent.EntityKey);
        Assert.Equal(recordId, domainEvent.RecordId);
        Assert.Equal(7, domainEvent.Sequence);
        Assert.Equal(Tenant, domainEvent.TenantId);
        Assert.Equal(User, domainEvent.UserId);
        Assert.Equal(companyId, domainEvent.CompanyId);
        Assert.Equal(ExecutionSource.Api, domainEvent.Source);
        Assert.Equal(0, domainEvent.Depth);
        Assert.Null(domainEvent.CausationId);
        Assert.Equal("deal.won", domainEvent.Reason);
        Assert.Equal(1500, domainEvent.Payload["amount"]!.GetValue<int>());
        Assert.Equal(1, work.Saves);
    }

    [Fact]
    public async Task Undeclared_type_undeclared_field_missing_tenant_and_empty_record_are_programming_errors()
    {
        var (publisher, outbox, work) = Publisher(new TenantContext(Tenant, User, Guid.NewGuid()));
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(new BusinessEvent("sales.deal.lost", id, new JsonObject()), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(new BusinessEvent(Won, id, new JsonObject { ["margin"] = 5 }), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(new BusinessEvent(Won, Guid.Empty, new JsonObject()), CancellationToken.None));

        var (noTenant, _, _) = Publisher(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noTenant.PublishAsync(new BusinessEvent(Won, id, new JsonObject()), CancellationToken.None));

        // Reddedilen yayın outbox'a hiçbir şey bırakmaz ve iş birimini kaydetmez.
        Assert.Empty(outbox.Events);
        Assert.Equal(0, work.Saves);
    }

    [Fact]
    public async Task An_event_published_inside_a_subscriber_is_chained_to_the_cause_as_a_background_job()
    {
        var cause = new EventEnvelope(
            Guid.NewGuid(), Tenant, DomainEventTypes.RecordUpdated, 1, Entity, Guid.NewGuid(), 3, User, ExecutionSource.Api,
            "trace-9", null, 2, new JsonObject(), DateTimeOffset.UtcNow) { CompanyId = Guid.NewGuid() };
        var handling = new EventHandlingScope();
        handling.Begin(cause, "finance.on-won");
        var (publisher, outbox, _) = Publisher(new TenantContext(Tenant, Guid.Empty, Guid.Empty), handling);

        await publisher.PublishAsync(new BusinessEvent(Won, Guid.NewGuid(), new JsonObject()), CancellationToken.None);

        var domainEvent = Assert.Single(outbox.Events);
        Assert.Equal(cause.Id, domainEvent.CausationId);
        Assert.Equal(3, domainEvent.Depth);
        Assert.Equal("trace-9", domainEvent.CorrelationId);
        Assert.Equal(cause.CompanyId, domainEvent.CompanyId);
        Assert.Equal(ExecutionSource.BackgroundJob, domainEvent.Source);
        Assert.Equal(Guid.Empty, domainEvent.UserId);
    }
}
