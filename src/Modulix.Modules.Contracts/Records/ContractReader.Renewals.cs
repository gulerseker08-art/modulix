using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

// Yenileme, hatırlatma ve zamanlayıcı okumaları (dilim 8). Çağıran görünürlüğü kayıt boru hattında doğrulamış ya da sistem kimliğiyle (kendi tenant'ı) çalışıyor olmalıdır.
internal sealed partial class ContractReader
{
    public async Task<List<ContractRenewal>> RenewalsAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractRenewal>().AsNoTracking().Where(r => r.ContractId == contractId).OrderBy(r => r.PeriodEnd).ThenBy(r => r.CreatedAt).ToListAsync(cancellationToken);

    public async Task<List<ContractRenewal>> RenewalsOfAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken) =>
        await data.Set<ContractRenewal>().AsNoTracking().Where(r => contractIds.Contains(r.ContractId)).ToListAsync(cancellationToken);

    public async Task<List<ContractReminder>> RemindersAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractReminder>().AsNoTracking().Where(r => r.ContractId == contractId).OrderBy(r => r.PeriodKey).ThenByDescending(r => r.ThresholdDays).ToListAsync(cancellationToken);

    public async Task<List<ContractReminder>> RemindersOfAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken) =>
        await data.Set<ContractReminder>().AsNoTracking().Where(r => contractIds.Contains(r.ContractId)).ToListAsync(cancellationToken);

    /// <summary>
    /// Zamanlayıcının sözleşme adayları: imzası tamamlanmış ve yürürlük döngüsünde olan (başlangıcı bekleyen, aktif, askıda) sözleşmeler. Tek turda en fazla
    /// <paramref name="limit"/> sözleşme (en yakın bitişliler önce); kalanlar sonraki turda işlenir.
    /// </summary>
    public async Task<List<Contract>> SchedulerContractsAsync(int limit, CancellationToken cancellationToken) =>
        await data.Set<Contract>().AsNoTracking()
            .Where(c => c.SignatureStatus == SignatureStatus.Completed
                && (c.EffectiveStatus == EffectiveStatus.AwaitingStart || c.EffectiveStatus == EffectiveStatus.Active || c.EffectiveStatus == EffectiveStatus.Suspended))
            .OrderBy(c => c.EndDate == null).ThenBy(c => c.EndDate).Take(limit).ToListAsync(cancellationToken);

    /// <summary>Onay hedef tarihi geçmiş, hâlâ onay bekleyen sözleşmeler (güncel sürümün hedef tarihine göre).</summary>
    public async Task<List<(Contract Contract, ContractVersion Version)>> OverdueApprovalsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var rows = await (
            from c in data.Set<Contract>().AsNoTracking()
            join v in data.Set<ContractVersion>().AsNoTracking() on new { c.Id, No = c.CurrentVersionNo } equals new { Id = v.ContractId, No = v.VersionNo }
            where c.ApprovalStatus == ApprovalStatus.Pending && v.ApprovalDueAt != null && v.ApprovalDueAt <= now
            orderby v.ApprovalDueAt
            select new { Contract = c, Version = v }).Take(limit).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.Contract, r.Version))];
    }

    /// <summary>Etkin taahhüdü olan sözleşmenin açık gerçekleşmelerini toplu iptal etmek için (fesih): etkin taahhütler.</summary>
    public async Task<List<ContractCommitment>> ActiveCommitmentsOfContractAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractCommitment>().AsNoTracking().Where(c => c.ContractId == contractId && c.Status == CommitmentStatus.Active).ToListAsync(cancellationToken);
}
