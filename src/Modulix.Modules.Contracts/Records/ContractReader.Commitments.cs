using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Records;

internal sealed record ContractCommitmentFileMeta(Guid Id, Guid OccurrenceId, string FileName, string ContentType, int SizeBytes, string Sha256);

// Taahhüt okumaları (dilim 7). Çağıran taahhüdün/sözleşmenin görünürlüğünü (izin ve kapsam) kayıt boru hattında doğrulamış olmalıdır.
internal sealed partial class ContractReader
{
    public Task<ContractCommitment?> CommitmentAsync(Guid commitmentId, CancellationToken cancellationToken) =>
        data.Set<ContractCommitment>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == commitmentId, cancellationToken);

    public async Task<List<ContractCommitment>> CommitmentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await data.Set<ContractCommitment>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);

    public async Task<List<ContractCommitmentOccurrence>> OccurrencesOfAsync(IReadOnlyCollection<Guid> commitmentIds, CancellationToken cancellationToken) =>
        await data.Set<ContractCommitmentOccurrence>().AsNoTracking().Where(o => commitmentIds.Contains(o.CommitmentId)).OrderBy(o => o.DueDate).ThenBy(o => o.Sequence).ToListAsync(cancellationToken);

    public async Task<List<ContractCommitmentFileMeta>> CommitmentFilesAsync(IReadOnlyCollection<Guid> occurrenceIds, CancellationToken cancellationToken) =>
        await data.Set<ContractCommitmentFile>().AsNoTracking().Where(f => occurrenceIds.Contains(f.OccurrenceId))
            .Select(f => new ContractCommitmentFileMeta(f.Id, f.OccurrenceId, f.FileName, f.ContentType, f.SizeBytes, f.Sha256)).ToListAsync(cancellationToken);

    public Task<ContractCommitmentFile?> CommitmentFileAsync(Guid occurrenceId, Guid fileId, CancellationToken cancellationToken) =>
        data.Set<ContractCommitmentFile>().AsNoTracking().SingleOrDefaultAsync(f => f.OccurrenceId == occurrenceId && f.Id == fileId, cancellationToken);

    /// <summary>Sözleşme numarası ve başlığı (iş listesi gösterimi).</summary>
    public async Task<Dictionary<Guid, (string Number, string Title)>> ContractNamesAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken) =>
        await data.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).Select(c => new { c.Id, c.Number, c.Title })
            .ToDictionaryAsync(c => c.Id, c => (c.Number, c.Title), cancellationToken);
}
