using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Teklif tarihçesi ve kalem varlığının salt okunur erişimi. Yazma yalnızca kayıt depolarındadır (aynı işlemde). Çağıran, teklifin
/// görünürlüğünü (izin ve veri kapsamı) kayıt boru hattında kendisi doğrulamış olmalıdır.
/// </summary>
internal sealed class QuoteReader(IModuleData data)
{
    public async Task<List<QuoteEvent>> HistoryAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quoteId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Ana toplama giren en az bir kalem var mı?</summary>
    public Task<bool> HasIncludedLinesAsync(Guid quoteId, CancellationToken cancellationToken) =>
        data.Set<QuoteLine>().AsNoTracking().AnyAsync(l => l.QuoteId == quoteId && l.Included, cancellationToken);

    /// <summary>Teklifi en son onaya gönderen kullanıcı (kendi teklifini onaylama denetimi için).</summary>
    public async Task<Guid?> LastSubmitterAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quoteId && e.Kind == "SubmittedForApproval")
            .OrderByDescending(e => e.CreatedAt).Select(e => e.ActorId).FirstOrDefaultAsync(cancellationToken);
}
