using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Events;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Platform;

internal sealed class PlatformHealth(ModulixDbContext db, IEventAdministration events, TimeProvider time) : IPlatformHealth
{
    private static readonly string[] SecurityActions =
    [
        PlatformAuditActions.UserTwoFactorReset, PlatformAuditActions.TeamChanged, PlatformAuditActions.TenantSuspended, PlatformAuditActions.TenantClosed,
    ];

    public async Task<PlatformHealthReport> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var day = now.AddHours(-24);

        var eventSummary = await events.GetSummaryAsync(cancellationToken);
        var emails = db.Set<QueuedEmail>().AsNoTracking();
        var emailQueue = new QueueHealth(
            await emails.CountAsync(e => e.Status == EmailStatus.Pending, cancellationToken),
            await emails.CountAsync(e => e.Status == EmailStatus.Failed, cancellationToken),
            await emails.Where(e => e.Status == EmailStatus.Pending).MinAsync(e => (DateTimeOffset?)e.CreatedAt, cancellationToken));

        var deliveries = db.Set<EventDelivery>().AsNoTracking().Where(d => d.CompletedAt != null && d.CompletedAt >= day);
        var eventOutcome = new OutcomeStats(
            await deliveries.CountAsync(d => d.Status == EventDeliveryStatus.Succeeded, cancellationToken),
            await deliveries.CountAsync(d => d.Status == EventDeliveryStatus.Failed, cancellationToken));

        // E-postada başarısızlık anı saklanmaz: son 24 saatte kuyruğa girip kalıcı olarak başarısız olanlar sayılır.
        var emailOutcome = new OutcomeStats(
            await emails.CountAsync(e => e.Status == EmailStatus.Sent && e.SentAt >= day, cancellationToken),
            await emails.CountAsync(e => e.Status == EmailStatus.Failed && e.CreatedAt >= day, cancellationToken));

        return new PlatformHealthReport(
            now,
            await VersionAsync(cancellationToken),
            emailQueue,
            new EventQueueHealth(eventSummary.Pending, eventSummary.Held, eventSummary.Failed, eventSummary.OldestUndispatched, eventSummary.OldestPending),
            eventOutcome,
            emailOutcome,
            await UsageAsync(now, cancellationToken),
            await InactiveAsync(now, cancellationToken),
            await SecurityAsync(now, cancellationToken));
    }

    private async Task<VersionInfo> VersionAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(PlatformHealth).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "?";
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        return new VersionInfo(version.Split('+')[0], RuntimeInformation.FrameworkDescription, applied.LastOrDefault(), pending);
    }

    // Son giriş zamanı üyelikte (platform tablosu) tutulur; firma verisine bakılmaz.
    private async Task<Dictionary<Guid, DateTimeOffset?>> LastSignInsAsync(CancellationToken cancellationToken)
    {
        var operational = db.Tenants.AsNoTracking().Where(t => TenantStatusRules.Operational.Contains(t.Status)).Select(t => t.Id);
        return await db.Memberships.AsNoTracking()
            .Where(m => operational.Contains(m.TenantId))
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, Last = g.Max(m => m.LastSignInAt) })
            .ToDictionaryAsync(g => g.TenantId, g => g.Last, cancellationToken);
    }

    private async Task<UsageSummary> UsageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operational = await db.Tenants.AsNoTracking().CountAsync(t => TenantStatusRules.Operational.Contains(t.Status), cancellationToken);
        var members = await db.Memberships.AsNoTracking().CountAsync(
            m => m.Status == MembershipStatus.Active && db.Tenants.Any(t => t.Id == m.TenantId && TenantStatusRules.Operational.Contains(t.Status)), cancellationToken);
        var lastSignIns = await LastSignInsAsync(cancellationToken);
        var sessions = await db.Set<UserSession>().AsNoTracking().CountAsync(s => s.LastSeenAt >= now.AddHours(-24), cancellationToken);
        return new UsageSummary(
            operational,
            members,
            lastSignIns.Values.Count(v => v >= now.AddDays(-7)),
            lastSignIns.Values.Count(v => v >= now.AddDays(-30)),
            sessions);
    }

    private async Task<IReadOnlyList<InactiveTenant>> InactiveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lastSignIns = await LastSignInsAsync(cancellationToken);
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => TenantStatusRules.Operational.Contains(t.Status))
            .Select(t => new { t.Id, t.Name, t.Slug })
            .ToListAsync(cancellationToken);
        return
        [
            .. tenants
                .Select(t => new InactiveTenant(t.Id, t.Name, t.Slug, lastSignIns.GetValueOrDefault(t.Id)))
                .Where(t => t.LastSignInAt is null || t.LastSignInAt < now.AddDays(-30))
                .OrderBy(t => t.LastSignInAt ?? DateTimeOffset.MinValue)
                .ThenBy(t => t.Name)
                .Take(10),
        ];
    }

    private async Task<IReadOnlyList<SecurityEventCount>> SecurityAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now.AddDays(-7);
        var counts = await db.PlatformAuditLog.AsNoTracking()
            .Where(e => e.OccurredAt >= since && SecurityActions.Contains(e.Action))
            .GroupBy(e => e.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Action, g => g.Count, cancellationToken);
        return [.. SecurityActions.Select(a => new SecurityEventCount(a, counts.GetValueOrDefault(a)))];
    }
}
