using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Notifications;

/// <summary>Uygulama içi bildirim (ADR-0027 Dilim 4b, Ç5a). Tenant verisidir ve yalnızca alıcısına görünür.</summary>
public sealed class Notification : ITenantEntity, ICompanyShared
{
    public const int KindMaxLength = 50;
    public const int TitleMaxLength = 200;
    public const int LinkMaxLength = 300;
    public const int DedupeKeyMaxLength = 150;

    private Notification()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string? Link { get; private set; }

    public string DedupeKey { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(InboxMessage message, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = message.UserId,
        Kind = message.Kind,
        Title = message.Title.Length > TitleMaxLength ? message.Title[..TitleMaxLength] : message.Title,
        Link = message.Link,
        DedupeKey = message.DedupeKey,
        CreatedAt = now,
    };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

public sealed record NotificationPage(IReadOnlyList<Notification> Items, int UnreadCount);

/// <summary>Kişinin kendi bildirimleri: bütün işlemler sorgu seviyesinde aktif kullanıcıyla sınırlıdır.</summary>
public interface INotificationCenter
{
    Task<NotificationPage> ListAsync(int take, CancellationToken cancellationToken);

    Task<int> UnreadCountAsync(CancellationToken cancellationToken);

    /// <summary>Bildirim yoksa ya da başkasınınsa false.</summary>
    Task<bool> MarkReadAsync(Guid id, CancellationToken cancellationToken);

    Task MarkAllReadAsync(CancellationToken cancellationToken);
}
