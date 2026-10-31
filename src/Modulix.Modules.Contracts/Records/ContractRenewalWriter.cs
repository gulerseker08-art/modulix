using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Yenileme/hatırlatma/fesih tablolarına yazım (iş akışı kapısının sözleşme etkisi olarak sözleşme güncellemesiyle aynı işlemde çalışır; ADR-0022, dilim 8). Kararlar serviste
/// saf kurallarla verilir; burası yalnızca değişikliği uygular. (Sözleşme, dönem) ve (sözleşme, dönem, tür, eşik) benzersiz indeksleri mükerrer kararı ve hatırlatmayı engeller.
/// </summary>
internal static class ContractRenewalWriter
{
    public static void AddRenewal(
        ContractsStoreContext context, Guid contractId, DateOnly periodEnd, string outcome, string source, DateOnly? newEnd = null, Guid? resultContractId = null, string? reason = null, string? note = null) =>
        context.Data.Set<ContractRenewal>().Add(new ContractRenewal
        {
            ContractId = contractId,
            PeriodEnd = periodEnd,
            Outcome = outcome,
            NewEndDate = newEnd,
            ResultContractId = resultContractId,
            Reason = reason,
            Note = note is { Length: > ContractRenewal.NoteMaxLength } ? note[..ContractRenewal.NoteMaxLength] : note,
            Source = source,
            DecidedBy = context.CurrentUserId,
        });

    public static void AddReminder(ContractsStoreContext context, Guid contractId, DateOnly periodKey, string kind, int threshold, string status, Guid? activityId, string? note) =>
        context.Data.Set<ContractReminder>().Add(new ContractReminder
        {
            ContractId = contractId,
            PeriodKey = periodKey,
            Kind = kind,
            ThresholdDays = threshold,
            Status = status,
            ActivityId = activityId,
            Note = note is { Length: > Contract.ReasonMaxLength } ? note[..Contract.ReasonMaxLength] : note,
        });

    /// <summary>Daha önce başarısız olmuş hatırlatmayı yeniden denemenin sonucunu yazar (satır tekildir, güncellenir).</summary>
    public static async Task UpdateReminderAsync(ContractsStoreContext context, Guid reminderId, string status, Guid? activityId, string? note, CancellationToken cancellationToken)
    {
        var reminder = await context.Data.Set<ContractReminder>().SingleAsync(r => r.Id == reminderId, cancellationToken);
        reminder.Status = status;
        reminder.ActivityId = activityId ?? reminder.ActivityId;
        reminder.Note = note;
        reminder.Touch();
    }

    /// <summary>
    /// Feshedilen sözleşmenin etkin taahhütlerini ve açık gerçekleşmelerini iptal eder (taahhüt takibi sona erer; tamamlanmış kayıtlar korunur). Fesih işlemiyle aynı işlemdedir.
    /// </summary>
    public static async Task CancelCommitmentsAsync(ContractsStoreContext context, Guid contractId, string reason, CancellationToken cancellationToken)
    {
        var commitments = await context.Data.Set<ContractCommitment>().Where(c => c.ContractId == contractId && c.Status == CommitmentStatus.Active).ToListAsync(cancellationToken);
        foreach (var commitment in commitments)
        {
            commitment.Status = CommitmentStatus.Cancelled;
            commitment.Touch();
            foreach (var occurrence in await context.Data.Set<ContractCommitmentOccurrence>().Where(o => o.CommitmentId == commitment.Id).ToListAsync(cancellationToken))
            {
                if (CommitmentTracker.IsCompletable(occurrence.Status, occurrence.CompletedOn))
                {
                    occurrence.Status = OccurrenceStatus.Cancelled;
                    occurrence.Note = reason.Length > ContractCommitmentOccurrence.NoteMaxLength ? reason[..ContractCommitmentOccurrence.NoteMaxLength] : reason;
                    occurrence.Touch();
                }
            }
        }
    }
}
