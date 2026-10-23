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
