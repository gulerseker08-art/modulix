using System.Net;
using Modulix.Core.Contracts.Notifications;

namespace Modulix.Infrastructure.Users;

/// <summary>Kullanıcı yönetimi e-postaları. Kullanıcı girdisi (ad, tenant adı) HTML'e kodlanarak eklenir.</summary>
internal static class UserEmails
{
    public static EmailMessage Invitation(string to, string displayName, string tenantName, string invitedBy, string link) =>
        Build(
            to,
            displayName,
            $"{tenantName} sizi Modülix'e davet etti",
            [
                $"{invitedBy}, sizi Modülix'te {tenantName} kuruluşuna davet etti.",
                "Daveti kabul etmek için şifrenizi belirleyin. Bağlantı 7 gün geçerlidir ve bir kez kullanılabilir.",
            ],
            ("Şifremi belirle", link),
            "Bu daveti beklemiyorsanız e-postayı yok sayabilirsiniz.");

    public static EmailMessage AddedToTenant(string to, string displayName, string tenantName, string invitedBy, string link) =>
        Build(
            to,
            displayName,
            $"{tenantName} kuruluşuna eklendiniz",
            [
                $"{invitedBy}, sizi Modülix'te {tenantName} kuruluşuna ekledi.",
                "Mevcut e-posta adresiniz ve şifrenizle giriş yapıp kuruluşu seçebilirsiniz. Şifreniz değişmedi.",
            ],
            ("Giriş yap", link),
            "Bu kuruluşu tanımıyorsanız kuruluşun yöneticisine bildirin.");

    public static EmailMessage PasswordReset(string to, string displayName, string tenantName, string requestedBy, string link) =>
        Build(
            to,
            displayName,
            "Modülix şifre sıfırlama bağlantınız",
            [
                $"{tenantName} kuruluşunun yöneticisi {requestedBy}, Modülix hesabınız için şifre sıfırlama bağlantısı gönderdi.",
                "Yeni şifrenizi belirlemek için bağlantıyı açın. Bağlantı 24 saat geçerlidir ve bir kez kullanılabilir. "
                + "Hesabınızı birden fazla kuruluşta kullanıyorsanız yeni şifre hepsinde geçerli olur.",
            ],
            ("Yeni şifre belirle", link),
            "Bu isteği beklemiyorsanız şifreniz değişmez; e-postayı yok sayabilirsiniz.");

    /// <summary>Kişinin kendi istediği şifre sıfırlama bağlantısı (Y7a, "Şifremi unuttum").</summary>
    public static EmailMessage ForgotPassword(string to, string displayName, string link) =>
        Build(
            to,
            displayName,
            "Modülix şifre sıfırlama bağlantınız",
            [
                "Modülix hesabınız için şifre sıfırlama bağlantısı istendi.",
                "Yeni şifrenizi belirlemek için bağlantıyı açın. Bağlantı 24 saat geçerlidir ve bir kez kullanılabilir. "
                + "Hesabınızı birden fazla kuruluşta kullanıyorsanız yeni şifre hepsinde geçerli olur.",
            ],
            ("Yeni şifre belirle", link),
            "Bu isteği siz yapmadıysanız şifreniz değişmez; e-postayı yok sayabilirsiniz.");

    public static EmailMessage ConfirmEmailChange(string to, string displayName, string link) =>
        Build(
            to,
            displayName,
            "Yeni e-posta adresinizi onaylayın",
            [
                "Modülix hesabınızın e-posta adresini bu adresle değiştirmek istediniz.",
                "Onaylamak için bağlantıyı açın. Bağlantı 24 saat geçerlidir. Onayladıktan sonra bu adresle giriş yaparsınız.",
            ],
            ("E-postamı onayla", link),
            "Bu isteği siz yapmadıysanız e-postayı yok sayabilirsiniz; hesabınız değişmez.");

    public static EmailMessage EmailChanged(string to, string displayName, string newEmail, string link) =>
        Build(
            to,
            displayName,
            "Modülix e-posta adresiniz değişti",
            [
                $"Modülix hesabınızın e-posta adresi {newEmail} olarak değiştirildi. Artık bu adrese e-posta gelmeyecek.",
                "Bu değişikliği siz yapmadıysanız hemen kuruluşunuzun yöneticisine bildirin.",
            ],
            ("Giriş sayfası", link),
            "Bu e-posta bilgi amaçlıdır.");

    private static EmailMessage Build(
        string to, string displayName, string subject, string[] paragraphs, (string Text, string Url) action, string footer)
    {
        var html = string.Concat(
            $"<p>Merhaba {Encode(displayName)},</p>",
            string.Concat(paragraphs.Select(p => $"<p>{Encode(p)}</p>")),
            $"<p><a href=\"{Encode(action.Url)}\">{Encode(action.Text)}</a></p>",
            $"<p style=\"color:#666\">{Encode(footer)}</p>");

        var text = string.Join(
            "\n\n",
            [$"Merhaba {displayName},", .. paragraphs, $"{action.Text}: {action.Url}", footer]);

        return new EmailMessage(to, subject, html) { ToName = displayName, TextBody = text };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
