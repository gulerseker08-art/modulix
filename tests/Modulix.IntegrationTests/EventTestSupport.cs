using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;

namespace Modulix.IntegrationTests;

/// <summary>
/// Olay testleri sırayla ve diğer testlerden sonra çalışır: işleyici veritabanındaki bütün olayları ve teslimleri işler;
/// paralel çalışan testlerin olaylarını almamalıdır.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventTestCollection
{
    public const string Name = "Olay işleyicisi";
}

/// <summary>
/// Testlere özel modül: CRM firma ve fırsat olaylarına iki abonelik. <see cref="Recorder"/> sıralıdır ve ortak firma
/// kaydını yazabilir (<see cref="PartiesWrite"/>), <see cref="Unordered"/> sırasız ve izinsizdir. İkisi de CRM izni
/// bildirmediği için hassas alanları göremez.
/// </summary>
public sealed class EventTestModule : IModule
{
    public const string ModuleKey = "evtest";
    public const string Recorder = "evtest.recorder";
    public const string Unordered = "evtest.unordered";
    public const string PartiesWrite = "evtest.parties.write";

    private static readonly string[] AllEvents = [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated, DomainEventTypes.RecordDeleted];
    private static readonly string[] CrmRecords = [CrmModule.Entities.Company, CrmModule.Entities.Opportunity];

    public string Key => ModuleKey;

    public string DisplayName => "Olay testi";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new(PartiesWrite, "Ortak firma kaydı yazar")];

    public IReadOnlyList<SharedRecordAccess> SharedRecordAccess { get; } = [new(SharedEntities.Party, PartiesWrite, PartiesWrite)];

    public IReadOnlyList<EventSubscription> EventSubscriptions { get; } =
    [
        EventSubscription.For<RecorderSubscriber>(Recorder, AllEvents, CrmRecords, [PartiesWrite]),
        EventSubscription.For<UnorderedSubscriber>(Unordered, AllEvents, CrmRecords, []) with { Ordered = false },
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<RecorderSubscriber>();
        services.AddScoped<UnorderedSubscriber>();
    }
}

/// <summary>Abonenin bir çalışmasında gördükleri.</summary>
public sealed record EventCall(
    string Subscription,
    EventEnvelope Envelope,
    IReadOnlySet<string> Permissions,
    RecordScope Scope,
    IReadOnlySet<Guid>? Owners,
    IReadOnlyList<Guid> VisibleTenants);

/// <summary>
/// Test abonelerinin ortak durumu. Olay testleri sırayla çalıştığı için statiktir; her test başında sıfırlanır.
/// </summary>
public static class EventProbe
{
    public static ConcurrentQueue<EventCall> Calls { get; } = new();

    /// <summary>Doğruysa abone istisna fırlatır (kalıcı ya da geçici).</summary>
    public static Func<string, EventEnvelope, bool> Fails { get; set; } = (_, _) => false;

    public static bool PermanentFailure { get; set; }

    /// <summary>Doluysa abonenin ilk çalışması (bu olay için) kapı açılana kadar bekler.</summary>
    public static TaskCompletionSource? Gate { get; set; }

    public static TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TimeSpan Delay { get; set; }

    /// <summary>Abone her olay için bu adrese e-posta yazar (işlemle birlikte); ikinci etki olup olmadığı sayılarak görülür.</summary>
    public static string EmailAddress(EventEnvelope envelope, string subscription) => $"ev-{envelope.Id:N}-{subscription.Replace('.', '-')}@test.modulix";

    public static void Reset()
    {
        Calls.Clear();
        Fails = (_, _) => false;
        PermanentFailure = false;
        Gate = null;
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Delay = TimeSpan.Zero;
    }

    public static IReadOnlyList<EventCall> CallsOf(string subscription, Guid recordId) =>
        [.. Calls.Where(c => c.Subscription == subscription && c.Envelope.RecordId == recordId)];
}

public abstract class ProbeSubscriber(
    string subscription, IServiceProvider services, IEmailQueue emails) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var access = services.GetRequiredService<ICurrentAccess>();
        var tenant = services.GetRequiredService<Modulix.Core.Contracts.Tenancy.ITenantContextAccessor>().Current!;
        var owners = await services.GetRequiredService<IDataScopeService>().GetOwnersAsync(tenant, envelope.EntityKey, cancellationToken);

        // RLS: sorgu filtresi kapalıyken bile yalnızca olayın tenant'ının satırları görünür.
        var visible = await services.GetRequiredService<ModulixDbContext>().AuditLog.IgnoreQueryFilters()
            .Select(a => a.TenantId).Distinct().ToListAsync(cancellationToken);

        var first = !EventProbe.Calls.Any(c => c.Subscription == subscription && c.Envelope.Id == envelope.Id);
        EventProbe.Calls.Enqueue(new EventCall(
            subscription, envelope, await access.GetPermissionsAsync(cancellationToken), await access.GetScopeAsync(cancellationToken), owners, visible));

        if (first && EventProbe.Gate is { } gate)
        {
            EventProbe.Entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (EventProbe.Delay > TimeSpan.Zero)
        {
            await Task.Delay(EventProbe.Delay, cancellationToken);
        }

        if (EventProbe.Fails(subscription, envelope))
        {
            // İletide kişisel veri yok; yalnızca kimlikler.
            throw EventProbe.PermanentFailure
                ? new PermanentEventFailureException($"Kalıcı test hatası ({envelope.Id}).")
                : new InvalidOperationException($"Geçici test hatası ({envelope.Id}).");
        }

        emails.Enqueue(new EmailMessage(EventProbe.EmailAddress(envelope, subscription), "Olay", "<p>Olay işlendi.</p>"));
    }
}

public sealed class RecorderSubscriber(IServiceProvider services, IEmailQueue emails)
    : ProbeSubscriber(EventTestModule.Recorder, services, emails);

public sealed class UnorderedSubscriber(IServiceProvider services, IEmailQueue emails)
    : ProbeSubscriber(EventTestModule.Unordered, services, emails);

/// <summary>Zamanı elle ilerleyen saat: kira, yeniden deneme ve saklama süreleri beklemeden sınanır.</summary>
public sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>Logları toplar; olay işleyicisinin loglarında payload ve alan değeri olmadığı denetlenir.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Messages.Enqueue($"{formatter(state, exception)} {exception}");
    }
}
