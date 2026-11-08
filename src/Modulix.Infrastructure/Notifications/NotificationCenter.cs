using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Notifications;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Notifications;

/// <summary>Bildirim kutusu (Ç5a). Tenant query filter ve RLS ile, kişi de her sorguda aktif kullanıcıyla sınırlanır.</summary>
internal sealed class NotificationCenter(ModulixDbContext db, ITenantContextAccessor tenantAccessor, TimeProvider time) : INotificationInbox, INotificationCenter
{
    private const int MaxTake = 50;

    public async Task PostAsync(InboxMessage message, CancellationToken cancellationToken)
    {
        // Aynı işlemde eklenmiş ama henüz kaydedilmemiş bildirim de tekrar sayılır.
        bool pending = db.ChangeTracker.Entries<Notification>()
            .Any(e => e.Entity.UserId == message.UserId && e.Entity.DedupeKey == message.DedupeKey);
        if (pending || await db.Notifications.AsNoTracking().AnyAsync(n => n.UserId == message.UserId && n.DedupeKey == message.DedupeKey, cancellationToken))
        {
            return;
        }

        db.Notifications.Add(Notification.Create(message, time.GetUtcNow()));
    }

    public async Task<NotificationPage> ListAsync(int take, CancellationToken cancellationToken)
    {
        Guid userId = RequireUser();
        List<Notification> items = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id)
            .Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(cancellationToken);
        return new NotificationPage(items, await UnreadCountAsync(cancellationToken));
    }

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken)
    {
        Guid userId = RequireUser();
        return db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);
    }

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        Guid userId = RequireUser();
        Notification? notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.UserId == userId, cancellationToken);
        if (notification is null)
        {
            return false;
        }

        notification.MarkRead(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken)
    {
        Guid userId = RequireUser();
        DateTimeOffset now = time.GetUtcNow();
        await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
    }

    private Guid RequireUser() =>
        tenantAccessor.Current?.UserId ?? throw new InvalidOperationException("Aktif tenant olmadan bildirimler okunamaz.");
}
