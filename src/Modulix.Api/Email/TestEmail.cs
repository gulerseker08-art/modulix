using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Notifications;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Api.Email;

/// <summary>"test-email" komutu: deneme e-postasını kuyruğa yazar, hemen gönderir ve sonucu döner.</summary>
public static class TestEmail
{
    public static async Task<string> SendAsync(IServiceProvider services, string to)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var settings = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
        if (!settings.IsConfigured)
        {
            return "E-posta ayarları eksik: Email:FromAddress ve Email:Smtp:Host gerekli (README, E-posta bölümü).";
        }

        // E-posta kuyruğu platform veritabanındadır (ADR-0009) ve kuyruğa yazma hemen kalıcı kılınır.
        var db = provider.GetRequiredService<PlatformDbContext>();
        provider.GetRequiredService<IEmailQueue>().Enqueue(new EmailMessage(
            to,
            "Modülix deneme e-postası",
            "<p>Bu bir deneme e-postasıdır. Modülix e-posta ayarları çalışıyor.</p>")
        {
            TextBody = "Bu bir deneme e-postasıdır. Modülix e-posta ayarları çalışıyor.",
        });

        await provider.GetRequiredService<EmailDispatcher>().DispatchPendingAsync(CancellationToken.None);

        var result = await db.EmailMessages.AsNoTracking()
            .Where(e => e.ToAddress == to)
            .OrderByDescending(e => e.CreatedAt)
            .FirstAsync();
        return result.Status == EmailStatus.Sent
            ? $"Gönderildi: {to} ({settings.Smtp.Host}:{settings.Smtp.Port})."
            : $"Gönderilemedi ({result.Status}): {result.LastError}";
    }
}
