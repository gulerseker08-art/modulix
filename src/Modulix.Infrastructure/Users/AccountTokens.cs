namespace Modulix.Infrastructure.Users;

/// <summary>
/// Davet ve şifre sıfırlama bağlantıları. Token'lar ASP.NET Identity'nin şifreli, süreli token'larıdır; veritabanında
/// saklanmaz. Token hesabın güvenlik damgasına bağlıdır: şifre belirlenince veya bağlantı yeniden gönderilince
/// damga değişir ve eski bağlantılar çalışmaz (tek kullanımlık).
/// </summary>
public static class AccountTokens
{
    /// <summary>Davet token sağlayıcısının adı (7 gün geçerli; API kaydeder).</summary>
    public const string InvitationProvider = "ModulixInvitation";

    public static readonly TimeSpan InvitationLifespan = TimeSpan.FromDays(7);

    public static readonly TimeSpan PasswordResetLifespan = TimeSpan.FromHours(24);

    /// <summary>Token bir üyeliğe bağlıdır: başka tenant'ın davetinde kullanılamaz.</summary>
    public static string InvitationPurpose(Guid membershipId) => $"invitation:{membershipId:N}";

    public static string InvitationLink(string publicUrl, Guid membershipId, string token) =>
        $"{Root(publicUrl)}/davet?uyelik={membershipId}&token={Uri.EscapeDataString(token)}";

    public static string PasswordResetLink(string publicUrl, Guid userId, string token) =>
        $"{Root(publicUrl)}/sifre-sifirla?kullanici={userId}&token={Uri.EscapeDataString(token)}";

    public static string EmailChangeLink(string publicUrl, Guid userId, string email, string token) =>
        $"{Root(publicUrl)}/eposta-onayla?kullanici={userId}&eposta={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";

    public static string SignInLink(string publicUrl) => $"{Root(publicUrl)}/giris";

    public static string AccountLink(string publicUrl) => $"{Root(publicUrl)}/hesabim";

    private static string Root(string publicUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicUrl);
        return publicUrl.TrimEnd('/');
    }
}
