using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Activities;
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
            .Select(r => new QuoteApprovalRuleInfo(r.Id, r.Version, r.Name, r.Priority, r.Currency, r.MinDiscountPercent, r.MinGrossTotal, r.ApproverId, r.Step))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Fırsatın hizmet kapsamı kodları (virgülle saklanan hâli; sunum anı kapsam özeti için). Çağıran teklifi sunma yetkisini kendisi
    /// doğrulamıştır; fırsat okuma izni gerekmez, yalnızca teklifin bağlı olduğu fırsatın kapsamı alınır. Fırsat yoksa null.
    /// </summary>
    public async Task<string?> OpportunityServiceScopeAsync(Guid opportunityId, CancellationToken cancellationToken) =>
        await data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == opportunityId).Select(o => o.ServiceScope).SingleOrDefaultAsync(cancellationToken);

    /// <summary>Firmanın ortak taraf kaydı (devir sözleşmesi için); firma yoksa null.</summary>
    public async Task<Guid?> PartyOfCompanyAsync(Guid companyId, CancellationToken cancellationToken) =>
        await data.Set<Company>().AsNoTracking().Where(c => c.Id == companyId).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>Kişinin ortak kişi kaydı (devir sözleşmesi için); kişi yoksa null.</summary>
    public async Task<Guid?> PersonOfContactAsync(Guid contactId, CancellationToken cancellationToken) =>
        await data.Set<Contact>().AsNoTracking().Where(c => c.Id == contactId).Select(c => (Guid?)c.PersonId).SingleOrDefaultAsync(cancellationToken);

    public Task<Quote?> QuoteAsync(Guid quoteId, CancellationToken cancellationToken) =>
        data.Set<Quote>().AsNoTracking().SingleOrDefaultAsync(q => q.Id == quoteId, cancellationToken);

    /// <summary>Belge verisi için kalemler (sıralı). Maliyet alanı çağıran tarafından hiçbir zaman kullanılmaz.</summary>
    public async Task<List<QuoteLine>> LinesAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteLine>().AsNoTracking().Where(l => l.QuoteId == quoteId).OrderBy(l => l.Position).ThenBy(l => l.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>Birden çok teklifin kalemleri (marj raporu). Çağıran maliyet iznini kendisi denetlemiş olmalıdır.</summary>
    public async Task<List<QuoteLine>> LinesForQuotesAsync(IReadOnlyCollection<Guid> quoteIds, CancellationToken cancellationToken) =>
        quoteIds.Count == 0 ? [] : await data.Set<QuoteLine>().AsNoTracking().Where(l => quoteIds.Contains(l.QuoteId)).ToListAsync(cancellationToken);

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

    /// <summary>Teklife bağlı (ActivityLink) ve hâlâ açık aktiviteler; otomatik kapatma için. Yalnızca görev ailesindeki açık durumlar.</summary>
    public async Task<List<Activity>> LinkedOpenActivitiesAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var ids = data.Set<ActivityLink>().Where(l => l.EntityKey == CrmModule.Entities.Quote && l.RecordId == quoteId).Select(l => l.ActivityId);
        return await data.Set<Activity>().AsNoTracking()
            .Where(a => ids.Contains(a.Id) && (a.Status == ActivityStatus.Open || a.Status == ActivityStatus.InProgress || a.Status == ActivityStatus.Blocked))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Teklifi kabul eden son olay (manuel kabul ya da müşteri kabulü); yoksa null.</summary>
    public Task<QuoteEvent?> AcceptEventAsync(Guid quoteId, CancellationToken cancellationToken) =>
        data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quoteId && (e.Kind == "Accepted" || e.Kind == "CustomerAccepted"))
            .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Firmanın etkin adresinin tek satır metin kopyası. Verilen türler sırayla denenir; ilk türde birincil adres önceliklidir. Adres yoksa null.
    /// Sipariş bu metni kopya olarak saklar; firma kartı sonradan değişse de sipariş değişmez.
    /// </summary>
    public async Task<string?> AddressTextAsync(Guid companyId, IReadOnlyList<string> types, CancellationToken cancellationToken)
    {
        var addresses = await data.Set<Address>().AsNoTracking().Where(a => a.CompanyId == companyId && a.IsActive).ToListAsync(cancellationToken);
        foreach (var type in types)
        {
            var found = addresses.Where(a => a.Type == type).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.CreatedAt).FirstOrDefault();
            if (found is not null)
            {
                return string.Join(", ", new[] { found.Line1, found.Line2, found.District, found.City, found.PostalCode, found.Country }
                    .Where(p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        return null;
    }

    public Task<bool> HasEventAsync(Guid quoteId, string kind, CancellationToken cancellationToken) =>
        data.Set<QuoteEvent>().AsNoTracking().AnyAsync(e => e.QuoteId == quoteId && e.Kind == kind, cancellationToken);

    /// <summary>Geçerliliği bitmiş ama hâlâ sunulmuş teklifler (en eski önce).</summary>
    public async Task<List<Quote>> DueForExpiryAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await data.Set<Quote>().AsNoTracking().Where(q => q.Status == QuoteStatus.Presented && q.ValidUntil != null && q.ValidUntil <= now)
            .OrderBy(q => q.ValidUntil).Take(take).ToListAsync(cancellationToken);

    /// <summary>Geçerliliği <paramref name="until"/> tarihine kadar bitecek, hatırlatması açılmamış sunulmuş teklifler.</summary>
    public async Task<List<Quote>> DueForReminderAsync(DateTimeOffset now, DateTimeOffset until, int take, CancellationToken cancellationToken) =>
        await data.Set<Quote>().AsNoTracking()
            .Where(q => q.Status == QuoteStatus.Presented && q.ValidUntil != null && q.ValidUntil > now && q.ValidUntil <= until
                && !data.Set<QuoteEvent>().Any(e => e.QuoteId == q.Id && e.Kind == "ExpiryReminderCreated"))
            .OrderBy(q => q.ValidUntil).Take(take).ToListAsync(cancellationToken);

    /// <summary>Son onaya gönderimden bu yana verilen adım onayları (zaman sırasıyla). Gönderim yoksa boş.</summary>
    public async Task<List<QuoteEvent>> StepApprovalsSinceSubmissionAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var submitted = await data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quoteId && e.Kind == "SubmittedForApproval")
            .OrderByDescending(e => e.CreatedAt).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (submitted is null)
        {
            return [];
        }

        return await data.Set<QuoteEvent>().AsNoTracking()
            .Where(e => e.QuoteId == quoteId && e.Kind == "StepApproved" && e.CreatedAt >= submitted)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Son gönderimden sonra verilen tarihçe işareti var mı (ör. onay hatırlatması)?</summary>
    public async Task<bool> HasEventSinceSubmissionAsync(Guid quoteId, string kind, CancellationToken cancellationToken)
    {
        var submitted = await data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quoteId && e.Kind == "SubmittedForApproval")
            .OrderByDescending(e => e.CreatedAt).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return submitted is not null && await data.Set<QuoteEvent>().AsNoTracking().AnyAsync(e => e.QuoteId == quoteId && e.Kind == kind && e.CreatedAt >= submitted, cancellationToken);
    }

    /// <summary>Şu an geçerli vekâletler (veren → vekil).</summary>
    public async Task<List<QuoteApprovalDelegation>> ActiveDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        (await data.Set<QuoteApprovalDelegation>().AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken)).Where(d => d.CoversAt(now)).ToList();

    /// <summary>Onay bekleyen taslaklar (son gönderimi <paramref name="before"/> öncesinde olanlar; hatırlatma için).</summary>
    public async Task<List<Quote>> PendingApprovalsAsync(DateTimeOffset before, int take, CancellationToken cancellationToken)
    {
        var pending = await data.Set<Quote>().AsNoTracking()
            .Where(q => q.Status == QuoteStatus.Draft && q.ApprovalStatus == QuoteApprovalStatus.Pending).OrderBy(q => q.UpdatedAt).Take(take).ToListAsync(cancellationToken);
        var result = new List<Quote>();
        foreach (var quote in pending)
        {
            var submitted = await data.Set<QuoteEvent>().AsNoTracking().Where(e => e.QuoteId == quote.Id && e.Kind == "SubmittedForApproval")
                .OrderByDescending(e => e.CreatedAt).Select(e => (DateTimeOffset?)e.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (submitted is { } at && at <= before)
            {
                result.Add(quote);
            }
        }

        return result;
    }

    private static IQueryable<QuoteDocumentMeta> Metas(IQueryable<QuoteDocument> source) =>
        source.Select(d => new QuoteDocumentMeta(d.Id, d.QuoteId, d.RevisionNo, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.TemplateVersion, d.Snapshot, d.CreatedAt));
}
