using Microsoft.EntityFrameworkCore;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Communication;

/// <summary>
/// Oturumsuz tercih merkezi (ADR-0025, dilim 6). Tenant bağlamını çağıran kurar (adresteki tenant); belirteç yalnızca bu tenant'ın satır güvenliği
/// altında aranır, bu yüzden başka tenant'ın belirteci "geçersiz" görünür. Değişiklik <see cref="ICommunicationAdministration"/> üzerinden yapılır
/// (tercih, olay ve denetim kaydı tek işlemde; adres maskeli).
/// </summary>
internal sealed class CommunicationPortal(ModulixDbContext db, ICommunicationAdministration administration, TimeProvider time) : ICommunicationPortal
{
    public async Task<PortalResult> ViewAsync(string token, CancellationToken cancellationToken) =>
        await FindAsync(token, cancellationToken) is { } found
            ? new PortalResult(PortalError.None, await BuildAsync(found, cancellationToken))
            : Invalid();

    public async Task<PortalResult> UnsubscribeAsync(string token, CancellationToken cancellationToken)
    {
        if (await FindAsync(token, cancellationToken) is not { } found)
        {
            return Invalid();
        }

        // Süresi dolmuş bağlantıyla da çıkılır: iletişimi azaltmak her zaman güvenlidir. Zaten reddedilmişse değişiklik ve olay yazılmaz.
        var current = await StatusAsync(found, cancellationToken);
        if (current != ConsentStatus.Denied)
        {
            try
            {
                await administration.RecordConsentAsync(
                    new ConsentChange(found.Channel, found.Address, CommunicationPurpose.Marketing, ConsentStatus.Denied, ConsentSources.Unsubscribe, null, "Abonelikten çıkma bağlantısı"),
                    cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return new PortalResult(PortalError.Refused, null, ex.Message);
            }
        }

        return new PortalResult(PortalError.None, await BuildAsync(found, cancellationToken));
    }

    public async Task<PortalResult> ResubscribeAsync(string token, CancellationToken cancellationToken)
    {
        if (await FindAsync(token, cancellationToken) is not { } found)
        {
            return Invalid();
        }

        if (found.IsExpiredAt(time.GetUtcNow()))
        {
            return new PortalResult(PortalError.Refused, null, "Bağlantının süresi dolmuş. Lütfen firmayla iletişime geçin.");
        }

        var (status, source) = await PreferenceAsync(found, cancellationToken);
        if (CommunicationRules.SelfResubscribeBlock(status, source, await IsSuppressedAsync(found, cancellationToken)) is { } block)
        {
            return new PortalResult(PortalError.Refused, null, block);
        }

        try
        {
            await administration.RecordConsentAsync(
                new ConsentChange(
                    found.Channel, found.Address, CommunicationPurpose.Marketing, ConsentStatus.Granted, ConsentSources.PreferenceCenter,
                    $"link:{found.Id:N}", "Kişi aboneliği tercih merkezinden yeniden açtı"),
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return new PortalResult(PortalError.Refused, null, ex.Message);
        }

        return new PortalResult(PortalError.None, await BuildAsync(found, cancellationToken));
    }

    private async Task<CommunicationLinkToken?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (!LinkTokens.LooksValid(token))
        {
            return null;
        }

        var hash = LinkTokens.Hash(token);
        return await db.CommunicationLinkTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    private async Task<PortalView> BuildAsync(CommunicationLinkToken token, CancellationToken cancellationToken)
    {
        var (status, source) = await PreferenceAsync(token, cancellationToken);
        var suppressed = await IsSuppressedAsync(token, cancellationToken);
        var masked = CommunicationAddress.Mask(token.Channel, token.Address);

        if (suppressed)
        {
            return new PortalView(token.Channel, masked, PortalStates.Blocked, false, false, "Bu adres için gönderim engellenmiş; size pazarlama iletisi gönderilmez.", token.ExpiresAt);
        }

        return status switch
        {
            ConsentStatus.Granted => new PortalView(token.Channel, masked, PortalStates.Subscribed, true, false, null, token.ExpiresAt),
            ConsentStatus.Denied => new PortalView(
                token.Channel, masked, PortalStates.Unsubscribed, false,
                !token.IsExpiredAt(time.GetUtcNow()) && CommunicationRules.SelfResubscribeBlock(status, source, false) is null,
                "Size pazarlama iletisi gönderilmez.", token.ExpiresAt),
            _ => new PortalView(token.Channel, masked, PortalStates.NoConsent, true, false, "Bu adres için pazarlama izni kaydı yok; size pazarlama iletisi gönderilmez.", token.ExpiresAt),
        };
    }

    private async Task<ConsentStatus> StatusAsync(CommunicationLinkToken token, CancellationToken cancellationToken) =>
        (await PreferenceAsync(token, cancellationToken)).Status;

    private async Task<(ConsentStatus Status, string Source)> PreferenceAsync(CommunicationLinkToken token, CancellationToken cancellationToken)
    {
        var row = await db.CommunicationPreferences.AsNoTracking()
            .Where(p => p.Channel == token.Channel && p.Address == token.Address && p.Purpose == CommunicationPurpose.Marketing)
            .Select(p => new { p.Status, p.Source })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? (ConsentStatus.Unknown, ConsentSources.System) : (row.Status, row.Source);
    }

    private async Task<bool> IsSuppressedAsync(CommunicationLinkToken token, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await db.CommunicationSuppressions.AsNoTracking().AnyAsync(
            s => s.Channel == token.Channel && s.Address == token.Address && s.LiftedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now),
            cancellationToken);
    }

    private static PortalResult Invalid() => new(PortalError.Invalid);
}
