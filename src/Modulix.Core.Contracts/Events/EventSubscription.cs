namespace Modulix.Core.Contracts.Events;

/// <summary>
/// Bir modülün olay aboneliği (ADR-0007); <see cref="Modules.IModule.EventSubscriptions"/> ile bildirilir.
/// <list type="bullet">
///   <item><see cref="Key"/>: modül anahtarıyla başlar ve değişmez; teslim satırları buna bağlıdır.</item>
///   <item><see cref="EntityKeys"/>: boşsa bütün kayıt türleri. Modül başka modülün kayıt türüne abone olabilir; tepkisini
///   yalnızca kendi kayıtlarında veya ortak kayıtta gösterir.</item>
///   <item><see cref="Permissions"/>: abonenin çalışırken sahip olduğu izinler (yalnızca kendi modülünün). Abone olayı
///   tetikleyen kişi adına değil "sistem" adına, tenant geneli kapsamla çalışır; hassas alanı görmek için o alanın iznini
///   bildirmelidir.</item>
///   <item><see cref="Subscriber"/>: <see cref="IEventSubscriber"/> uygulayan sınıf; modül <c>ConfigureServices</c>'te
///   scoped kaydeder.</item>
///   <item><see cref="Ordered"/>: aynı kaydın olayları bu aboneye sıra numarasıyla teslim edilir; önceki teslim bitmeden
///   (başarılı veya atlanmış) sonraki alınmaz. Sırası önemli olmayan abone (ör. bildirim) <c>false</c> der.</item>
///   <item><see cref="MaxSchemaVersion"/>: abonenin okuyabildiği en yüksek olay sürümü; daha yenisi teslim edilmez.</item>
/// </list>
/// </summary>
public sealed record EventSubscription(
    string Key,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> EntityKeys,
    IReadOnlyList<string> Permissions,
    Type Subscriber)
{
    public bool Ordered { get; init; } = true;

    public int MaxSchemaVersion { get; init; } = 1;

    public static EventSubscription For<TSubscriber>(
        string key, IReadOnlyList<string> eventTypes, IReadOnlyList<string> entityKeys, IReadOnlyList<string> permissions)
        where TSubscriber : class, IEventSubscriber =>
        new(key, eventTypes, entityKeys, permissions, typeof(TSubscriber));

    /// <summary>Olay bu aboneliğe gider mi (tür ve kayıt türü)?</summary>
    public bool Matches(string eventType, string entityKey) =>
        EventTypes.Contains(eventType) && (EntityKeys.Count == 0 || EntityKeys.Contains(entityKey));
}

/// <summary>
/// Abone. Olayın tenant'ının bağlamında, açık bir veritabanı işlemi içinde çağrılır: kayıt boru hattından yaptığı
/// değişiklik, kuyruğa yazdığı e-posta ve teslimin "başarılı" işareti birlikte yazılır ya da hiçbiri.
/// <list type="bullet">
///   <item>Teslim en az bir kezdir: abone birden fazla kez çalışabilir, idempotent yazılır (olay kimliği).</item>
///   <item>İstisna fırlatırsa teslim artan aralıklarla yeniden denenir; kalıcı hatayı
///   <see cref="PermanentEventFailureException"/> ile bildirir. İstisna iletisine kişisel veri konmaz.</item>
///   <item>Yalnızca Contracts'taki servisleri kullanır; <c>DbContext</c>'e doğrudan yazmaz. 5 dakikadan uzun iş yapmaz.</item>
///   <item>Tepki vermemeyi seçmek (ör. kural gereği) hata değildir; abone sessizce döner.</item>
/// </list>
/// </summary>
public interface IEventSubscriber
{
    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Tekrar denemek sonucu değiştirmez (ör. olay sürümü desteklenmiyor); teslim doğrudan başarısız olur.</summary>
public sealed class PermanentEventFailureException(string message, Exception? innerException = null) : Exception(message, innerException);
