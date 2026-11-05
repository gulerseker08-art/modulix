using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Automation;

/// <param name="Runs">Silinen çalışma sayısı.</param>
/// <param name="Steps">Silinen adım sayısı.</param>
/// <param name="Cursors">Silinen tarama imleci sayısı.</param>
/// <param name="More">Sınırlı tur sonunda silinecek daha çok satır kalmış olabilir (sonraki tur devam eder).</param>
public sealed record AutomationRetentionResult(int Runs, int Steps, int Cursors, bool More);

/// <summary>
/// Kural çalışması saklama temizliği (ADR-0027, Dilim 2b-2). Aktif tenant için, <b>bitmiş</b> çalışmaları ve adımlarını saklama süresinden sonra siler:
/// başarılı, iptal ve koşulu sağlanmayan çalışmalar <see cref="AutomationOptions.RunRetentionDays"/> gün, başarısız çalışmalar
/// <see cref="AutomationOptions.FailedRunRetentionDays"/> gün sonra. Günlük taramanın koşulu sağlanmayan kayıtlar için yazdığı
/// <see cref="AutomationRunStatus.NotMatched"/> satırları da bu yolla temizlenir. Bitmemiş (bekleyen, çalışan) çalışmaya dokunulmaz. Eski tarama imleçleri de
/// aynı süreyle silinir (dönemleri günlüktür, geçmiş günler bir daha taranmaz).
/// <para>
/// Silme sınırlı gruplarla yapılır (uzun kilit ve işlem günlüğü şişmesi olmaz); önce adımlar, sonra çalışmalar silinir. Tur sınırı dolarsa
/// <c>More</c> döner, sonraki tur devam eder. Tekrar çalışması güvenlidir: yarım kalan grup bir sonraki turda tamamlanır.
/// Tarihçe ekranı yalnızca saklanan çalışmaları gösterir; denetim kaydı (audit) bu temizlikten etkilenmez.
/// </para>
/// </summary>
public sealed class AutomationRetention(ModulixDbContext db, IOptions<AutomationOptions> options, TimeProvider time)
{
    private const int BatchSize = 500;
    private const int MaxBatches = 40;

    public async Task<AutomationRetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-Math.Max(1, settings.RunRetentionDays));
        var failedCutoff = now.AddDays(-Math.Max(1, settings.FailedRunRetentionDays));

        int runs = 0, steps = 0;
        var more = false;
        for (var i = 0; i < MaxBatches; i++)
        {
            var ids = await db.AutomationRuns
                .Where(r => r.CompletedAt != null
                    && ((r.Status == AutomationRunStatus.Failed && r.CompletedAt < failedCutoff)
                        || ((r.Status == AutomationRunStatus.Succeeded || r.Status == AutomationRunStatus.Cancelled || r.Status == AutomationRunStatus.NotMatched)
                            && r.CompletedAt < cutoff)))
                .OrderBy(r => r.CompletedAt)
                .Select(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            steps += await db.AutomationRunSteps.Where(s => ids.Contains(s.RunId)).ExecuteDeleteAsync(cancellationToken);
            runs += await db.AutomationRuns.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
            if (ids.Count < BatchSize)
            {
                break;
            }

            more = i == MaxBatches - 1;
        }

        var cursors = 0;
        for (var i = 0; i < MaxBatches; i++)
        {
            var deleted = await db.AutomationScanCursors
                .Where(c => c.UpdatedAt < cutoff)
                .OrderBy(c => c.UpdatedAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            cursors += deleted;
            if (deleted < BatchSize)
            {
                break;
            }

            more |= i == MaxBatches - 1;
        }

        return new AutomationRetentionResult(runs, steps, cursors, more);
    }
}
