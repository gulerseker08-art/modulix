namespace Modulix.Core.Contracts.Notifications;

/// <summary>
/// Uygulama içi bildirim (Ç5a). <see cref="Title"/> kaydın hassas içeriğini taşımaz: alıcının kayda erişimi sonradan kalkabilir, ayrıntı
/// bağlantıyla açılır ve yetki orada denetlenir. <see cref="DedupeKey"/> aynı alıcıya aynı olay için ikinci bildirimi engeller.
/// </summary>
public sealed record InboxMessage(Guid UserId, string Kind, string Title, string? Link, string DedupeKey);

/// <summary>Bildirim kutusu: kayıt, çağıranın bir sonraki kaydetmesiyle aynı işlemde yazılır; işlem geri alınırsa bildirim de düşer.</summary>
public interface INotificationInbox
{
    Task PostAsync(InboxMessage message, CancellationToken cancellationToken);
}
