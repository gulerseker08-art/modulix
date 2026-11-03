using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Communication;

/// <summary>
/// Abonelikten çıkma bağlantısı üretici (ADR-0025, dilim 6). Belirteç iletiye girmeden önce kaydedilir: gönderim sonrası bir çökme, iletideki
/// bağlantıyı geçersiz bırakmaz. Her üretim yeni bir belirteç açar (yeniden denenen gönderimde kullanılmamış belirteç zararsızdır).
/// </summary>
internal sealed class UnsubscribeLinks(ModulixDbContext db, ITenantContextAccessor tenantAccessor, INotificationRecipients links, TimeProvider time) : IUnsubscribeLinks
{
    public bool IsAvailable => links.Link("abonelik") is not null;

    public async Task<UnsubscribeLink?> IssueAsync(CommunicationChannel channel, string address, string reference, CancellationToken cancellationToken) =>
        (await IssueManyAsync(channel, [(address, reference)], cancellationToken))[0];

    public async Task<IReadOnlyList<UnsubscribeLink?>> IssueManyAsync(
        CommunicationChannel channel, IReadOnlyList<(string Address, string Reference)> items, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan bağlantı üretilemez.");
        var result = new List<UnsubscribeLink?>(items.Count);
        var rows = new List<CommunicationLinkToken>(items.Count);
        var now = time.GetUtcNow();
        foreach (var (address, reference) in items)
        {
            var normalized = CommunicationAddress.Normalize(channel, address) ?? throw new ArgumentException("Adres geçersiz.", nameof(items));
            var token = LinkTokens.New();

            // Sayfa bağlantısında belirteç # parçasındadır (sunucuya, günlüklere ve Referer başlığına gitmez); tek tıkla çıkış adresinde yol parçasıdır, çünkü
            // posta istemcisi bu adrese gövde ile POST eder. Belirtecin yetkisi yalnızca o adresin pazarlama tercihidir.
            var page = links.Link($"abonelik/{tenant.TenantId:N}#t={token}");
            var oneClick = links.Link($"api/public/communication/{tenant.TenantId:N}/one-click/{token}");
            if (page is null || oneClick is null)
            {
                result.Add(null);
                continue;
            }

            rows.Add(new CommunicationLinkToken(LinkTokens.Hash(token), channel, normalized, reference, now));
            result.Add(new UnsubscribeLink(page, oneClick));
        }

        if (rows.Count > 0)
        {
            db.CommunicationLinkTokens.AddRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
