using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Modulix.Infrastructure.Email;

/// <summary>
/// SMTP ile gönderim (MailKit). Her e-posta için bağlanır; gönderim hacmi arttığında bağlantı havuzu eklenebilir.
/// Sunucu alıcıyı, göndereni veya içeriği 5xx koduyla reddederse hata kalıcıdır; diğer hatalar tekrar denenir.
/// </summary>
internal sealed class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("E-posta ayarları eksik: Email:FromAddress ve Email:Smtp:Host gerekli.");
        }

        using var message = BuildMessage(email, settings);
        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(settings.Smtp.TimeoutSeconds).TotalMilliseconds };

        try
        {
            await client.ConnectAsync(settings.Smtp.Host!, settings.Smtp.Port, ToSocketOptions(settings.Smtp.Security), cancellationToken);
            if (!string.IsNullOrEmpty(settings.Smtp.UserName))
            {
                await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
        }
        catch (SmtpCommandException exception) when ((int)exception.StatusCode >= 500
            && exception.ErrorCode is SmtpErrorCode.RecipientNotAccepted
                or SmtpErrorCode.SenderNotAccepted
                or SmtpErrorCode.MessageNotAccepted)
        {
            throw new PermanentEmailFailureException(
                $"SMTP sunucusu e-postayı reddetti ({(int)exception.StatusCode}): {exception.Message}", exception);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None);
            }
        }
    }

    internal static MimeMessage BuildMessage(OutgoingEmail email, EmailOptions settings)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress!));
        message.To.Add(new MailboxAddress(email.ToName ?? string.Empty, email.ToAddress));
        message.Subject = email.Subject;

        // Tekrar denemede aynı kimlik: alıcı tarafı yinelenen e-postayı tanıyabilir.
        message.MessageId = $"{email.Id:N}@modulix";

        message.Body = new BodyBuilder { HtmlBody = email.HtmlBody, TextBody = email.TextBody }.ToMessageBody();
        return message;
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}
