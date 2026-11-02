namespace Modulix.Core.Contracts.Communication;

/// <summary>
/// İletişim sıklığı sınırı (ADR-0025): bir adrese bir günde gidebilecek pazarlama iletisi sayısı kampanyalar arasında ortak sayılır. Rezervasyon
/// atomiktir: iki kampanya aynı anda aynı adrese gönderirken toplam sınır aşılmaz. Gün, çağıranın verdiği (tenant saat dilimindeki) gündür.
/// </summary>
public interface IFrequencyLimiter
{
    /// <summary>Sınır dolmadıysa bir hak ayırır (true); doluysa hiçbir şey yazmaz (false). <paramref name="limit"/> 1 ve üstü olmalıdır.</summary>
    Task<bool> TryReserveAsync(CommunicationChannel channel, string address, DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Ayrılan hakkı geri verir (ileti sağlayıcıya hiç kabul ettirilemedi). Hak yoksa bir şey yapmaz.</summary>
    Task ReleaseAsync(CommunicationChannel channel, string address, DateOnly day, CancellationToken cancellationToken);
}
