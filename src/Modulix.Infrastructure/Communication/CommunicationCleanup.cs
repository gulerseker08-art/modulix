using Microsoft.EntityFrameworkCore;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Communication;

/// <param name="Tokens">Silinen bağlantı belirteci sayısı.</param>
/// <param name="FrequencyWindows">Silinen eski sıklık penceresi sayısı.</param>
/// <param name="More">Sınırlı tur sonunda silinecek daha çok satır kalmış olabilir (sonraki tur devam eder).</param>
public sealed record CommunicationCleanupResult(int Tokens, int FrequencyWindows, bool More);

/// <summary>
/// Gönderim günlüğü temizliği (ADR-0025, dilim 12). Aktif tenant için iki şeyi siler: (1) <b>süresi dolmasının üstünden <see cref="TokenGraceDays"/> gün geçmiş</b> bağlantı
/// belirteçleri, (2) <see cref="FrequencyWindowDays"/> günden eski sıklık pencereleri. Belirteçler bilerek geç silinir: süresi dolan bağlantıyla çıkış hâlâ kabul edilir
/// (tasarım gereği iletişimi azaltmak her zaman güvenlidir), bu yüzden eski bir iletideki bağlantı ömrü (400 gün) + bu süre boyunca çalışır; sonrasında "geçersiz bağlantı"
/// der. Silme sınırlı gruplarla yapılır (uzun kilit ve işlem günlüğü şişmesi olmaz); tur sınırı dolarsa <c>More</c> döner ve sonraki tur devam eder. Tekrar çalışması güvenlidir.
/// </summary>
public sealed class CommunicationCleanup(ModulixDbContext db, ITenantClock clock, TimeProvider time)
{
    /// <summary>Süresi dolmuş belirtecin ne kadar daha saklanacağı (gün).</summary>
    public const int TokenGraceDays = 365;

    /// <summary>Sıklık penceresinin saklanacağı gün sayısı (yalnızca bugün sınırı etkiler; geri kalanı tanı içindir).</summary>
    public const int FrequencyWindowDays = 7;

    private const int BatchSize = 1000;
    private const int MaxBatchesPerTable = 50;

    public async Task<CommunicationCleanupResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var tokenCutoff = now.AddDays(-TokenGraceDays);
        var today = (await clock.GetAsync(cancellationToken)).Today;
        var windowCutoff = today.AddDays(-FrequencyWindowDays);

        var tokens = 0;
        var more = false;
        for (var i = 0; i < MaxBatchesPerTable; i++)
        {
            var deleted = await db.CommunicationLinkTokens
                .Where(t => t.ExpiresAt < tokenCutoff)
                .OrderBy(t => t.ExpiresAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            tokens += deleted;
            if (deleted < BatchSize)
            {
                break;
            }

            more = i == MaxBatchesPerTable - 1;
        }

        var windows = 0;
        for (var i = 0; i < MaxBatchesPerTable; i++)
        {
            var deleted = await db.FrequencyWindows
                .Where(w => w.Day < windowCutoff)
                .OrderBy(w => w.Day)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            windows += deleted;
            if (deleted < BatchSize)
            {
                break;
            }

            more |= i == MaxBatchesPerTable - 1;
        }

        return new CommunicationCleanupResult(tokens, windows, more);
    }
}
