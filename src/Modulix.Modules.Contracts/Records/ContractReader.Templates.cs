using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Records;

/// <summary>Belge üst bilgisi (PDF baytları olmadan; listelerde bayt yüklenmez).</summary>
internal sealed record ContractDocumentMeta(
    Guid Id, Guid ContractId, int ContractVersionNo, bool IsFinal, Guid TemplateId, int TemplateVersionNo, string TemplateName, bool IsExample,
    string FileName, int SizeBytes, string Sha256, Guid? CreatedBy, DateTimeOffset CreatedAt);

// Şablon, şablon sürümü, belge ve ortak taraf okumaları. Çağıran sözleşmenin/şablonun görünürlüğünü (izin ve kapsam) kayıt boru hattında doğrulamış olmalıdır.
internal sealed partial class ContractReader
{
    public Task<ContractTemplate?> TemplateAsync(Guid templateId, CancellationToken cancellationToken) =>
        data.Set<ContractTemplate>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);

    public async Task<List<ContractTemplateVersion>> TemplateVersionsAsync(Guid templateId, CancellationToken cancellationToken) =>
        await data.Set<ContractTemplateVersion>().AsNoTracking().Where(v => v.TemplateId == templateId).OrderByDescending(v => v.VersionNo).ToListAsync(cancellationToken);

    public Task<ContractTemplateVersion?> TemplateVersionAsync(Guid templateId, int versionNo, CancellationToken cancellationToken) =>
        data.Set<ContractTemplateVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.TemplateId == templateId && v.VersionNo == versionNo, cancellationToken);

    public async Task<List<ContractDocumentMeta>> DocumentsAsync(Guid contractId, CancellationToken cancellationToken) =>
        await Metas(data.Set<ContractDocument>().AsNoTracking().Where(d => d.ContractId == contractId).OrderByDescending(d => d.CreatedAt)).ToListAsync(cancellationToken);

    public Task<ContractDocumentMeta?> DocumentMetaAsync(Guid contractId, Guid documentId, CancellationToken cancellationToken) =>
        Metas(data.Set<ContractDocument>().AsNoTracking().Where(d => d.ContractId == contractId && d.Id == documentId)).SingleOrDefaultAsync(cancellationToken);

    public Task<byte[]?> DocumentContentAsync(Guid contractId, Guid documentId, CancellationToken cancellationToken) =>
        data.Set<ContractDocument>().AsNoTracking().Where(d => d.ContractId == contractId && d.Id == documentId).Select(d => (byte[]?)d.Content).SingleOrDefaultAsync(cancellationToken);

    public Task<Party?> PartyAsync(Guid partyId, CancellationToken cancellationToken) =>
        data.Set<Party>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == partyId, cancellationToken);

    public Task<Person?> PersonAsync(Guid personId, CancellationToken cancellationToken) =>
        data.Set<Person>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == personId, cancellationToken);

    private static IQueryable<ContractDocumentMeta> Metas(IQueryable<ContractDocument> source) =>
        source.Select(d => new ContractDocumentMeta(
            d.Id, d.ContractId, d.ContractVersionNo, d.IsFinal, d.TemplateId, d.TemplateVersionNo, d.TemplateName, d.IsExample, d.FileName, d.SizeBytes, d.Sha256, d.CreatedBy, d.CreatedAt));
}
