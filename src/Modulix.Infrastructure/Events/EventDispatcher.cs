using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Events;

/// <summary>"Events" yapılandırma bölümü (ADR-0007).</summary>
public sealed class EventOptions
{
    public const string SectionName = "Events";

    /// <summary>Arka plan işleyicisi çalışsın mı? Entegrasyon testleri ve tek seferlik komutlar kapatır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Bir turda en fazla teslim; tek tenant'tan en fazla <see cref="EventDispatcher.PerTenantLimit"/>.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>Bir turun sonucu; tur dolu bittiyse işleyici beklemeden devam eder.</summary>
public sealed record EventRound(int Dispatched, int Delivered);

/// <summary>
/// Olay işleyicisi (ADR-0007). İki adım:
/// <list type="number">
///   <item><b>Dağıtım:</b> dağıtılmamış olaylar yazılış sırasıyla okunur, eşleşen ve modülü tenant için açık her aboneliğe
///   bir teslim satırı açılır. Uygulama kilidiyle (<c>sp_getapplock</c>) tek örnekte çalışır; böylece aynı kaydın küçük
///   sıra numaralı olayının teslimi her zaman önce oluşur.</item>
///   <item><b>Teslim:</b> zamanı gelmiş teslimler koşullu UPDATE ile kiralanır (kira belirteciyle); abone olayın tenant'ının
///   bağlamında, sistem kimliğiyle ve açık bir veritabanı işleminde çalışır; teslimin "başarılı" işareti aynı işlemde ve
///   kira belirteci şartıyla yazılır. Abone çalışırken kira uzatılır (heartbeat).</item>
/// </list>
/// Birden fazla uygulama örneği aynı anda çalışabilir. Loglara olay kimliği, tenant, kayıt türü ve kimliği yazılır;
/// payload ve alan değerleri yazılmaz.
/// </summary>
public sealed class EventDispatcher(
    ModulixDbContext db,
    ModuleCatalog catalog,
    ITenantScopeFactory tenantScopes,
    IServiceScopeFactory scopes,
    EventMetrics metrics,
    IOptions<EventOptions> options,
    TimeProvider time,
    ILogger<EventDispatcher> logger)
{
    /// <summary>Dağıtımın bir turda okuduğu en fazla olay.</summary>
    public const int DispatchBatchSize = 200;

    /// <summary>Bir turda tek tenant'tan alınan en fazla teslim; kalan kapasite diğer tenant'lara gider.</summary>
    public const int PerTenantLimit = 20;

    /// <summary>Bu kadar başarısız denemeden sonra teslim <see cref="EventDeliveryStatus.Failed"/> olur.</summary>
    public const int MaxAttempts = 6;

    /// <summary>Kiralanan teslim bu süre içinde bitmez veya uzatılmazsa başka işleyiciye geçer.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Aboneye verilen süre; uzun iş aboneden kuyruğa atılan bir arka plan işi olmalıdır (Ç2).</summary>
    public static readonly TimeSpan SubscriberTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Başarılı ve atlanmış teslimlerin, teslimleri bitmiş olayların saklama süresi.</summary>
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);

    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(30);

    private const int CleanupChunk = 1000;
    private const string DispatchLock = "modulix.event-dispatch";

    // Sıralı teslim, aynı abone ve aynı kayıt için daha küçük sıra numaralı, bitmemiş (bekleyen veya başarısız) bir
    // teslim varken alınmaz. Teslim seçimi (SQL) ve kiralama (LINQ) aynı kuralı uygular.
    private const string NotBlockedSql = """
        NOT (d.[Ordered] = 1 AND EXISTS (
            SELECT 1 FROM [platform].[EventDeliveries] p
            WHERE p.[TenantId] = d.[TenantId] AND p.[Subscription] = d.[Subscription]
              AND p.[EntityKey] = d.[EntityKey] AND p.[RecordId] = d.[RecordId]
              AND p.[Sequence] < d.[Sequence] AND p.[Status] IN (1, 3)))
        """;

    // Adillik: tenant başına en fazla @perTenant teslim; tek tenant'ın birikmesi diğerlerini bekletmez.
    private const string DueDeliveriesSql = $"""
        SELECT TOP (@batch) c.[Id] AS [Value]
        FROM (
            SELECT d.[Id], d.[NextAttemptAt],
                   ROW_NUMBER() OVER (PARTITION BY d.[TenantId] ORDER BY d.[NextAttemptAt], d.[Id]) AS [Rank]
            FROM [platform].[EventDeliveries] d
            WHERE d.[Status] = @pending AND d.[NextAttemptAt] <= @now
              AND {NotBlockedSql}
        ) c
        WHERE c.[Rank] <= @perTenant
        ORDER BY c.[NextAttemptAt], c.[Id]
        """;

    /// <summary>1.-5. başarısız denemeden sonra bekleme: 1 dk, 5 dk, 15 dk, 1 sa, 4 sa (her biri ±%20 rastgele).</summary>
    public static TimeSpan RetryDelay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(4),
    };

    /// <summary>Dağıtım ve teslim; bir tur.</summary>
    public async Task<EventRound> RunOnceAsync(CancellationToken cancellationToken)
    {
        var dispatched = await DispatchAsync(cancellationToken);
        var delivered = await DeliverAsync(cancellationToken);
        return new EventRound(dispatched, delivered);
    }

    /// <summary>
    /// Dağıtılmamış en fazla <see cref="DispatchBatchSize"/> olayı abonelere dağıtır; dağıtılan olay sayısını döner.
    /// Kilidi başka örnek tutuyorsa bu tur dağıtım yapmaz (0).
    /// </summary>
    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.Database.SqlQuery<int>($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = {DispatchLock}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
            SELECT @result AS [Value];
            """).ToListAsync(cancellationToken);
        if (locked.Single() < 0)
        {
            return 0;
        }

        var events = await db.OutboxMessages
            .Where(o => o.DispatchedAt == null)
            .OrderBy(o => o.OccurredAt).ThenBy(o => o.Id)
            .Take(DispatchBatchSize)
            .ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            return 0;
        }

        var tenantIds = events.Select(e => e.TenantId).Distinct().ToList();
        var enabled = (await db.ModuleEntitlements.AsNoTracking()
                .Where(e => tenantIds.Contains(e.TenantId) && e.IsEnabled)
                .Select(e => new { e.TenantId, e.ModuleKey })
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.TenantId, e => e.ModuleKey);

        var now = time.GetUtcNow();
        foreach (var message in events)
        {
            if (message.Depth > EventHandlingScope.MaxDepth)
            {
                // Aboneler birbirini tetikliyor; zincir burada kesilir.
                message.MarkDispatched(now, $"Derinlik sınırı aşıldı ({message.Depth} > {EventHandlingScope.MaxDepth}); abonelere dağıtılmadı.");
                logger.LogWarning(
                    "Olay {EventId} derinlik sınırını aştı ({Depth}); dağıtılmadı. Tenant {TenantId}, kayıt {EntityKey} {RecordId}, tetikleyen {CausationId}.",
                    message.Id, message.Depth, message.TenantId, message.EntityKey, message.RecordId, message.CausationId);
                continue;
            }

            var modules = enabled[message.TenantId].ToHashSet(StringComparer.Ordinal);
            foreach (var (moduleKey, subscription) in catalog.SubscriptionsFor(message.EventType, message.EntityKey))
            {
                if (catalog.IsEnabled(moduleKey, modules))
                {
                    db.EventDeliveries.Add(new EventDelivery(message, subscription.Key, subscription.Ordered, now));
                }
            }

            message.MarkDispatched(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return events.Count;
    }

    /// <summary>
    /// Zamanı gelmiş en fazla <see cref="EventOptions.BatchSize"/> teslimi dener (tenant başına en fazla
    /// <see cref="PerTenantLimit"/>); denenen sayıyı döner.
    /// </summary>
    public async Task<int> DeliverAsync(CancellationToken cancellationToken)
    {
        var batch = Math.Max(1, options.Value.BatchSize);
        var now = time.GetUtcNow();
        var due = await db.Database.SqlQueryRaw<Guid>(
            DueDeliveriesSql,
            new SqlParameter("@batch", batch),
            new SqlParameter("@pending", (int)EventDeliveryStatus.Pending),
            new SqlParameter("@now", now),
            new SqlParameter("@perTenant", PerTenantLimit)).ToListAsync(cancellationToken);

        var attempted = 0;
        foreach (var id in due)
        {
            if (await TryClaimAsync(id, now, cancellationToken) is { } lease)
            {
                await DeliverClaimedAsync(id, lease, cancellationToken);
                attempted++;
            }
        }

        return attempted;
    }

    /// <summary>
    /// Saklama süresi dolan teslimleri ve olayları siler (1000'erlik parçalarla): başarılı ve atlanmış teslimler ile
    /// teslimleri bitmiş olaylar 7 gün, başarısız teslimler 30 gün sonra. Dağıtılmamış veya bekleyen olay silinmez.
    /// </summary>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var completedBefore = now - CompletedRetention;
        var failedBefore = now - FailedRetention;

        var deliveries = await DeleteInChunksAsync(
            () => db.EventDeliveries.Where(d =>
                ((d.Status == EventDeliveryStatus.Succeeded || d.Status == EventDeliveryStatus.Skipped) && d.CompletedAt < completedBefore)
                || (d.Status == EventDeliveryStatus.Failed && d.CompletedAt < failedBefore)),
            cancellationToken);
        var events = await DeleteInChunksAsync(
            () => db.OutboxMessages.Where(o => o.DispatchedAt < completedBefore && !db.EventDeliveries.Any(d => d.EventId == o.Id)),
            cancellationToken);

        if (deliveries + events > 0)
        {
            logger.LogInformation("Olay temizliği: {Deliveries} teslim ve {Events} olay silindi.", deliveries, events);
        }

        return deliveries + events;
    }

    /// <summary>Gözlemleme (Ç14.5): en eski dağıtılmamış olayın ve en eski bekleyen teslimin zamanı, başarısız teslim sayısı.</summary>
    public async Task<EventBacklog> MeasureAsync(CancellationToken cancellationToken)
    {
        var oldestEvent = await db.OutboxMessages.Where(o => o.DispatchedAt == null)
            .MinAsync(o => (DateTimeOffset?)o.OccurredAt, cancellationToken);
        var oldestDelivery = await db.EventDeliveries.Where(d => d.Status == EventDeliveryStatus.Pending)
            .MinAsync(d => (DateTimeOffset?)d.CreatedAt, cancellationToken);
        var failed = await db.EventDeliveries.CountAsync(d => d.Status == EventDeliveryStatus.Failed, cancellationToken);
        var backlog = new EventBacklog(time.GetUtcNow(), oldestEvent, oldestDelivery, failed);
        metrics.Observe(backlog);
        return backlog;
    }

    private async Task<int> DeleteInChunksAsync<T>(Func<IQueryable<T>> query, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await query().Take(CleanupChunk).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == CleanupChunk);

        return total;
    }

    // Kira: süre ileri atılır, deneme sayısı artar, yeni belirteç yazılır. Etkilenen satır 1 değilse teslim başka işleyicide.
    private async Task<Guid?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        var leaseUntil = now + Lease;
        var claimed = await db.EventDeliveries
            .Where(d => d.Id == id && d.Status == EventDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .Where(d => !(d.Ordered && db.EventDeliveries.Any(p =>
                p.TenantId == d.TenantId && p.Subscription == d.Subscription && p.EntityKey == d.EntityKey && p.RecordId == d.RecordId
                && p.Sequence < d.Sequence && (p.Status == EventDeliveryStatus.Pending || p.Status == EventDeliveryStatus.Failed))))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.NextAttemptAt, leaseUntil)
                    .SetProperty(d => d.AttemptCount, d => d.AttemptCount + 1)
                    .SetProperty(d => d.LeaseToken, token),
                cancellationToken);
        return claimed == 1 ? token : null;
    }

    private async Task DeliverClaimedAsync(Guid id, Guid lease, CancellationToken cancellationToken)
    {
        var delivery = await db.EventDeliveries.AsNoTracking().SingleAsync(d => d.Id == id, cancellationToken);
        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.Id == delivery.EventId, cancellationToken);

        if (await SkipReasonAsync(delivery, cancellationToken) is { } reason)
        {
            await FinishAsync(delivery, lease, EventDeliveryStatus.Skipped, reason, cancellationToken);
            return;
        }

        var (_, subscription) = catalog.FindSubscription(delivery.Subscription)!.Value;
        if (message.SchemaVersion > subscription.MaxSchemaVersion)
        {
            await FailAsync(delivery, lease, new PermanentEventFailureException(
                $"Olayın {message.SchemaVersion}. sürümü desteklenmiyor (abonelik en fazla {subscription.MaxSchemaVersion})."), cancellationToken);
            return;
        }

        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var beating = HeartbeatAsync(delivery, lease, heartbeat.Token);
        try
        {
            var envelope = await EnvelopeAsync(message, subscription, cancellationToken);
            if (await RunSubscriberAsync(delivery, lease, envelope, subscription, cancellationToken))
            {
                metrics.Completed(subscription.Key, EventDeliveryStatus.Succeeded);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Uygulama kapanıyor: kira bitince teslim yeniden alınır.
            throw;
        }
        catch (Exception exception)
        {
            await FailAsync(delivery, lease, exception, CancellationToken.None);
        }
        finally
        {
            await heartbeat.CancelAsync();
            await beating;
        }
    }

    /// <summary>
    /// Abone olayın tenant'ında, sistem kimliğiyle ve açık bir işlemde çalışır. Başarılı dönerse yaptığı değişiklikler,
    /// kuyruğa yazdığı e-postalar ve teslimin başarılı işareti birlikte onaylanır. Kira bu arada başka işleyiciye geçtiyse
    /// işaret yazılamaz ve işlem geri alınır (false).
    /// </summary>
    private async Task<bool> RunSubscriberAsync(
        EventDelivery delivery, Guid lease, EventEnvelope envelope, EventSubscription subscription, CancellationToken cancellationToken)
    {
        await using var scope = tenantScopes.Create(new TenantContext(delivery.TenantId, Guid.Empty, Guid.Empty));
        var services = scope.ServiceProvider;
        services.GetRequiredService<EventHandlingScope>().Begin(envelope, subscription.Key);
        var subscriber = services.GetService(subscription.Subscriber) as IEventSubscriber
            ?? throw new PermanentEventFailureException($"Abone {subscription.Subscriber.Name} kayıtlı değil.");
        var tenantDb = services.GetRequiredService<ModulixDbContext>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SubscriberTimeout);
        await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await subscriber.HandleAsync(envelope, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Abone {SubscriberTimeout.TotalMinutes:0} dakikada bitmedi.");
        }

        // Abonenin kaydetmeden bıraktığı değişiklikler (ör. kuyruğa yazılan e-posta) teslimle birlikte yazılır.
        await tenantDb.SaveChangesAsync(cancellationToken);
        var now = time.GetUtcNow();
        var marked = await tenantDb.EventDeliveries
            .Where(d => d.Id == delivery.Id && d.LeaseToken == lease && d.Status == EventDeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.Status, EventDeliveryStatus.Succeeded)
                    .SetProperty(d => d.CompletedAt, now)
                    .SetProperty(d => d.LastError, (string?)null)
                    .SetProperty(d => d.LeaseToken, (Guid?)null),
                cancellationToken);
        if (marked != 1)
        {
            // Geri alınır (işlem onaylanmadan kapanır); teslim, kirayı alan işleyicide.
            logger.LogWarning(
                "Teslim {DeliveryId} ({Subscription}, olay {EventId}) kirası dolduğu için sonucu yazılmadı; teslim başka işleyicide.",
                delivery.Id, delivery.Subscription, delivery.EventId);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Teslim yapılmadan biter: firma kapalı, aboneliğin modülü kapatıldı veya abonelik koddan kaldırıldı.
    private async Task<string?> SkipReasonAsync(EventDelivery delivery, CancellationToken cancellationToken)
    {
        if (catalog.FindSubscription(delivery.Subscription) is not { } found)
        {
            return "Abonelik artık tanımlı değil.";
        }

        var status = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == delivery.TenantId)
            .Select(t => (TenantStatus?)t.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status != TenantStatus.Active)
        {
            return "Firma kapalı.";
        }

        var modules = await EnabledModulesAsync(delivery.TenantId, cancellationToken);
        return catalog.IsEnabled(found.ModuleKey, modules) ? null : "Aboneliğin modülü firmada kapalı.";
    }

    /// <summary>
    /// Aboneye giden zarf: aboneliğin izinleriyle görülemeyen hassas alanlar (Y8b) farktan çıkarılır, yalnızca adları
    /// verilir. Değere ihtiyaç duyan abone ilgili hassas alan iznini bildirmek zorundadır.
    /// </summary>
    private async Task<EventEnvelope> EnvelopeAsync(OutboxMessage message, EventSubscription subscription, CancellationToken cancellationToken)
    {
        var granted = catalog.EffectivePermissions(subscription.Permissions, await EnabledModulesAsync(message.TenantId, cancellationToken));
        var hidden = catalog.HiddenFields(message.EntityKey, granted);
        var payload = JsonNode.Parse(message.Payload) as JsonObject ?? [];
        var removed = hidden.Where(payload.Remove).Order(StringComparer.Ordinal).ToList();

        return new EventEnvelope(
            message.Id,
            message.TenantId,
            message.EventType,
            message.SchemaVersion,
            message.EntityKey,
            message.RecordId,
            message.Sequence,
            message.UserId,
            message.Source,
            message.CorrelationId,
            message.CausationId,
            message.Depth,
            payload,
            message.OccurredAt)
        {
            CompanyId = message.CompanyId,
            Reason = message.Reason,
            HiddenFields = removed,
        };
    }

    private async Task<IReadOnlySet<string>> EnabledModulesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsEnabled)
            .Select(e => e.ModuleKey)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Başarısız deneme: kalıcı hatada veya deneme hakkı bittiyse <see cref="EventDeliveryStatus.Failed"/>, değilse artan
    /// aralıkla (±%20) yeniden denenir. Kira başka işleyiciye geçtiyse bir şey yazılmaz.
    /// </summary>
    private async Task FailAsync(EventDelivery delivery, Guid lease, Exception exception, CancellationToken cancellationToken)
    {
        var attempts = delivery.AttemptCount;
        var failed = exception is PermanentEventFailureException || attempts >= MaxAttempts;
        var error = $"{exception.GetType().Name}: {exception.Message}";
        if (error.Length > EventDelivery.LastErrorMaxLength)
        {
            error = error[..EventDelivery.LastErrorMaxLength];
        }

        var now = time.GetUtcNow();
        var next = failed ? now : now + Jitter(RetryDelay(attempts));
        await db.EventDeliveries
            .Where(d => d.Id == delivery.Id && d.LeaseToken == lease && d.Status == EventDeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.Status, failed ? EventDeliveryStatus.Failed : EventDeliveryStatus.Pending)
                    .SetProperty(d => d.NextAttemptAt, next)
                    .SetProperty(d => d.CompletedAt, failed ? now : null)
                    .SetProperty(d => d.LastError, error)
                    .SetProperty(d => d.LeaseToken, (Guid?)null),
                cancellationToken);
        metrics.Completed(delivery.Subscription, failed ? EventDeliveryStatus.Failed : EventDeliveryStatus.Pending);

        // Payload ve alan değeri yazılmaz; abone istisna iletisine kişisel veri koymaz.
        logger.LogWarning(
            exception,
            "Teslim {DeliveryId} başarısız ({Subscription}, deneme {Attempt}, {Outcome}). Olay {EventId}, tenant {TenantId}, kayıt {EntityKey} {RecordId}.",
            delivery.Id,
            delivery.Subscription,
            attempts,
            failed ? "dead-letter" : "yeniden denenecek",
            delivery.EventId,
            delivery.TenantId,
            delivery.EntityKey,
            delivery.RecordId);
    }

    private async Task FinishAsync(EventDelivery delivery, Guid lease, EventDeliveryStatus status, string note, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.EventDeliveries
            .Where(d => d.Id == delivery.Id && d.LeaseToken == lease && d.Status == EventDeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.Status, status)
                    .SetProperty(d => d.CompletedAt, now)
                    .SetProperty(d => d.LastError, note)
                    .SetProperty(d => d.LeaseToken, (Guid?)null),
                cancellationToken);
        metrics.Completed(delivery.Subscription, status);
    }

    // Abone çalışırken kira, ayrı bağlantıyla ve belirteç şartıyla uzatılır; işleyici çökerse uzatma durur.
    private async Task HeartbeatAsync(EventDelivery delivery, Guid lease, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, time, cancellationToken);
                await using var scope = scopes.CreateAsyncScope();
                var leaseUntil = time.GetUtcNow() + Lease;
                await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EventDeliveries
                    .Where(d => d.Id == delivery.Id && d.LeaseToken == lease && d.Status == EventDeliveryStatus.Pending)
                    .ExecuteUpdateAsync(set => set.SetProperty(d => d.NextAttemptAt, leaseUntil), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Abone bitti.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Teslim {DeliveryId} kirası uzatılamadı.", delivery.Id);
        }
    }

    private static TimeSpan Jitter(TimeSpan delay) => delay * (0.8 + (Random.Shared.NextDouble() * 0.4));
}

public static class EventSetup
{
    /// <summary>
    /// Olay işleyicisi (ADR-0007). Ayarları host bağlar
    /// (<c>services.Configure&lt;EventOptions&gt;(configuration.GetSection(EventOptions.SectionName))</c>).
    /// </summary>
    public static IServiceCollection AddModulixEvents(this IServiceCollection services)
    {
        services.AddSingleton<EventMetrics>();
        services.AddScoped<EventDispatcher>();
        services.AddScoped<IEventAdministration, EventAdministration>();
        return services;
    }
}
