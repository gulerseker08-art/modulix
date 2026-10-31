using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Records;

/// <summary>Dosya baytları olmadan imza kanıtı üst bilgisi.</summary>
internal sealed record ContractSignatureFileMeta(Guid Id, Guid SignatoryId, string FileName, string ContentType, int SizeBytes, string Sha256, DateTimeOffset CreatedAt);

// İmza okumaları (dilim 6). Çağıran sözleşmenin görünürlüğünü (izin ve kapsam) kayıt boru hattında doğrulamış olmalıdır.
internal sealed partial class ContractReader
{
    /// <summary>Sözleşmenin imza istekleri, yeniden eskiye.</summary>
    public async Task<List<ContractSignatureRequest>> SignatureRequestsAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractSignatureRequest>().AsNoTracking().Where(r => r.ContractId == contractId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToListAsync(cancellationToken);

    /// <summary>Sözleşmenin açık (bekleyen ya da kısmen imzalı) isteği; en fazla bir tane olabilir.</summary>
    public Task<ContractSignatureRequest?> OpenSignatureRequestAsync(Guid contractId, CancellationToken cancellationToken) =>
        data.Set<ContractSignatureRequest>().AsNoTracking()
            .Where(r => r.ContractId == contractId && (r.Status == Lifecycle.SignatureRequestStatus.Pending || r.Status == Lifecycle.SignatureRequestStatus.PartiallySigned))
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public Task<ContractSignatureRequest?> SignatureRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        data.Set<ContractSignatureRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    /// <summary>Sağlayıcı referansından istek (olay işleme). Tenant filtresi/RLS yalnızca kendi tenant'ın isteğini döndürür.</summary>
    public Task<ContractSignatureRequest?> SignatureRequestByReferenceAsync(string reference, CancellationToken cancellationToken) =>
        data.Set<ContractSignatureRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.ProviderReference == reference, cancellationToken);

    public async Task<List<ContractSignatory>> SignatoriesAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) =>
        await data.Set<ContractSignatory>().AsNoTracking().Where(s => requestIds.Contains(s.RequestId)).OrderBy(s => s.Position).ToListAsync(cancellationToken);

    public async Task<List<ContractSignatureEvent>> SignatureEventsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) =>
        await data.Set<ContractSignatureEvent>().AsNoTracking().Where(e => requestIds.Contains(e.RequestId)).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    public Task<bool> SignatureEventSeenAsync(Guid requestId, string providerEventId, CancellationToken cancellationToken) =>
        data.Set<ContractSignatureEvent>().AsNoTracking().AnyAsync(e => e.RequestId == requestId && e.ProviderEventId == providerEventId, cancellationToken);

    public async Task<List<ContractSignatureFileMeta>> SignatureFilesAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractSignatureFile>().AsNoTracking().Where(f => f.ContractId == contractId).OrderBy(f => f.CreatedAt)
            .Select(f => new ContractSignatureFileMeta(f.Id, f.SignatoryId, f.FileName, f.ContentType, f.SizeBytes, f.Sha256, f.CreatedAt)).ToListAsync(cancellationToken);

    public Task<ContractSignatureFile?> SignatureFileAsync(Guid contractId, Guid fileId, CancellationToken cancellationToken) =>
        data.Set<ContractSignatureFile>().AsNoTracking().SingleOrDefaultAsync(f => f.ContractId == contractId && f.Id == fileId, cancellationToken);

    /// <summary>Süresi dolmuş ama hâlâ açık görünen istekler (zamanlanmış iş / süre denetimi).</summary>
    public async Task<List<Guid>> OverdueSignatureContractsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await data.Set<ContractSignatureRequest>().AsNoTracking()
            .Where(r => (r.Status == Lifecycle.SignatureRequestStatus.Pending || r.Status == Lifecycle.SignatureRequestStatus.PartiallySigned) && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt).Select(r => r.ContractId).Distinct().Take(limit).ToListAsync(cancellationToken);
}
