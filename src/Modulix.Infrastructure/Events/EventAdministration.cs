using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Events;

/// <summary>
/// Olaylar platform ekranı (ADR-0007). Satırlarda olay türü, kayıt türü ve kimliği, abonelik, deneme sayısı ve son hata
/// görünür; payload okunmaz. Yeniden deneme ve atlama teslimin tenant'ının bağlamında, denetim kaydıyla aynı işlemde
/// yazılır.
/// </summary>
internal sealed class EventAdministration(
    PlatformDbContext platform,
    ModuleCatalog catalog,
    ITenantScopeFactory tenantScopes,
    TimeProvider time) : IEventAdministration
{
    public const int MaxPageSize = 100;

    /// <summary>Denetim kaydındaki kayıt türü; kaydın kimliği teslimin kimliğidir.</summary>
    public const string AuditEntityKey = CoreModule.AuditedEntityKeys.EventDelivery;

    public const string SkippedByAdministrator = "Platform yöneticisi atladı.";

    // ADR-0009: outbox ve teslimler tenant başına ayrı veritabanındadır. Platform ekranı bir sorguyla bütün tenant'ları
    // göremez; kurulumu tamamlanmış tenant'lar üzerinde dönülür ve sonuç bellekte birleştirilip süzülür/sayfalanır.
    public async Task<EventSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var rows = await AllRowsAsync(tenantId: null, cancellationToken);
        var pending = rows.Where(r => r.Delivery.Status == EventDeliveryStatus.Pending).ToList();
        DateTimeOffset? oldestEvent = null;
        foreach (var tenant in await ActiveTenantsAsync(tenantId: null, cancellationToken))
        {
            try
            {
                await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, Guid.Empty, Guid.Empty));
                var tenantDb = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
                var oldest = await tenantDb.OutboxMessages.Where(o => o.DispatchedAt == null)
                    .MinAsync(o => (DateTimeOffset?)o.OccurredAt, cancellationToken);
                if (oldest is { } at && (oldestEvent is null || at < oldestEvent))
                {
                    oldestEvent = at;
                }
            }
            catch (Exception)
            {
                // Ulaşılamayan tenant atlanır (ADR-0009 sınırlaması).
            }
        }

        return new EventSummary(
            pending.Count(r => !r.Blocked),
            pending.Count(r => r.Blocked),
            rows.Count(r => r.Delivery.Status == EventDeliveryStatus.Failed),
            oldestEvent,
            pending.Count == 0 ? null : pending.Min(r => r.Delivery.CreatedAt),
            [.. catalog.EventSubscriptions.Select(s => s.Subscription.Key)],
            [.. DomainEventTypes.SchemaVersions.Keys.Order(StringComparer.Ordinal)]);
    }

    public async Task<EventDeliveryPage> ListAsync(EventDeliveryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var all = await AllRowsAsync(query.TenantId, cancellationToken);
        var rows = Filter(all, query).ToList();
        var total = rows.Count;
        var items = rows
            .OrderByDescending(r => r.Delivery.CreatedAt).ThenByDescending(r => r.Delivery.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new EventDeliveryItem(
                r.Delivery.Id,
                r.Delivery.EventId,
                r.Delivery.TenantId,
                r.TenantName,
                r.Delivery.Subscription,
                r.EventType,
                r.Delivery.EntityKey,
                r.Delivery.RecordId,
                r.Delivery.Sequence,
                StateOf(r.Delivery.Status, r.Blocked),
                r.Delivery.AttemptCount,
                r.Delivery.CreatedAt,
                r.Delivery.NextAttemptAt,
                r.Delivery.CompletedAt,
                r.Delivery.LastError,
                r.Delivery.Status == EventDeliveryStatus.Failed && r.Delivery.Ordered
                    ? all.Count(n => n.Delivery.TenantId == r.Delivery.TenantId && n.Delivery.Subscription == r.Delivery.Subscription
                        && n.Delivery.EntityKey == r.Delivery.EntityKey && n.Delivery.RecordId == r.Delivery.RecordId
                        && n.Delivery.Sequence > r.Delivery.Sequence && n.Delivery.Status == EventDeliveryStatus.Pending)
                    : 0))
            .ToList();

        return new EventDeliveryPage([.. items], page, pageSize, total);
    }

    public Task<int> RetryAsync(IReadOnlyCollection<Guid>? ids, EventDeliveryQuery? filter, Guid actingUserId, CancellationToken cancellationToken) =>
        ChangeAsync(ids, filter, actingUserId, EventDeliveryStatus.Pending, cancellationToken);

    public Task<int> SkipAsync(IReadOnlyCollection<Guid>? ids, EventDeliveryQuery? filter, Guid actingUserId, CancellationToken cancellationToken) =>
        ChangeAsync(ids, filter, actingUserId, EventDeliveryStatus.Skipped, cancellationToken);

    /// <summary>
    /// Bekleyen (kiralı değilse) veya başarısız teslimi yeniden zamanlar ya da atlar. Teslimler tenant'larına göre
    /// gruplanır; her tenant'ta durum değişikliği ve denetim kaydı aynı işlemdedir.
    /// </summary>
    private async Task<int> ChangeAsync(
        IReadOnlyCollection<Guid>? ids, EventDeliveryQuery? filter, Guid actingUserId, EventDeliveryStatus target, CancellationToken cancellationToken)
    {
        // ADR-0009: teslimler tenant başına ayrı veritabanındadır; her tenant kendi bağlamında, denetim kaydıyla aynı
        // işlemde değiştirilir. Toplu sınır bütün tenant'lara yayılır.
        var budget = IEventAdministration.MaxBulk;
        var changed = 0;
        foreach (var tenantRef in await ActiveTenantsAsync(filter?.TenantId, cancellationToken))
        {
            if (budget <= 0)
            {
                break;
            }

            await using var scope = tenantScopes.Create(new TenantContext(tenantRef.Id, actingUserId, Guid.Empty));
            var tenantDb = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

            List<Guid> selected;
            try
            {
                if (ids is { Count: > 0 })
                {
                    selected = await tenantDb.EventDeliveries.Where(d => ids.Contains(d.Id)).Select(d => d.Id).Take(budget).ToListAsync(cancellationToken);
                }
                else if (filter is not null)
                {
                    var rows = await RowsForTenantAsync(tenantRef, tenantDb, cancellationToken);
                    selected = Filter(rows, filter).OrderBy(r => r.Delivery.CreatedAt).Select(r => r.Delivery.Id).Take(budget).ToList();
                }
                else
                {
                    selected = [];
                }
            }
            catch (Exception)
            {
                // Ulaşılamayan tenant atlanır (ADR-0009 sınırlaması); diğer tenant'lar etkilenmez.
                continue;
            }

            if (selected.Count == 0)
            {
                continue;
            }

            var deliveries = await (
                from d in tenantDb.EventDeliveries.AsNoTracking()
                join e in tenantDb.OutboxMessages.AsNoTracking() on d.EventId equals e.Id
                where selected.Contains(d.Id)
                select new { Delivery = d, e.EventType })
                .ToListAsync(cancellationToken);

            await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);
            foreach (var (delivery, eventType) in deliveries.Select(t => (t.Delivery, t.EventType)))
            {
                var now = time.GetUtcNow();
                var open = tenantDb.EventDeliveries.Where(d =>
                    d.Id == delivery.Id
                    && (d.Status == EventDeliveryStatus.Failed
                        || (d.Status == EventDeliveryStatus.Pending && (d.LeaseToken == null || d.NextAttemptAt <= now))));
                var updated = target == EventDeliveryStatus.Pending
                    ? await open.ExecuteUpdateAsync(
                        set => set
                            .SetProperty(d => d.Status, EventDeliveryStatus.Pending)
                            .SetProperty(d => d.AttemptCount, 0)
                            .SetProperty(d => d.NextAttemptAt, now)
                            .SetProperty(d => d.CompletedAt, (DateTimeOffset?)null)
                            .SetProperty(d => d.LastError, (string?)null)
                            .SetProperty(d => d.LeaseToken, (Guid?)null),
                        cancellationToken)
                    : await open.ExecuteUpdateAsync(
                        set => set
                            .SetProperty(d => d.Status, EventDeliveryStatus.Skipped)
                            .SetProperty(d => d.CompletedAt, now)
                            .SetProperty(d => d.LastError, SkippedByAdministrator)
                            .SetProperty(d => d.LeaseToken, (Guid?)null),
                        cancellationToken);
                if (updated != 1)
                {
                    continue;
                }

                audit.Add(new AuditEntry(
                    delivery.TenantId,
                    actingUserId,
                    AuditEntityKey,
                    delivery.Id,
                    AuditAction.Updated,
                    RecordDiff.Compute(
                        new JsonObject { ["status"] = delivery.Status.ToString(), ["attemptCount"] = delivery.AttemptCount },
                        new JsonObject
                        {
                            ["status"] = target.ToString(),
                            ["attemptCount"] = target == EventDeliveryStatus.Pending ? 0 : delivery.AttemptCount,
                            ["subscription"] = delivery.Subscription,
                            ["eventId"] = delivery.EventId.ToString(),
                            ["eventType"] = eventType,
                            ["entityKey"] = delivery.EntityKey,
                            ["recordId"] = delivery.RecordId.ToString(),
                        }),
                    ExecutionSource.Api,
                    Activity.Current?.TraceId.ToString()));
                changed++;
                budget--;
            }

            await tenantDb.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return changed;
    }

    private static IEnumerable<Row> Filter(IEnumerable<Row> rows, EventDeliveryQuery query)
    {
        if (query.TenantId is { } tenantId)
        {
            rows = rows.Where(r => r.Delivery.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Subscription))
        {
            rows = rows.Where(r => r.Delivery.Subscription == query.Subscription);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            rows = rows.Where(r => r.EventType == query.EventType);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(r => r.Delivery.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(r => r.Delivery.CreatedAt < to);
        }

        return query.State switch
        {
            null => rows,
            EventDeliveryState.Pending => rows.Where(r => r.Delivery.Status == EventDeliveryStatus.Pending && !r.Blocked),
            EventDeliveryState.Held => rows.Where(r => r.Delivery.Status == EventDeliveryStatus.Pending && r.Blocked),
            var state => rows.Where(r => r.Delivery.Status == (EventDeliveryStatus)(int)state),
        };
    }

    /// <summary>Kurulumu tamamlanmış tenant'lar (ADR-0009); <paramref name="tenantId"/> verilirse yalnızca o tenant.</summary>
    private async Task<IReadOnlyList<TenantRef>> ActiveTenantsAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var query = platform.Tenants.AsNoTracking().Where(t => t.ProvisioningStatus == TenantProvisioningStatus.Active);
        if (tenantId is { } id)
        {
            query = query.Where(t => t.Id == id);
        }

        return await query.Select(t => new TenantRef(t.Id, t.Name)).ToListAsync(cancellationToken);
    }

    /// <summary>Bütün (veya seçili) tenant'ların teslim satırlarını tenant başına okuyup bellekte birleştirir.</summary>
    private async Task<List<Row>> AllRowsAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        foreach (var tenant in await ActiveTenantsAsync(tenantId, cancellationToken))
        {
            try
            {
                await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, Guid.Empty, Guid.Empty));
                var tenantDb = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
                rows.AddRange(await RowsForTenantAsync(tenant, tenantDb, cancellationToken));
            }
            catch (Exception)
            {
                // Bir tenant'ın veritabanına ulaşılamazsa ekran tümden düşmez; o tenant atlanır (ADR-0009 sınırlaması).
            }
        }

        return rows;
    }

    // Teslim, olayın türü, firmanın adı ve sıralı teslimin önceki bitmemiş teslimi bekleyip beklemediği (tenant DB'si).
    private static async Task<List<Row>> RowsForTenantAsync(TenantRef tenant, ModulixDbContext tenantDb, CancellationToken cancellationToken) =>
        await (
            from d in tenantDb.EventDeliveries.AsNoTracking()
            join e in tenantDb.OutboxMessages.AsNoTracking() on d.EventId equals e.Id
            select new Row
            {
                Delivery = d,
                EventType = e.EventType,
                TenantName = tenant.Name,
                Blocked = d.Ordered && tenantDb.EventDeliveries.Any(p =>
                    p.TenantId == d.TenantId && p.Subscription == d.Subscription && p.EntityKey == d.EntityKey && p.RecordId == d.RecordId
                    && p.Sequence < d.Sequence && (p.Status == EventDeliveryStatus.Pending || p.Status == EventDeliveryStatus.Failed)),
            })
            .ToListAsync(cancellationToken);

    private sealed record TenantRef(Guid Id, string Name);

    private static EventDeliveryState StateOf(EventDeliveryStatus status, bool blocked) =>
        status == EventDeliveryStatus.Pending && blocked ? EventDeliveryState.Held : (EventDeliveryState)(int)status;

    private sealed class Row
    {
        public required EventDelivery Delivery { get; init; }

        public required string EventType { get; init; }

        public required string TenantName { get; init; }

        public required bool Blocked { get; init; }
    }
}
