using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Communication;

/// <summary>Tercih merkezindeki durum. Kararlıdır; sayfa buna göre çizer.</summary>
public static class PortalStates
{
    /// <summary>Pazarlama izni var; çıkış yapılabilir.</summary>
    public const string Subscribed = "subscribed";

    /// <summary>Reddedilmiş (çıkılmış ya da firma kaydı); gönderilmez.</summary>
    public const string Unsubscribed = "unsubscribed";

    /// <summary>Aktif engel var; gönderilmez ve bağlantıyla değiştirilemez.</summary>
    public const string Blocked = "blocked";

    /// <summary>İzin kaydı yok; gönderilmez.</summary>
    public const string NoConsent = "no-consent";
}

/// <param name="MaskedAddress">Adres maskelidir ("a***@example.com"): bağlantıyı bulan kişiye adresin tamamı gösterilmez.</param>
public sealed record PortalView(
    CommunicationChannel Channel, string MaskedAddress, string State, bool CanUnsubscribe, bool CanResubscribe, string? Message, DateTimeOffset ExpiresAt);

public enum PortalError
{
    None = 0,

    /// <summary>Belirteç yok, bu tenant'a ait değil ya da biçimsiz (kasıtlı olarak ayırt edilmez).</summary>
    Invalid = 1,

    /// <summary>Belirteç geçerli ama işlem kurallara aykırı (ör. firma kaydını kişi geri açamaz, süre dolmuş).</summary>
    Refused = 2,
}

public sealed record PortalResult(PortalError Error, PortalView? View = null, string? Message = null)
{
    public bool IsSuccess => Error == PortalError.None;
}

/// <summary>
/// Oturumsuz tercih merkezi (ADR-0025, dilim 6). Kimlik doğrulama bağlantı belirtecine sahip olmaktır; çağıran tenant bağlamını adresteki tenant'tan
/// kurar (belirteç başka tenant'a ait olsa da bulunamaz). Yalnızca belirtecin adresinin pazarlama tercihini değiştirir; tercih ve olay satırları ile
/// denetim kaydı yazılır (adres maskeli).
/// </summary>
public interface ICommunicationPortal
{
    Task<PortalResult> ViewAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Pazarlama iznini reddeder (kaynak "abonelikten çıkma"). Tekrarı güvenlidir (zaten reddedilmişse değişiklik ve olay yazılmaz). Süresi dolmuş
    /// bağlantıyla da yapılır: iletişimi azaltmak her zaman güvenlidir.
    /// </summary>
    Task<PortalResult> UnsubscribeAsync(string token, CancellationToken cancellationToken);

    /// <summary>Kişinin kendi çıkışını geri alması (kaynak "tercih merkezi"). Süresi dolmuş bağlantıyla, firma kaydıyla ve aktif engelle yapılamaz.</summary>
    Task<PortalResult> ResubscribeAsync(string token, CancellationToken cancellationToken);
}
