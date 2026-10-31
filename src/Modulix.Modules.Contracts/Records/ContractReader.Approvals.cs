using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

// Onay kuralı, vekâlet ve karar okumaları (dilim 5). Çağıran sözleşmenin görünürlüğünü (izin ve kapsam) kayıt boru hattında doğrulamış olmalıdır.
internal sealed partial class ContractReader
{
    public async Task<List<ContractApprovalRuleInfo>> ActiveRulesAsync(CancellationToken cancellationToken) =>
        await data.Set<ContractApprovalRule>().AsNoTracking().Where(r => r.IsActive)
            .Select(r => new ContractApprovalRuleInfo(
                r.Id, r.Version, r.Name, r.Priority, r.Step, r.ContractType, r.Currency, r.MinGrossTotal, r.MinDiscountPercent, r.MinTermMonths,
                r.RequiresNonStandardTerms, r.MinRiskLevel, r.ApproverId))
            .ToListAsync(cancellationToken);

    /// <summary>Şu anda geçerli etkin vekâletler (başlamış, bitmemiş).</summary>
    public async Task<List<ContractApprovalDelegation>> ActiveDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await data.Set<ContractApprovalDelegation>().AsNoTracking()
            .Where(d => d.IsActive && d.StartsAt <= now && (d.EndsAt == null || d.EndsAt > now)).ToListAsync(cancellationToken);

    public async Task<List<ContractApprovalDecision>> DecisionsAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractApprovalDecision>().AsNoTracking().Where(d => d.ContractId == contractId).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken);

    public async Task<List<ContractApprovalDecision>> DecisionsAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken) =>
        await data.Set<ContractApprovalDecision>().AsNoTracking().Where(d => contractIds.Contains(d.ContractId)).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken);

    /// <summary>Verilen sözleşmelerin tüm dondurulmuş sürümleri (iş listesi hangisinin güncel olduğunu bellekte seçer; en fazla bir sayfa kadar sözleşme).</summary>
    public async Task<List<ContractVersion>> VersionsForAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken) =>
        await data.Set<ContractVersion>().AsNoTracking().Where(v => contractIds.Contains(v.ContractId)).ToListAsync(cancellationToken);

    /// <summary>Sözleşmenin en yüksek kalem indirim yüzdesi (kalem yoksa 0).</summary>
    public async Task<decimal> MaxLineDiscountAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var discounts = await data.Set<ContractLine>().AsNoTracking().Where(l => l.ContractId == contractId).Select(l => l.DiscountPercent).ToListAsync(cancellationToken);
        return discounts.Count == 0 ? 0m : discounts.Max();
    }

    /// <summary>Sözleşmelerin en son onaya gönderimi (kim, ne zaman); gönderimi olmayan sözleşme sözlükte yer almaz.</summary>
    public async Task<Dictionary<Guid, (Guid? ActorId, DateTimeOffset At)>> SubmissionsAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken)
    {
        var events = await data.Set<ContractEvent>().AsNoTracking()
            .Where(e => contractIds.Contains(e.ContractId) && e.Kind == ContractEventKinds.SubmittedForApproval)
            .Select(e => new { e.ContractId, e.ActorId, e.CreatedAt }).ToListAsync(cancellationToken);
        return events.GroupBy(e => e.ContractId).ToDictionary(g => g.Key, g => { var last = g.OrderByDescending(e => e.CreatedAt).First(); return (last.ActorId, last.CreatedAt); });
    }
}
