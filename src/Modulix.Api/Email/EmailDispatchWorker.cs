using Microsoft.Extensions.Options;
using Modulix.Infrastructure.Email;

namespace Modulix.Api.Email;

/// <summary>
/// Kuyruktaki e-postaları düzenli aralıklarla gönderir (<see cref="EmailDispatcher"/>). Ayarlar eksikse
/// çalışmaz; e-postalar kuyrukta bekler ve ayarlar tamamlanıp uygulama yeniden başlatılınca gönderilir.
/// </summary>
public sealed class EmailDispatchWorker(
    IServiceScopeFactory scopes,
    IOptions<EmailOptions> options,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        if (!settings.IsConfigured)
        {
            logger.LogWarning("E-posta ayarları eksik (Email:FromAddress, Email:Smtp:Host); e-postalar gönderilmeden kuyrukta bekleyecek.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempted = 0;
            try
            {
                using var scope = scopes.CreateScope();
                attempted = await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Ör. veritabanına ulaşılamadı; bir sonraki turda tekrar denenir.
                logger.LogError(exception, "E-posta kuyruğu işlenemedi.");
            }

            // Tam dolu bir tur daha fazla e-posta olduğunu gösterir; beklemeden devam edilir.
            if (attempted < settings.BatchSize)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
