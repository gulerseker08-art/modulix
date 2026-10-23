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

        var db = provider.GetRequiredService<ModulixDbContext>();
        provider.GetRequiredService<IEmailQueue>().Enqueue(new EmailMessage(
            to,
            "Modülix deneme e-postası",
            "<p>Bu bir deneme e-postasıdır. Modülix e-posta ayarları çalışıyor.</p>")
        {
            TextBody = "Bu bir deneme e-postasıdır. Modülix e-posta ayarları çalışıyor.",
        });
        var queued = db.ChangeTracker.Entries<QueuedEmail>().Single().Entity;
        await db.SaveChangesAsync();

        await provider.GetRequiredService<EmailDispatcher>().DispatchPendingAsync(CancellationToken.None);

        var result = await db.EmailMessages.AsNoTracking().SingleAsync(e => e.Id == queued.Id);
        return result.Status == EmailStatus.Sent
            ? $"Gönderildi: {to} ({settings.Smtp.Host}:{settings.Smtp.Port})."
            : $"Gönderilemedi ({result.Status}): {result.LastError}";
    }
}
