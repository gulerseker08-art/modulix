namespace Modulix.Core.Contracts.Notifications;

/// <summary>
/// Gönderilecek e-posta. <see cref="HtmlBody"/> zorunludur; <see cref="TextBody"/> verilmezse
/// yalnızca HTML gönderilir. İçerik çağıranın sorumluluğundadır: kullanıcı girdisi HTML'e
/// eklenmeden önce kodlanmalıdır (ör. <c>System.Net.WebUtility.HtmlEncode</c>).
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody)
{
    public string? ToName { get; init; }

    public string? TextBody { get; init; }
}

/// <summary>
/// E-posta kuyruğu. E-posta hemen gönderilmez: kuyruğa yazılır ve arka plandaki işleyici gönderir;
/// SMTP sunucusuna ulaşılamazsa artan aralıklarla tekrar dener. Aktif tenant varsa e-posta o tenant'a
/// bağlanır.
/// </summary>
public interface IEmailQueue
{
    /// <summary>
    /// E-postayı kuyruğa ekler. Kayıt, çağıranın bir sonraki kaydetmesiyle (kayıt boru hattı veya
    /// <c>SaveChangesAsync</c>) aynı işlemde yazılır; işlem geri alınırsa e-posta da gönderilmez.
    /// Geçersiz adres, boş konu veya sınırı aşan içerik için <see cref="ArgumentException"/> fırlatır.
    /// </summary>
    void Enqueue(EmailMessage message);
}

/// <summary>Bildirimin alıcısı: aktif tenant'ın etkin üyesi.</summary>
public sealed record NotificationRecipient(Guid UserId, string DisplayName, string Email);

/// <summary>
/// İnsana giden içerik (ADR-0007): e-posta veya bildirim metnine kaydın bilgisi alıcının gönderim anındaki yetkisine göre
/// konur; olay aboneliğinin izni alıcının izni değildir. Aktif tenant'ta çalışır.
/// </summary>
public interface INotificationRecipients
{
    /// <summary>Aktif tenant'ın etkin üyesiyse adı ve e-postası; değilse (üyeliği kapalı, başka tenant'ın kişisi) null.</summary>
    Task<NotificationRecipient?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Kaydı alıcının o anki yetkisiyle okur: izin, kayıt kapsamı ve hassas alanlar kişinin kendi isteğindeki gibi
    /// uygulanır. Alıcı kaydı göremiyorsa null.
    /// </summary>
    Task<Records.RecordData?> ReadRecordAsync(NotificationRecipient recipient, Records.RecordRef record, CancellationToken cancellationToken);

    /// <summary>Uygulamanın genel adresiyle (App:PublicUrl) arayüz bağlantısı (ör. "crm/firmalar/…"); adres ayarlı değilse null.</summary>
    string? Link(string path);
}
