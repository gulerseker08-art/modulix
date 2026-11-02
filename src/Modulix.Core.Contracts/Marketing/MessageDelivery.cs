using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Contracts.Marketing;

// Gönderim sözleşmeleri (ADR-0025, dilim 5). Pazarlama modülü kanal sağlayıcısını bilmez: iletiyi bu sözleşmeyle teslim eder. Gerçek bir sağlayıcı
// (SMTP, SMS, WhatsApp) yalnızca yapılandırıldığında ve açıkça seçildiğinde kullanılır; bu sürümde yalnızca iletiyi hiçbir yere göndermeyen test
// sağlayıcısı vardır.

public enum ProviderOutcome
{
    /// <summary>Sağlayıcı iletiyi kabul etti. Bu "teslim edildi" demek değildir; teslim bilgisi sağlayıcı olaylarıyla (sonraki dilim) gelir.</summary>
    Accepted = 1,

    /// <summary>Geçici hata (ör. bağlantı, hız sınırı): sınırlı yeniden denenebilir. Sağlayıcı iletiyi kabul etmedi.</summary>
    TransientFailure = 2,

    /// <summary>Kalıcı hata (ör. adres reddedildi): yeniden denenmez.</summary>
    PermanentFailure = 3,

    /// <summary>
    /// Sonuç bilinmiyor (ör. kabulden sonra zaman aşımı, yanıt kayboldu). Kör yeniden deneme çift gönderim yaratabileceğinden otomatik denenmez; durum
    /// sağlayıcı sorgusu ya da olayıyla netleştirilir.
    /// </summary>
    Unknown = 4,
}

/// <param name="IdempotencyKey">Aynı gönderim için aynı anahtar: sağlayıcı destekliyorsa çift gönderimi engeller.</param>
public sealed record MessageRequest(string IdempotencyKey, CommunicationChannel Channel, string Address, string? Subject, string Body);

public sealed record ProviderResult(ProviderOutcome Outcome, string? ProviderMessageId = null, string? ErrorCode = null);

/// <summary>Bir kanal sağlayıcısı. Modül <c>ConfigureServices</c>'te scoped kaydeder; hangisinin kullanılacağı tenant ayarından gelir.</summary>
public interface IMessageProvider
{
    /// <summary>Ayarda seçilen anahtar (ör. "fake").</summary>
    string Key { get; }

    bool Supports(CommunicationChannel channel);

    Task<ProviderResult> SendAsync(MessageRequest request, CancellationToken cancellationToken);
}

/// <summary>Bir hedefin (ve varsa iletişim noktasının) gönderim anındaki adresi. Adres anlık görüntüde saklanmaz; gönderimden önce yeniden çözülür.</summary>
public sealed record AddressLookup(Guid TargetId, Guid? ContactPointId);

public sealed record ResolvedAddress(Guid TargetId, Guid? ContactPointId, string Address);

/// <summary>
/// Gönderimden hemen önce adresi yeniden çözer: iletişim noktası silinmiş, pasifleşmiş ya da kişi artık erişilemiyorsa sonuçta yer almaz (o alıcı atlanır).
/// Böylece pasif kayıt ya da değişmiş adres eski anlık görüntüyle gönderilmez.
/// </summary>
public interface IAddressLookup
{
    bool Supports(SegmentTarget target);

    Task<IReadOnlyList<ResolvedAddress>> LookupAsync(
        SegmentTarget target, CommunicationChannel channel, IReadOnlyCollection<AddressLookup> items, CancellationToken cancellationToken);
}
