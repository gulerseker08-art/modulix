namespace Modulix.Core.Contracts.Records;

/// <summary>
/// Aktivitenin bağlanabildiği bir iş kaydı türü (ADR-0015). Her modül, aktivite ilişkisine açmak istediği kayıt türü için bir
/// uygulama kaydeder (<c>services.AddScoped&lt;IActivityLinkTarget, ...&gt;</c>); aktivite çekirdeği bu listeyi dolaşır ve
/// değişmez. Hedef modül kendi tablosunu bilir; aktivite tarafında hedefe veritabanı bağı yoktur.
/// <para>
/// Uygulamalar çağıranın erişimine göre karar verir: <see cref="IsVisibleAsync"/> yalnızca kayıt aktif tenant'ta VARSA, modül
/// tenant için açıksa ve kullanıcı kaydı okuyabiliyorsa (izin + veri kapsamı) true döner. Erişilemeyen kayıt, olmayan kayıtla
/// aynı yanıtı alır (varlığı belli edilmez). <see cref="GetTitleAsync"/> görünür kaydın gösterim adını verir; görünmeyen kayıt
/// için null döner, böylece aktivite üzerinden fırsat adı, tutarı ya da açıklaması sızmaz.
/// </para>
/// </summary>
public interface IActivityLinkTarget
{
    /// <summary>Kayıt türü anahtarı (ör. <c>crm.company</c>); modül anahtarıyla başlar.</summary>
    string EntityKey { get; }

    Task<bool> IsVisibleAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>Çağıranın görebildiği kaydın kısa gösterim adı; görünmüyorsa null.</summary>
    Task<string?> GetTitleAsync(Guid recordId, CancellationToken cancellationToken);
}
