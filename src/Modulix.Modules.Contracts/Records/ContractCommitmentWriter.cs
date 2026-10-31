using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

internal sealed record NewCommitmentFile(string FileName, string ContentType, byte[] Content, string Sha256);

/// <summary>Bir gerçekleşmedeki değişiklik; boş bırakılan alan değişmez.</summary>
internal sealed record OccurrenceMutation(
    Guid OccurrenceId,
    string? Status = null,
    DateOnly? CompletedOn = null,
    Guid? CompletedBy = null,
    DateTimeOffset? RecordedAt = null,
    decimal? ActualValue = null,
    bool? MetTarget = null,
    string? Note = null,
    NewCommitmentFile? File = null,
    DateTimeOffset? BreachedAt = null,
    string? BreachReason = null,
    string? FollowUpStatus = null,
    Guid? FollowUpActivityId = null,
    DateTimeOffset? EscalatedAt = null,
    Guid? EscalationActivityId = null,
    Guid? WaivedBy = null,
    string? WaiveReason = null);

internal sealed record CommitmentHistory(string Kind, string? Reason);

/// <summary>Taahhütte tek işlemde uygulanacak değişiklik: gerçekleşmeler, taahhüt durumu, sözleşme tarihçesi ve kayan ufuk genişletmesi.</summary>
internal sealed record CommitmentMutation(
    string? CommitmentStatus, IReadOnlyList<OccurrenceMutation> Occurrences, IReadOnlyList<CommitmentHistory> History, DateOnly? ExtendHorizonFrom = null);

/// <summary>
/// Taahhüt tablolarına yazım (iş akışı kapısının taahhüt etkisi olarak taahhüt güncellemesiyle aynı işlemde çalışır; ADR-0022, dilim 7). Karar (ihlal mi, hedef tuttu mu,
/// bağımlılık engeli) serviste saf kurallarla verilir; burası yalnızca verilen değişikliği uygular ve gerçekleşme satırı üretimini yapar.
/// </summary>
internal static class ContractCommitmentWriter
{
    /// <summary>Kayan ufuk: dönemsel ve bitişsiz taahhüt için bugünden bu kadar ay sonrasına kadar gerçekleşme satırı üretilir.</summary>
    public const int HorizonMonths = 12;

    /// <summary>
    /// Eksik gerçekleşme satırlarını üretir (idempotent: var olan sıralar atlanır; (taahhüt, sıra) benzersiz indeksi de tekrarı engeller). Tekrar bitişi yoksa sözleşme
    /// bitişi (belirsiz süreli değilse), o da yoksa kayan ufuk sınırdır.
    /// </summary>
    public static void Materialize(ContractsStoreContext context, ContractCommitment commitment, Contract contract, IReadOnlyCollection<int> existingSequences, DateOnly today)
    {
        var end = commitment.RecurrenceEndDate ?? (contract.IsIndefinite ? null : contract.EndDate);
        foreach (var (sequence, due) in CommitmentSchedule.DueDates(commitment.Kind, commitment.DueDate, commitment.Recurrence, end, today.AddMonths(HorizonMonths)))
        {
            if (!existingSequences.Contains(sequence))
            {
                context.Data.Set<ContractCommitmentOccurrence>().Add(new ContractCommitmentOccurrence
                {
                    CommitmentId = commitment.Id,
                    ContractId = commitment.ContractId,
                    Sequence = sequence,
                    DueDate = due,
                });
            }
        }
    }

    public static async Task ApplyAsync(ContractsStoreContext context, ContractCommitment commitment, CommitmentMutation mutation, CancellationToken cancellationToken)
    {
        var occurrences = await context.Data.Set<ContractCommitmentOccurrence>().Where(o => o.CommitmentId == commitment.Id).ToListAsync(cancellationToken);
        foreach (var m in mutation.Occurrences)
        {
            var o = occurrences.Single(x => x.Id == m.OccurrenceId);
            if (m.Status is not null) { o.Status = m.Status; }
            if (m.CompletedOn is not null) { o.CompletedOn = m.CompletedOn; }
            if (m.CompletedBy is not null) { o.CompletedBy = m.CompletedBy; }
            if (m.RecordedAt is not null) { o.RecordedAt = m.RecordedAt; }
            if (m.ActualValue is not null) { o.ActualValue = m.ActualValue; }
            if (m.MetTarget is not null) { o.MetTarget = m.MetTarget; }
            if (m.Note is not null) { o.Note = m.Note.Length > ContractCommitmentOccurrence.NoteMaxLength ? m.Note[..ContractCommitmentOccurrence.NoteMaxLength] : m.Note; }
            if (m.BreachedAt is not null) { o.BreachedAt = m.BreachedAt; }
            if (m.BreachReason is not null) { o.BreachReason = m.BreachReason.Length > Contract.ReasonMaxLength ? m.BreachReason[..Contract.ReasonMaxLength] : m.BreachReason; }
            if (m.FollowUpStatus is not null) { o.FollowUpStatus = m.FollowUpStatus; }
            if (m.FollowUpActivityId is not null) { o.FollowUpActivityId = m.FollowUpActivityId; }
            if (m.EscalatedAt is not null) { o.EscalatedAt = m.EscalatedAt; }
            if (m.EscalationActivityId is not null) { o.EscalationActivityId = m.EscalationActivityId; }
            if (m.WaivedBy is not null) { o.WaivedBy = m.WaivedBy; }
            if (m.WaiveReason is not null) { o.WaiveReason = m.WaiveReason.Length > Contract.ReasonMaxLength ? m.WaiveReason[..Contract.ReasonMaxLength] : m.WaiveReason; }

            if (m.File is { } file)
            {
                var stored = new ContractCommitmentFile
                {
                    ContractId = commitment.ContractId,
                    OccurrenceId = o.Id,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Content.Length,
                    Sha256 = file.Sha256,
                    Content = file.Content,
                    CreatedBy = context.CurrentUserId,
                };
                context.Data.Set<ContractCommitmentFile>().Add(stored);
                o.EvidenceFileId = stored.Id;
            }

            o.Touch();
        }

        if (mutation.CommitmentStatus is not null)
        {
            commitment.Status = mutation.CommitmentStatus;
        }

        var contract = await context.Data.Set<Contract>().SingleAsync(c => c.Id == commitment.ContractId, cancellationToken);
        foreach (var h in mutation.History)
        {
            ContractHistory.Add(context, contract, h.Kind, h.Reason);
        }

        if (mutation.ExtendHorizonFrom is { } today && commitment.Status == CommitmentStatus.Active && commitment.Kind == CommitmentKind.Periodic)
        {
            Materialize(context, commitment, contract, [.. occurrences.Select(o => o.Sequence)], today);
        }
    }
}
