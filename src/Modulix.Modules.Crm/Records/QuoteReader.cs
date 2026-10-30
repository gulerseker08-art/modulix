using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Records;

/// <summary>Belge üst bilgisi (PDF baytları olmadan; listelerde ve müşteri sayfasında bayt yüklenmez).</summary>
internal sealed record QuoteDocumentMeta(Guid Id, Guid QuoteId, int RevisionNo, string FileName, string ContentType, int SizeBytes, string Sha256, int TemplateVersion, string Snapshot, DateTimeOffset CreatedAt)
{
    public QuoteDocumentInfo ToInfo() => new(Id, RevisionNo, FileName, SizeBytes, Sha256, TemplateVersion, CreatedAt);
}

/// <summary>
/// Teklif tarihçesi, kalem, belge, paylaşım bağlantısı ve karar kayıtlarının salt okunur erişimi. Yazma yalnızca kayıt depolarındadır
/// (aynı işlemde). Çağıran, teklifin görünürlüğünü (izin ve veri kapsamı) kayıt boru hattında kendisi doğrulamış olmalıdır; müşteri
/// portalı ise belirteç sahipliğiyle erişir.
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

    /// <summary>Etkin onay kuralları (değerlendirme sunucu kararıdır; kural yönetimi izni gerekmez).</summary>
    public async Task<List<QuoteApprovalRuleInfo>> ActiveRulesAsync(CancellationToken cancellationToken) =>
        await data.Set<QuoteApprovalRule>().AsNoTracking().Where(r => r.IsActive)
            .Select(r => new QuoteApprovalRuleInfo(r.Id, r.Version, r.Name, r.Priority, r.Currency, r.MinDiscountPercent, r.MinGrossTotal, r.ApproverId))
            .ToListAsync(cancellationToken);

    public Task<Quote?> QuoteAsync(Guid quoteId, CancellationToken cancellationToken) =>
        data.Set<Quote>().AsNoTracking().SingleOrDefaultAsync(q => q.Id == quoteId, cancellationToken);

    /// <summary>Belge verisi için kalemler (sıralı). Maliyet alanı çağıran tarafından hiçbir zaman kullanılmaz.</summary>
    public async Task<List<QuoteLine>> LinesAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteLine>().AsNoTracking().Where(l => l.QuoteId == quoteId).OrderBy(l => l.Position).ThenBy(l => l.CreatedAt).ToListAsync(cancellationToken);

    public async Task<List<QuoteDocumentMeta>> DocumentsAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await Metas(data.Set<QuoteDocument>().AsNoTracking().Where(d => d.QuoteId == quoteId).OrderByDescending(d => d.CreatedAt)).ToListAsync(cancellationToken);

    public Task<QuoteDocumentMeta?> DocumentMetaAsync(Guid documentId, CancellationToken cancellationToken) =>
        Metas(data.Set<QuoteDocument>().AsNoTracking().Where(d => d.Id == documentId)).SingleOrDefaultAsync(cancellationToken);

    public Task<byte[]?> DocumentContentAsync(Guid documentId, CancellationToken cancellationToken) =>
        data.Set<QuoteDocument>().AsNoTracking().Where(d => d.Id == documentId).Select(d => (byte[]?)d.Content).SingleOrDefaultAsync(cancellationToken);

    public async Task<List<QuoteShareLink>> LinksAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteShareLink>().AsNoTracking().Where(l => l.QuoteId == quoteId).OrderByDescending(l => l.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Belirteç özetiyle bağlantıyı bulur (müşteri portalı). Özet tenant içinde benzersizdir.</summary>
    public Task<QuoteShareLink?> LinkByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        data.Set<QuoteShareLink>().AsNoTracking().SingleOrDefaultAsync(l => l.TokenHash == tokenHash, cancellationToken);

    public async Task<List<QuoteDecision>> DecisionsAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteDecision>().AsNoTracking().Where(d => d.QuoteId == quoteId).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);

    public Task<QuoteDecision?> LastDecisionAsync(Guid quoteId, CancellationToken cancellationToken) =>
        data.Set<QuoteDecision>().AsNoTracking().Where(d => d.QuoteId == quoteId).OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    private static IQueryable<QuoteDocumentMeta> Metas(IQueryable<QuoteDocument> source) =>
        source.Select(d => new QuoteDocumentMeta(d.Id, d.QuoteId, d.RevisionNo, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.TemplateVersion, d.Snapshot, d.CreatedAt));
}
