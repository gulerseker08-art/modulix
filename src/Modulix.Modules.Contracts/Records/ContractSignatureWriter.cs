using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>Yeni imzacı (istek açılırken).</summary>
internal sealed record NewSignatory(int Position, string Kind, Guid? UserId, string Name, string? Email, string? Capacity, bool IsRequired, string Status, DateTimeOffset? SentAt);

/// <summary>Yeni imza isteği: hangi belge, hangi yöntem ve kimler. Sıradaki (Sent) imzacılar için "Sent" olayı, istek için "Requested" olayı yazılır.</summary>
internal sealed record NewSignatureRequest(
    Guid DocumentId, string DocumentSha256, string Method, string? Provider, string? ProviderReference, string Mode, DateTimeOffset ExpiresAt, IReadOnlyList<NewSignatory> Signatories);

/// <summary>Bir imzacıdaki değişiklik; boş bırakılan alan değişmez.</summary>
internal sealed record SignatoryChange(
    Guid Id,
    string? Status = null,
    DateTimeOffset? SentAt = null,
    DateTimeOffset? SignedAt = null,
    string? Assurance = null,
    Guid? VerifiedBy = null,
    DateTimeOffset? VerifiedAt = null,
    string? DeclineReason = null,
    bool Remind = false,
    bool AttachFile = false);

internal sealed record NewSignatureEvent(string Source, string EventType, Guid? SignatoryId, string? ProviderEventId, int? Sequence, string Outcome, string? Note, Guid? ActorId);

internal sealed record NewSignatureFile(string FileName, string ContentType, byte[] Content, string Sha256);

/// <summary>İmza isteğinde tek işlemde uygulanacak değişiklik (imzacılar, istek durumu, olaylar, kanıt dosyası, sürüm).</summary>
internal sealed record SignatureMutation(
    Guid RequestId,
    IReadOnlyList<SignatoryChange> Signatories,
    string? RequestStatus,
    string? CloseReason,
    DateTimeOffset? CompletedAt,
    int? LastEventSequence,
    IReadOnlyList<NewSignatureEvent> Events,
    NewSignatureFile? File = null,
    bool MarkVersionSigned = false);

/// <summary>
/// İmza tablolarına yazım (iş akışı kapısının etkisi olarak sözleşme güncellemesiyle aynı işlemde çalışır; ADR-0022, dilim 6). Durum kararları (kim imzalayabilir,
/// istek hangi duruma geçer) serviste saf <see cref="SignatureFlow"/> ile verilir; burası yalnızca verilen değişikliği uygular.
/// </summary>
internal static class ContractSignatureWriter
{
    public static void Create(ContractsStoreContext context, Contract contract, NewSignatureRequest input)
    {
        var request = new ContractSignatureRequest
        {
            ContractId = contract.Id,
            VersionNo = contract.CurrentVersionNo,
            DocumentId = input.DocumentId,
            DocumentSha256 = input.DocumentSha256,
            Method = input.Method,
            Provider = input.Provider,
            ProviderReference = input.ProviderReference,
            Mode = input.Mode,
            Status = SignatureRequestStatus.Pending,
            ExpiresAt = input.ExpiresAt,
            CreatedBy = context.CurrentUserId,
        };
        context.Data.Set<ContractSignatureRequest>().Add(request);
        context.Data.Set<ContractSignatureEvent>().Add(Event(contract.Id, request.Id, new NewSignatureEvent("User", SignatureEventType.Requested, null, null, null, SignatureEventOutcome.Applied, null, context.CurrentUserId)));

        foreach (var s in input.Signatories)
        {
            var signatory = new ContractSignatory
            {
                RequestId = request.Id,
                ContractId = contract.Id,
                Position = s.Position,
                Kind = s.Kind,
                UserId = s.UserId,
                Name = s.Name,
                Email = s.Email,
                Capacity = s.Capacity,
                IsRequired = s.IsRequired,
                Status = s.Status,
                SentAt = s.SentAt,
            };
            context.Data.Set<ContractSignatory>().Add(signatory);
            if (s.Status == SignatoryStatus.Sent)
            {
                context.Data.Set<ContractSignatureEvent>().Add(Event(contract.Id, request.Id, new NewSignatureEvent("System", SignatureEventType.Sent, signatory.Id, null, null, SignatureEventOutcome.Applied, null, null)));
            }
        }

        contract.Touch();
    }

    public static async Task ApplyAsync(ContractsStoreContext context, Contract contract, SignatureMutation mutation, CancellationToken cancellationToken)
    {
        var request = await context.Data.Set<ContractSignatureRequest>().SingleAsync(r => r.Id == mutation.RequestId, cancellationToken);
        var signatories = await context.Data.Set<ContractSignatory>().Where(s => s.RequestId == mutation.RequestId).ToDictionaryAsync(s => s.Id, cancellationToken);

        foreach (var change in mutation.Signatories)
        {
            var s = signatories[change.Id];
            if (change.Status is not null) { s.Status = change.Status; }
            if (change.SentAt is not null) { s.SentAt = change.SentAt; }
            if (change.SignedAt is not null) { s.SignedAt = change.SignedAt; }
            if (change.Assurance is not null) { s.Assurance = change.Assurance; }
            if (change.VerifiedBy is not null) { s.VerifiedBy = change.VerifiedBy; }
            if (change.VerifiedAt is not null) { s.VerifiedAt = change.VerifiedAt; }
            if (change.DeclineReason is not null) { s.DeclineReason = change.DeclineReason.Length > Contract.ReasonMaxLength ? change.DeclineReason[..Contract.ReasonMaxLength] : change.DeclineReason; }
            if (change.Remind)
            {
                s.ReminderCount++;
                s.LastReminderAt = DateTimeOffset.UtcNow;
            }

            if (change.AttachFile && mutation.File is { } file)
            {
                var stored = new ContractSignatureFile
                {
                    ContractId = contract.Id,
                    SignatoryId = s.Id,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Content.Length,
                    Sha256 = file.Sha256,
                    Content = file.Content,
                    CreatedBy = context.CurrentUserId,
                };
                context.Data.Set<ContractSignatureFile>().Add(stored);
                s.EvidenceFileId = stored.Id;
            }

            s.Touch();
        }

        if (mutation.RequestStatus is not null) { request.Status = mutation.RequestStatus; }
        if (mutation.CloseReason is not null) { request.CloseReason = mutation.CloseReason.Length > Contract.ReasonMaxLength ? mutation.CloseReason[..Contract.ReasonMaxLength] : mutation.CloseReason; }
        if (mutation.CompletedAt is not null) { request.CompletedAt = mutation.CompletedAt; }
        if (mutation.LastEventSequence is { } sequence && sequence > request.LastEventSequence) { request.LastEventSequence = sequence; }
        request.Touch();

        foreach (var e in mutation.Events)
        {
            context.Data.Set<ContractSignatureEvent>().Add(Event(contract.Id, request.Id, e));
        }

        if (mutation.MarkVersionSigned)
        {
            await ContractVersions.SetStatusAsync(context, contract, VersionStatus.Signed, cancellationToken);
        }

        // Sözleşme satırı değişmese de sürüm sayacı artar: eşzamanlı iki imza işleminden yalnızca biri kazanır (409).
        contract.Touch();
    }

    /// <summary>Açık imza isteğini (varsa) iptal eder: sözleşme iptal edilirken ya da imza iptal edilirken aynı işlemde çalışır.</summary>
    public static async Task CancelOpenAsync(ContractsStoreContext context, Contract contract, string reason, CancellationToken cancellationToken)
    {
        var open = await context.Data.Set<ContractSignatureRequest>()
            .Where(r => r.ContractId == contract.Id && (r.Status == SignatureRequestStatus.Pending || r.Status == SignatureRequestStatus.PartiallySigned))
            .ToListAsync(cancellationToken);
        foreach (var request in open)
        {
            request.Status = SignatureRequestStatus.Cancelled;
            request.CloseReason = reason.Length > Contract.ReasonMaxLength ? reason[..Contract.ReasonMaxLength] : reason;
            request.Touch();
            foreach (var s in await context.Data.Set<ContractSignatory>().Where(s => s.RequestId == request.Id).ToListAsync(cancellationToken))
            {
                if (SignatoryStatus.IsOpen(s.Status))
                {
                    s.Status = SignatoryStatus.Cancelled;
                    s.Touch();
                }
            }

            context.Data.Set<ContractSignatureEvent>().Add(Event(contract.Id, request.Id, new NewSignatureEvent("System", SignatureEventType.Cancelled, null, null, null, SignatureEventOutcome.Applied, reason, context.CurrentUserId)));
        }
    }

    private static ContractSignatureEvent Event(Guid contractId, Guid requestId, NewSignatureEvent e) => new()
    {
        ContractId = contractId,
        RequestId = requestId,
        SignatoryId = e.SignatoryId,
        Source = e.Source,
        EventType = e.EventType,
        ProviderEventId = e.ProviderEventId,
        Sequence = e.Sequence,
        Outcome = e.Outcome,
        Note = e.Note is { Length: > ContractSignatureEvent.NoteMaxLength } note ? note[..ContractSignatureEvent.NoteMaxLength] : e.Note,
        ActorId = e.ActorId,
    };
}
