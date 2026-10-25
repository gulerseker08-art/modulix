using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
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
    ModulixDbContext db,
    ModuleCatalog catalog,
    ITenantScopeFactory tenantScopes,
    TimeProvider time) : IEventAdministration
{
    public const int MaxPageSize = 100;

    /// <summary>Denetim kaydındaki kayıt türü; kaydın kimliği teslimin kimliğidir.</summary>
    public const string AuditEntityKey = CoreModule.AuditedEntityKeys.EventDelivery;

    public const string SkippedByAdministrator = "Platform yöneticisi atladı.";

    public async Task<EventSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var pending = Rows().Where(r => r.Delivery.Status == EventDeliveryStatus.Pending);
        return new EventSummary(
            await pending.CountAsync(r => !r.Blocked, cancellationToken),
            await pending.CountAsync(r => r.Blocked, cancellationToken),
            await db.EventDeliveries.CountAsync(d => d.Status == EventDeliveryStatus.Failed, cancellationToken),
            await db.OutboxMessages.Where(o => o.DispatchedAt == null).MinAsync(o => (DateTimeOffset?)o.OccurredAt, cancellationToken),
            await db.EventDeliveries.Where(d => d.Status == EventDeliveryStatus.Pending).MinAsync(d => (DateTimeOffset?)d.CreatedAt, cancellationToken),
            [.. catalog.EventSubscriptions.Select(s => s.Subscription.Key)],
            [.. DomainEventTypes.SchemaVersions.Keys.Order(StringComparer.Ordinal)]);
    }

    public async Task<EventDeliveryPage> ListAsync(EventDeliveryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var rows = Filter(query);
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderByDescending(r => r.Delivery.CreatedAt).ThenByDescending(r => r.Delivery.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new
            {
                r.Delivery,
                r.EventType,
                r.TenantName,
                r.Blocked,
                HoldingBack = r.Delivery.Status == EventDeliveryStatus.Failed && r.Delivery.Ordered
                    ? db.EventDeliveries.Count(n =>
                        n.TenantId == r.Delivery.TenantId && n.Subscription == r.Delivery.Subscription && n.EntityKey == r.Delivery.EntityKey
                        && n.RecordId == r.Delivery.RecordId && n.Sequence > r.Delivery.Sequence && n.Status == EventDeliveryStatus.Pending)
                    : 0,
            })
            .ToListAsync(cancellationToken);

        return new EventDeliveryPage(
            [.. items.Select(i => new EventDeliveryItem(
                i.Delivery.Id,
                i.Delivery.EventId,
                i.Delivery.TenantId,
                i.TenantName,
                i.Delivery.Subscription,
                i.EventType,
                i.Delivery.EntityKey,
                i.Delivery.RecordId,
                i.Delivery.Sequence,
                StateOf(i.Delivery.Status, i.Blocked),
                i.Delivery.AttemptCount,
                i.Delivery.CreatedAt,
                i.Delivery.NextAttemptAt,
                i.Delivery.CompletedAt,
                i.Delivery.LastError,
                i.HoldingBack))],
            page,
            pageSize,
            total);
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
        var selected = ids is { Count: > 0 }
            ? await db.EventDeliveries.Where(d => ids.Contains(d.Id)).Select(d => d.Id).Take(IEventAdministration.MaxBulk).ToListAsync(cancellationToken)
            : filter is not null
                ? await Filter(filter).OrderBy(r => r.Delivery.CreatedAt).Select(r => r.Delivery.Id).Take(IEventAdministration.MaxBulk).ToListAsync(cancellationToken)
                : [];

        var deliveries = await (
            from d in db.EventDeliveries.AsNoTracking()
            join e in db.OutboxMessages.AsNoTracking() on d.EventId equals e.Id
            where selected.Contains(d.Id)
            select new { Delivery = d, e.EventType })
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var tenant in deliveries.GroupBy(d => d.Delivery.TenantId))
        {
            await using var scope = tenantScopes.Create(new TenantContext(tenant.Key, actingUserId, Guid.Empty));
            var tenantDb = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);

            foreach (var (delivery, eventType) in tenant.Select(t => (t.Delivery, t.EventType)))
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
            }

            await tenantDb.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return changed;
    }

    private IQueryable<Row> Filter(EventDeliveryQuery query)
    {
        var rows = Rows();
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

    // Teslim, olayın türü, firmanın adı ve sıralı teslimin önceki bitmemiş teslimi bekleyip beklemediği.
    private IQueryable<Row> Rows() =>
        from d in db.EventDeliveries.AsNoTracking()
        join e in db.OutboxMessages.AsNoTracking() on d.EventId equals e.Id
        join t in db.Tenants.AsNoTracking() on d.TenantId equals t.Id
        select new Row
        {
            Delivery = d,
            EventType = e.EventType,
            TenantName = t.Name,
            Blocked = d.Ordered && db.EventDeliveries.Any(p =>
                p.TenantId == d.TenantId && p.Subscription == d.Subscription && p.EntityKey == d.EntityKey && p.RecordId == d.RecordId
                && p.Sequence < d.Sequence && (p.Status == EventDeliveryStatus.Pending || p.Status == EventDeliveryStatus.Failed)),
        };

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
