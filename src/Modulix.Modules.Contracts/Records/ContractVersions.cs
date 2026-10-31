using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sürüm dondurma ve sürüm durumu yazımı; iş akışı kapısının etkisi olarak sözleşme geçişiyle aynı işlemde çalışır (ADR-0022, dilim 2).
/// Anlık görüntü başlık, taraf ve kalemlerden kurulur; aynı sürüm numarası için zaten dondurulmuş içerik varsa yeniden dondurulmaz.
/// </summary>
internal static class ContractVersions
{
    public static async Task<ContractSnapshot> BuildSnapshotAsync(ContractsStoreContext context, Contract c, CancellationToken cancellationToken)
    {
        var data = context.Data;
        var parties = await data.Set<ContractParty>().AsNoTracking().Where(p => p.ContractId == c.Id).OrderBy(p => p.Position).ThenBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var lines = await data.Set<ContractLine>().AsNoTracking().Where(l => l.ContractId == c.Id).OrderBy(l => l.Position).ThenBy(l => l.CreatedAt).ToListAsync(cancellationToken);
        return new ContractSnapshot(
            c.Number, c.CurrentVersionNo, c.Title, c.Type, c.Description, c.PartyId, c.PersonId, c.StartDate, c.EndDate, c.IsIndefinite, c.Currency,
            c.PriceIncludesTax, c.ContractValue, c.PaymentTerms, c.RenewalMode, c.RenewalPeriodMonths, c.NoticePeriodDays, c.NoticeDeadline,
            c.Confidentiality, c.NetTotal, c.TaxTotal, c.GrossTotal, c.RecurringSummary,
            [.. parties.Select(p => new SnapshotParty(p.Id, p.Role, p.PartyId, p.PersonId, p.Position, p.Notes))],
            [.. lines.Select(l => new SnapshotLine(l.StableKey, l.Position, l.Kind, l.ProductCode, l.Name, l.Description, l.Quantity, l.UnitName, l.UnitPrice,
                l.DiscountPercent, l.TaxRatePercent, l.BillingPeriod, l.Net, l.Tax, l.Gross))],
            c.HasNonStandardTerms, c.RiskLevel);
    }

    /// <summary>Güncel sürüm numarası için dondurulmuş satır yoksa oluşturur; varsa durumunu <see cref="VersionStatus.Frozen"/> yapar. Satırı döndürür.</summary>
    public static async Task<ContractVersion> FreezeAsync(ContractsStoreContext context, Contract contract, string kind, string? reason, CancellationToken cancellationToken)
    {
        var existing = await context.Data.Set<ContractVersion>().SingleOrDefaultAsync(v => v.ContractId == contract.Id && v.VersionNo == contract.CurrentVersionNo, cancellationToken);
        if (existing is not null)
        {
            existing.Status = VersionStatus.Frozen;
            existing.Kind = kind;
            existing.Touch();
            return existing;
        }

        var json = ContractSnapshots.Serialize(await BuildSnapshotAsync(context, contract, cancellationToken));
        var version = new ContractVersion
        {
            ContractId = contract.Id,
            VersionNo = contract.CurrentVersionNo,
            Kind = kind,
            Status = VersionStatus.Frozen,
            Snapshot = json,
            Sha256 = ContractSnapshots.Hash(json),
            Reason = reason is { Length: > Contract.ReasonMaxLength } ? reason[..Contract.ReasonMaxLength] : reason,
            CreatedBy = context.CurrentUserId,
        };
        context.Data.Set<ContractVersion>().Add(version);
        return version;
    }

    /// <summary>
    /// Onaya gönderim: sürümü dondurur ve yeni bir onay turu başlatır (tur numarası artar, plan ve hedef tarih yazılır). Önceki turun kararları
    /// (geri çekilen onay) yeni turun adımlarına sayılmaz; kararlar tur numarasıyla ayrılır.
    /// </summary>
    public static async Task StartApprovalAsync(
        ContractsStoreContext context, Contract contract, string? reason, ContractApprovalPlan plan, DateTimeOffset? dueAt, CancellationToken cancellationToken)
    {
        var version = await FreezeAsync(context, contract, VersionKinds.Approval, reason, cancellationToken);
        version.ApprovalRound++;
        version.ApprovalPlan = ContractApprovalPlans.Serialize(plan);
        version.ApprovalDueAt = dueAt;
        version.CreatedBy ??= context.CurrentUserId;
    }

    /// <summary>İptal edilen sözleşmenin teklif kalemlerini serbest bırakır: kalem yeniden bir sözleşmeye alınabilir (teklif izi sözleşmede kalır).</summary>
    public static async Task ReleaseQuoteLinesAsync(ContractsStoreContext context, Contract contract, CancellationToken cancellationToken)
    {
        foreach (var line in await context.Data.Set<ContractLine>().Where(l => l.ContractId == contract.Id && l.SourceQuoteId != null).ToListAsync(cancellationToken))
        {
            line.SourceQuoteId = null;
            line.Touch();
        }
    }

    /// <summary>Güncel sürümün durumunu yazar (onaylandı, reddedildi, geri çekildi, geçersiz kılındı).</summary>
    public static async Task SetStatusAsync(ContractsStoreContext context, Contract contract, string status, CancellationToken cancellationToken)
    {
        var current = await context.Data.Set<ContractVersion>().SingleOrDefaultAsync(v => v.ContractId == contract.Id && v.VersionNo == contract.CurrentVersionNo, cancellationToken);
        if (current is not null && current.Status != status)
        {
            current.Status = status;
            current.Touch();
        }
    }

    /// <summary>
    /// Yeni revizyon: güncel sürüm numarası için dondurulmuş satır varsa geçersiz kılınır (imzalanmışsa <see cref="VersionStatus.Signed"/>
    /// korunur) ve sözleşmenin sürüm sayacı artar. Yürürlükteki sürüm işaretçisine dokunulmaz.
    /// </summary>
    public static async Task OpenNextVersionAsync(ContractsStoreContext context, Contract contract, CancellationToken cancellationToken)
    {
        var current = await context.Data.Set<ContractVersion>().SingleOrDefaultAsync(v => v.ContractId == contract.Id && v.VersionNo == contract.CurrentVersionNo, cancellationToken);
        if (current is not null && current.Status != VersionStatus.Signed)
        {
            current.Status = VersionStatus.Superseded;
            current.Touch();
        }

        contract.CurrentVersionNo++;
    }

    /// <summary>Bir onay kararı satırı ekler (eklenir, değişmez). <paramref name="round"/> sürümün o anki onay turudur.</summary>
    public static void AddDecision(
        ContractsStoreContext context, Contract contract, int round, int step, string decision, IReadOnlyCollection<Guid> onBehalfOf, string? reason)
    {
        context.Data.Set<ContractApprovalDecision>().Add(new ContractApprovalDecision
        {
            ContractId = contract.Id,
            VersionNo = contract.CurrentVersionNo,
            Round = round,
            StepNo = step,
            ActorId = context.CurrentUserId ?? Guid.Empty,
            OnBehalfOf = string.Join(',', onBehalfOf),
            Decision = decision,
            Reason = reason is { Length: > Contract.ReasonMaxLength } ? reason[..Contract.ReasonMaxLength] : reason,
        });
    }
}
