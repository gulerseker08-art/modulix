namespace Modulix.Core.Contracts.Communication;

/// <summary>
/// Bir pazarlama iletisine eklenen abonelikten çıkma bağlantıları (ADR-0025, dilim 6). <see cref="PageUrl"/> kişiye gösterilen tercih sayfasıdır
/// (belirteç adresin # parçasındadır; sayfa yalnızca düğmeyle çıkış yapar, açılışta yapmaz). <see cref="OneClickUrl"/> posta istemcisinin
/// tek tıkla çıkış (RFC 8058: <c>List-Unsubscribe</c> + <c>List-Unsubscribe-Post</c>) isteği içindir.
/// </summary>
public sealed record UnsubscribeLink(string PageUrl, string OneClickUrl);

/// <summary>
/// Aktif tenant için, bir iletişim noktasına özel abonelikten çıkma bağlantısı üretir. Belirteç rastgele ve tahmin edilemezdir; yalnızca karması
/// saklanır. Bağlantı, uygulamanın genel adresi (<c>App:PublicUrl</c>) ayarlı değilse üretilemez: bu durumda pazarlama gönderimi başlatılmaz.
/// </summary>
public interface IUnsubscribeLinks
{
    /// <summary>Genel adres ayarlıysa true. Gönderim başlamadan önce denetlenir.</summary>
    bool IsAvailable { get; }

    /// <param name="reference">Bağlantının hangi gönderim için üretildiği (ör. teslimat anahtarı); yalnızca izleme içindir.</param>
    /// <returns>Üretilen bağlantılar; genel adres ayarlı değilse null.</returns>
    Task<UnsubscribeLink?> IssueAsync(CommunicationChannel channel, string address, string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Bir grup için bağlantı üretir (dilim 10: toplu gönderim tek kayıt turuyla çalışır). Sonuç girdiyle aynı sırada ve aynı uzunluktadır; genel adres ayarlı
    /// değilse her öğe null olur. Varsayılan uygulama tek tek üretir.
    /// </summary>
    async Task<IReadOnlyList<UnsubscribeLink?>> IssueManyAsync(
        CommunicationChannel channel, IReadOnlyList<(string Address, string Reference)> items, CancellationToken cancellationToken)
    {
        var links = new List<UnsubscribeLink?>(items.Count);
        foreach (var (address, reference) in items)
        {
            links.Add(await IssueAsync(channel, address, reference, cancellationToken));
        }

        return links;
    }
}
