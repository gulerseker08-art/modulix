using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Gerçekleşme tamamlama girdisi. Ölçümlü taahhütte <see cref="ActualValue"/> zorunludur; kanıt dosyası isteğe bağlıdır.</summary>
public sealed record CompleteOccurrenceInput(DateOnly CompletedOn, decimal? ActualValue, string? Note, string? FileName, byte[]? Content);

/// <summary>İş listesi süzgeci. <see cref="Status"/>: Open, Breached, Fulfilled, Waived, Cancelled ya da Overdue (türetilmiş: son tarihi geçmiş, tamamlanmamış).</summary>
public sealed record OccurrenceFilter(
    Guid? ContractId = null, Guid? PartyId = null, bool Mine = false, string? Status = null, DateOnly? DueFrom = null, DateOnly? DueTo = null, bool IncludeClosedCommitments = false, int Limit = 300);

public sealed record OccurrenceView(
    Guid Id,
    Guid CommitmentId,
    Guid ContractId,
    string ContractNumber,
    string ContractTitle,
    string CommitmentTitle,
    string Category,
    Guid? ResponsibleUserId,
    int Sequence,
    DateOnly DueDate,
    string Status,
    bool IsOverdue,
    int DaysOverdue,
    DateOnly? CompletedOn,
    Guid? CompletedBy,
    DateTimeOffset? RecordedAt,
    decimal? ActualValue,
    bool? MetTarget,
    string? MetricName,
    string? Unit,
    string? Direction,
    decimal? TargetValue,
    decimal? Tolerance,
    string? Note,
    Guid? EvidenceFileId,
    string? EvidenceFileName,
    DateTimeOffset? BreachedAt,
    string? BreachReason,
    string FollowUpStatus,
    Guid? FollowUpActivityId,
    DateTimeOffset? EscalatedAt,
    Guid? EscalationActivityId,
    Guid? WaivedBy,
    string? WaiveReason,
    DateOnly? BlockedByDue,
    bool CanComplete);

/// <summary>Değerlendirme turunun özeti.</summary>
public sealed record CommitmentEvaluation(int Breached, int Escalated, int FollowUpsCreated, int FollowUpsUnavailable, int OccurrencesCreated);

public sealed record CommitmentFileContent(Guid Id, string FileName, string ContentType, byte[] Content, string Sha256);

/// <summary>
/// Taahhüt takibi (ADR-0022, dilim 7). Tanım (<c>contracts.contract-commitment</c>) kayıt boru hattından; gerçekleşme tamamlama, muafiyet, iptal ve değerlendirme (ihlal,
/// eskalasyon, takip aktivitesi, kayan ufuk) bu serviste yapılır ve taahhüt güncellemesiyle aynı işlemde, sürüm denetimli yazılır. Her dönem ayrı satırdır ve tamamlanmış
/// satır üzerine yazılmaz. <b>Ceza ya da finansal sonuç uygulanmaz.</b> Değerlendirme tekrar çalıştırılabilir (idempotent).
/// </summary>
public interface IContractCommitments
{
    Task<RecordResult<IReadOnlyList<OccurrenceView>>> OccurrencesAsync(OccurrenceFilter filter, CancellationToken cancellationToken);

    Task<RecordResult<OccurrenceView>> CompleteAsync(Guid commitmentId, Guid occurrenceId, CompleteOccurrenceInput input, CancellationToken cancellationToken);

    Task<RecordResult<OccurrenceView>> WaiveAsync(Guid commitmentId, Guid occurrenceId, string reason, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CancelAsync(Guid commitmentId, string reason, CancellationToken cancellationToken);

    /// <summary>İhlal, eskalasyon, takip aktivitesi ve kayan ufuk turu (<paramref name="contractId"/> boşsa kullanıcının görebildiği bütün sözleşmeler).</summary>
    Task<RecordResult<CommitmentEvaluation>> EvaluateAsync(Guid? contractId, CancellationToken cancellationToken);

    Task<RecordResult<CommitmentFileContent>> GetEvidenceAsync(Guid commitmentId, Guid occurrenceId, Guid fileId, CancellationToken cancellationToken);
}

internal sealed class ContractCommitments(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    ITenantContextAccessor tenant,
    TimeProvider time,
    IContractPolicyProvider? policy = null,
    IFollowUpActivities? followUps = null) : IContractCommitments
{
    private const string Key = ContractsModule.Entities.ContractCommitment;
    private const int PageSize = RecordQuery.MaxPageSize;

    // ---- okuma

    public async Task<RecordResult<IReadOnlyList<OccurrenceView>>> OccurrencesAsync(OccurrenceFilter filter, CancellationToken cancellationToken)
    {
        var filters = new Dictionary<string, string>();
        if (!filter.IncludeClosedCommitments) { filters["status"] = CommitmentStatus.Active; }
        if (filter.ContractId is { } contractId) { filters["contractId"] = contractId.ToString(); }
        if (filter.PartyId is { } partyId) { filters["partyId"] = partyId.ToString(); }
        if (filter.Mine) { filters["responsibleUserId"] = "me"; }

        var (ids, error) = await VisibleCommitmentIdsAsync(filters, cancellationToken);
        if (error is not null)
        {
            return RecordResult<IReadOnlyList<OccurrenceView>>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var today = await TodayAsync(cancellationToken);
        var views = await ViewsAsync(ids, today, cancellationToken);
        IEnumerable<OccurrenceView> result = views;
        if (filter.Status == "Overdue") { result = result.Where(v => v.IsOverdue); }
        else if (filter.Status is { Length: > 0 } status && OccurrenceStatus.All.Contains(status)) { result = result.Where(v => v.Status == status); }
        if (filter.DueFrom is { } from) { result = result.Where(v => v.DueDate >= from); }
        if (filter.DueTo is { } to) { result = result.Where(v => v.DueDate <= to); }

        return RecordResult<IReadOnlyList<OccurrenceView>>.Ok([.. result.OrderBy(v => v.DueDate).ThenBy(v => v.CommitmentTitle, StringComparer.Ordinal).Take(Math.Clamp(filter.Limit, 1, 1000))]);
    }

    public async Task<RecordResult<CommitmentFileContent>> GetEvidenceAsync(Guid commitmentId, Guid occurrenceId, Guid fileId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, commitmentId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<CommitmentFileContent>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.DocumentsDownload))
        {
            return RecordResult<CommitmentFileContent>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.DocumentsDownload}' izni gerekir.");
        }

        var occurrence = (await reader.OccurrencesOfAsync([commitmentId], cancellationToken)).FirstOrDefault(o => o.Id == occurrenceId);
        return occurrence is not null && await reader.CommitmentFileAsync(occurrenceId, fileId, cancellationToken) is { } file
            ? RecordResult<CommitmentFileContent>.Ok(new CommitmentFileContent(file.Id, file.FileName, file.ContentType, file.Content, file.Sha256))
            : RecordResult<CommitmentFileContent>.Fail(RecordErrorCode.NotFound, "Dosya bulunamadı.");
    }

    // ---- tamamlama

    public async Task<RecordResult<OccurrenceView>> CompleteAsync(Guid commitmentId, Guid occurrenceId, CompleteOccurrenceInput input, CancellationToken cancellationToken)
    {
        var (state, error) = await LoadAsync<OccurrenceView>(commitmentId, occurrenceId, ContractsModule.Permissions.CommitmentsComplete, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        var (record, c, o, occurrences) = state;
        var errors = new List<RecordFieldError>();
        var today = await TodayAsync(cancellationToken);
        if (!CommitmentTracker.IsCompletable(o.Status, o.CompletedOn))
        {
            return Conflict<OccurrenceView>("Tamamlanmış, muaf tutulmuş ya da iptal edilmiş gerçekleşme değiştirilemez; kayıtlar yeniden yazılmaz.");
        }

        if (input.CompletedOn > today) { errors.Add(new RecordFieldError("completedOn", "Tamamlanma tarihi gelecekte olamaz.")); }
        else if (input.CompletedOn < c.StartDate) { errors.Add(new RecordFieldError("completedOn", "Tamamlanma tarihi taahhüdün başlangıcından önce olamaz.")); }

        var measured = !string.IsNullOrWhiteSpace(c.MetricName);
        if (measured && input.ActualValue is null) { errors.Add(new RecordFieldError("actualValue", $"'{c.MetricName}' için gerçekleşen değeri girin.")); }
        if (input.ActualValue is < 0) { errors.Add(new RecordFieldError("actualValue", "Gerçekleşen değer negatif olamaz.")); }
        if (input.Note is { Length: > ContractCommitmentOccurrence.NoteMaxLength }) { errors.Add(new RecordFieldError("note", "Not en fazla 1000 karakter olabilir.")); }

        NewCommitmentFile? file = null;
        if (input.Content is { Length: > 0 } content)
        {
            if (SignatureFileRules.Validate(input.FileName, content, out var contentType) is { } fileError) { errors.Add(new RecordFieldError("file", fileError)); }
            else { file = new NewCommitmentFile(SignatureFileRules.SafeName(input.FileName ?? string.Empty), contentType, content, Convert.ToHexString(SHA256.HashData(content))); }
        }

        if (errors.Count > 0)
        {
            return RecordResult<OccurrenceView>.Fail(RecordErrorCode.ValidationFailed, "Gerçekleşme doğrulanamadı.", errors);
        }

        var blocked = await DependencyBlockAsync(c, o, cancellationToken);
        if (blocked is { } blockedDue)
        {
            return Conflict<OccurrenceView>($"Bu taahhüt, bağlı olduğu taahhüdün {blockedDue:dd.MM.yyyy} tarihli gerçekleşmesi tamamlanmadan kaydedilemez.");
        }

        var met = measured && input.ActualValue is { } actual ? CommitmentMeasure.Meets(c.Direction, c.TargetValue, c.Tolerance, actual) : null;
        var lateBeyondGrace = input.CompletedOn > o.DueDate.AddDays(c.GraceDays);
        var breached = met == false || lateBeyondGrace || o.Status == OccurrenceStatus.Breached;
        var now = time.GetUtcNow();
        var reasons = new List<string>();
        if (met == false) { reasons.Add("Hedef karşılanmadı"); }
        if (lateBeyondGrace) { reasons.Add("Son tarih ve tolerans süresi geçtikten sonra tamamlandı"); }

        var mutation = new OccurrenceMutation(
            o.Id, breached ? OccurrenceStatus.Breached : OccurrenceStatus.Fulfilled, input.CompletedOn, tenant.Current?.UserId, now, input.ActualValue, met, Blank(input.Note), file,
            BreachedAt: breached && o.BreachedAt is null ? now : null, BreachReason: breached && o.BreachReason is null ? string.Join("; ", reasons.DefaultIfEmpty("İhlal")) : null);
        var title = $"{c.Title}: dönem {o.Sequence} ({o.DueDate:dd.MM.yyyy})";
        var terminal = c.Kind == CommitmentKind.OneTime;
        var commit = await CommitAsync(
            record, [ContractsModule.Permissions.CommitmentsComplete], breached ? ContractEventKinds.CommitmentBreached : ContractEventKinds.CommitmentCompleted, title,
            new CommitmentMutation(terminal ? CommitmentStatus.Completed : null, [mutation], [new CommitmentHistory(breached ? ContractEventKinds.CommitmentBreached : ContractEventKinds.CommitmentCompleted, title)], terminal ? null : today),
            cancellationToken);
        if (!commit.IsSuccess)
        {
            return RecordResult<OccurrenceView>.Fail(commit.Error!.Code, commit.Error.Message, commit.Error.FieldErrors);
        }

        if (breached)
        {
            await EnsureFollowUpsAsync(commitmentId, ContractsModule.Permissions.CommitmentsComplete, cancellationToken);
        }

        return await ViewOfAsync(commitmentId, occurrenceId, today, cancellationToken);
    }

    public async Task<RecordResult<OccurrenceView>> WaiveAsync(Guid commitmentId, Guid occurrenceId, string reason, CancellationToken cancellationToken)
    {
        var (state, error) = await LoadAsync<OccurrenceView>(commitmentId, occurrenceId, ContractsModule.Permissions.CommitmentsWaive, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        var (record, c, o, _) = state;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<OccurrenceView>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (!CommitmentTracker.IsCompletable(o.Status, o.CompletedOn))
        {
            return Conflict<OccurrenceView>("Tamamlanmış, muaf tutulmuş ya da iptal edilmiş gerçekleşme için muafiyet verilemez.");
        }

        var title = $"{c.Title}: dönem {o.Sequence} ({o.DueDate:dd.MM.yyyy}) — {reason.Trim()}";
        var commit = await CommitAsync(
            record, [ContractsModule.Permissions.CommitmentsWaive], ContractEventKinds.CommitmentWaived, title,
            new CommitmentMutation(c.Kind == CommitmentKind.OneTime ? CommitmentStatus.Completed : null,
                [new OccurrenceMutation(o.Id, OccurrenceStatus.Waived, WaivedBy: tenant.Current?.UserId, WaiveReason: reason.Trim(), RecordedAt: time.GetUtcNow())],
                [new CommitmentHistory(ContractEventKinds.CommitmentWaived, title)], c.Kind == CommitmentKind.OneTime ? null : await TodayAsync(cancellationToken)),
            cancellationToken);
        return commit.IsSuccess
            ? await ViewOfAsync(commitmentId, occurrenceId, await TodayAsync(cancellationToken), cancellationToken)
            : RecordResult<OccurrenceView>.Fail(commit.Error!.Code, commit.Error.Message, commit.Error.FieldErrors);
    }

    public async Task<RecordResult<RecordData>> CancelAsync(Guid commitmentId, string reason, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, commitmentId), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.CommitmentsWrite))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.CommitmentsWrite}' izni gerekir.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        var c = await reader.CommitmentAsync(commitmentId, cancellationToken);
        if (c is null || c.Status != CommitmentStatus.Active)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Yalnızca etkin taahhüt iptal edilir.");
        }

        var open = (await reader.OccurrencesOfAsync([commitmentId], cancellationToken)).Where(o => CommitmentTracker.IsCompletable(o.Status, o.CompletedOn)).ToList();
        var title = $"{c.Title} — {reason.Trim()}";
        return await CommitAsync(
            record.Value!, [ContractsModule.Permissions.CommitmentsWrite], ContractEventKinds.CommitmentCancelled, title,
            new CommitmentMutation(CommitmentStatus.Cancelled, [.. open.Select(o => new OccurrenceMutation(o.Id, OccurrenceStatus.Cancelled, Note: "Taahhüt iptal edildi."))],
                [new CommitmentHistory(ContractEventKinds.CommitmentCancelled, title)]),
            cancellationToken);
    }

    // ---- değerlendirme

    public async Task<RecordResult<CommitmentEvaluation>> EvaluateAsync(Guid? contractId, CancellationToken cancellationToken)
    {
        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.CommitmentsWrite))
        {
            return RecordResult<CommitmentEvaluation>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.CommitmentsWrite}' izni gerekir.");
        }

        var filters = new Dictionary<string, string> { ["status"] = CommitmentStatus.Active };
        if (contractId is { } id) { filters["contractId"] = id.ToString(); }
        var (ids, error) = await VisibleCommitmentIdsAsync(filters, cancellationToken);
        if (error is not null)
        {
            return RecordResult<CommitmentEvaluation>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var today = await TodayAsync(cancellationToken);
        var now = time.GetUtcNow();
        int breached = 0, escalated = 0, created = 0, unavailable = 0, extended = 0;
        foreach (var commitment in await reader.CommitmentsAsync(ids, cancellationToken))
        {
            var contract = await reader.ContractAsync(commitment.ContractId, cancellationToken);
            if (contract is null || contract.EffectiveStatus is EffectiveStatus.Cancelled or EffectiveStatus.Terminated)
            {
                continue;
            }

            var occurrences = await reader.OccurrencesOfAsync([commitment.Id], cancellationToken);
            var mutations = new List<OccurrenceMutation>();
            var history = new List<CommitmentHistory>();
            foreach (var o in occurrences)
            {
                if (CommitmentTracker.ShouldBreach(o.Status, o.DueDate, commitment.GraceDays, today))
                {
                    mutations.Add(new OccurrenceMutation(o.Id, OccurrenceStatus.Breached, BreachedAt: now, BreachReason: "Son tarih ve tolerans süresi geçti; yerine getirilmedi."));
                    history.Add(new CommitmentHistory(ContractEventKinds.CommitmentBreached, $"{commitment.Title}: dönem {o.Sequence} ({o.DueDate:dd.MM.yyyy}) yerine getirilmedi"));
                    breached++;
                }
                else if (CommitmentTracker.ShouldEscalate(o.Status, o.CompletedOn, o.BreachedAt, o.EscalatedAt, commitment.EscalateAfterDays, now))
                {
                    mutations.Add(new OccurrenceMutation(o.Id, EscalatedAt: now));
                    history.Add(new CommitmentHistory(ContractEventKinds.CommitmentEscalated, $"{commitment.Title}: dönem {o.Sequence} ({o.DueDate:dd.MM.yyyy}) eskale edildi"));
                    escalated++;
                }
            }

            var needsHorizon = commitment.Kind == CommitmentKind.Periodic && ExpectedSequences(commitment, contract, today) > occurrences.Count;
            if (mutations.Count > 0 || needsHorizon)
            {
                var record = await pipeline.GetAsync(new RecordRef(Key, commitment.Id), cancellationToken);
                if (!record.IsSuccess)
                {
                    continue;
                }

                var before = occurrences.Count;
                var commit = await CommitAsync(
                    record.Value!, [ContractsModule.Permissions.CommitmentsWrite], history.FirstOrDefault()?.Kind ?? ContractEventKinds.CommitmentEvaluated, null,
                    new CommitmentMutation(null, mutations, history, needsHorizon ? today : null), cancellationToken);
                if (!commit.IsSuccess)
                {
                    // Eşzamanlı değişiklik (sürüm çakışması): tur bu taahhüdü atlar, bir sonraki tur tekrar dener.
                    breached -= mutations.Count(m => m.Status == OccurrenceStatus.Breached);
                    escalated -= mutations.Count(m => m.EscalatedAt is not null);
                    continue;
                }

                if (needsHorizon)
                {
                    extended += Math.Max(0, ExpectedSequences(commitment, contract, today) - before);
                }
            }

            var (made, missing) = await EnsureFollowUpsAsync(commitment.Id, ContractsModule.Permissions.CommitmentsWrite, cancellationToken);
            created += made;
            unavailable += missing;
        }

        return RecordResult<CommitmentEvaluation>.Ok(new CommitmentEvaluation(breached, escalated, created, unavailable, extended));
    }

    /// <summary>
    /// İhlal ve eskalasyon takip aktivitelerini (henüz açılmamışsa) açar. Aktivite modülü yoksa "kullanılamıyor", açılamazsa "başarısız" işaretlenir (bir sonraki tur yeniden dener;
    /// en az bir kez garanti: aktivite açılıp işaret yazılamazsa mükerrer aktivite doğabilir). İhlal kaydı aktiviteden bağımsızdır.
    /// </summary>
    private async Task<(int Created, int Unavailable)> EnsureFollowUpsAsync(Guid commitmentId, string permission, CancellationToken cancellationToken)
    {
        var commitment = await reader.CommitmentAsync(commitmentId, cancellationToken);
        var record = await pipeline.GetAsync(new RecordRef(Key, commitmentId), cancellationToken);
        if (commitment is null || !record.IsSuccess)
        {
            return (0, 0);
        }

        var contract = await reader.ContractAsync(commitment.ContractId, cancellationToken);
        var occurrences = await reader.OccurrencesOfAsync([commitmentId], cancellationToken);
        var pending = occurrences.Where(o =>
            (o.Status == OccurrenceStatus.Breached && o.FollowUpActivityId is null && o.FollowUpStatus is FollowUpState.None or FollowUpState.Failed)
            || (o.EscalatedAt is not null && o.EscalationActivityId is null && commitment.EscalationUserId is not null)).ToList();
        if (pending.Count == 0 || contract is null)
        {
            return (0, 0);
        }

        var mutations = new List<OccurrenceMutation>();
        var created = 0;
        var unavailable = 0;
        foreach (var o in pending)
        {
            if (o.Status == OccurrenceStatus.Breached && o.FollowUpActivityId is null && o.FollowUpStatus is FollowUpState.None or FollowUpState.Failed)
            {
                var (status, activityId) = await CreateAsync(
                    $"Taahhüt ihlali: {commitment.Title} ({contract.Number})",
                    $"{contract.Title} sözleşmesi, dönem {o.Sequence}, son tarih {o.DueDate:dd.MM.yyyy}. {o.BreachReason}", o, commitment.ResponsibleUserId, contract, cancellationToken);
                mutations.Add(new OccurrenceMutation(o.Id, FollowUpStatus: status, FollowUpActivityId: activityId));
                created += status == FollowUpState.Created ? 1 : 0;
                unavailable += status == FollowUpState.Unavailable ? 1 : 0;
            }

            if (o.EscalatedAt is not null && o.EscalationActivityId is null && commitment.EscalationUserId is { } escalationUser)
            {
                var (status, activityId) = await CreateAsync(
                    $"Eskalasyon: {commitment.Title} ({contract.Number})",
                    $"{contract.Title} sözleşmesi, dönem {o.Sequence} ihlalden sonra hâlâ tamamlanmadı (son tarih {o.DueDate:dd.MM.yyyy}).", o, escalationUser, contract, cancellationToken);
                if (activityId is not null)
                {
                    mutations.Add(new OccurrenceMutation(o.Id, EscalationActivityId: activityId));
                    created++;
                }
            }
        }

        if (mutations.Count > 0)
        {
            var fresh = await pipeline.GetAsync(new RecordRef(Key, commitmentId), cancellationToken);
            if (fresh.IsSuccess)
            {
                await CommitAsync(fresh.Value!, [permission], ContractEventKinds.CommitmentEvaluated, null, new CommitmentMutation(null, Merge(mutations), []), cancellationToken);
            }
        }

        return (created, unavailable);
    }

    private async Task<(string Status, Guid? ActivityId)> CreateAsync(string subject, string description, ContractCommitmentOccurrence o, Guid? assignee, Contract contract, CancellationToken cancellationToken)
    {
        if (followUps is null)
        {
            return (FollowUpState.Unavailable, null);
        }

        var result = await followUps.CreateAsync(new FollowUpRequest(subject, description, o.DueDate, assignee, ContractsModule.Entities.Contract, contract.Id), cancellationToken);
        return result.IsCreated ? (FollowUpState.Created, result.ActivityId) : (result.Unavailable ? FollowUpState.Unavailable : FollowUpState.Failed, null);
    }

    /// <summary>Aynı gerçekleşme için ayrı ayrı üretilen değişiklikleri tek satırda birleştirir (aynı satır iki kez uygulanmaz).</summary>
    private static List<OccurrenceMutation> Merge(List<OccurrenceMutation> mutations) =>
        [.. mutations.GroupBy(m => m.OccurrenceId).Select(g => g.Aggregate((a, b) => a with
        {
            FollowUpStatus = b.FollowUpStatus ?? a.FollowUpStatus,
            FollowUpActivityId = b.FollowUpActivityId ?? a.FollowUpActivityId,
            EscalationActivityId = b.EscalationActivityId ?? a.EscalationActivityId,
        }))];

    // ---- yardımcılar

    private sealed record Loaded(RecordData Record, ContractCommitment Commitment, ContractCommitmentOccurrence Occurrence, IReadOnlyList<ContractCommitmentOccurrence> All)
    {
        public void Deconstruct(out RecordData record, out ContractCommitment commitment, out ContractCommitmentOccurrence occurrence, out IReadOnlyList<ContractCommitmentOccurrence> all) =>
            (record, commitment, occurrence, all) = (Record, Commitment, Occurrence, All);
    }

    private async Task<(Loaded? Loaded, RecordResult<T>? Error)> LoadAsync<T>(Guid commitmentId, Guid occurrenceId, string permission, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, commitmentId), cancellationToken);
        if (!record.IsSuccess)
        {
            return (null, RecordResult<T>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors));
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir."));
        }

        var commitment = await reader.CommitmentAsync(commitmentId, cancellationToken);
        if (commitment is null)
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Taahhüt bulunamadı."));
        }

        if (commitment.Status != CommitmentStatus.Active)
        {
            return (null, Conflict<T>("Tamamlanmış ya da iptal edilmiş taahhüdün gerçekleşmesi değiştirilemez."));
        }

        var occurrences = await reader.OccurrencesOfAsync([commitmentId], cancellationToken);
        var occurrence = occurrences.FirstOrDefault(o => o.Id == occurrenceId);
        return occurrence is null
            ? (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Gerçekleşme bulunamadı."))
            : (new Loaded(record.Value!, commitment, occurrence, occurrences), null);
    }

    private async Task<RecordResult<RecordData>> CommitAsync(
        RecordData record, IReadOnlyList<string> permissions, string eventKind, string? reason, CommitmentMutation mutation, CancellationToken cancellationToken)
    {
        using (gate.Open(permissions, eventKind, reason, commitmentEffect: (ctx, commitment, ct) => ContractCommitmentWriter.ApplyAsync(ctx, commitment, mutation, ct)))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, record.Id, new JsonObject(), record.Version) { Reason = $"contract.{eventKind}" }, cancellationToken);
        }
    }

    private async Task<(IReadOnlyCollection<Guid> Ids, RecordError? Error)> VisibleCommitmentIdsAsync(Dictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        for (var page = 1; page <= 10; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(Key, page, PageSize, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return ([], result.Error);
            }

            ids.AddRange(result.Value!.Items.Select(i => i.Id));
            if (result.Value.Items.Count < PageSize)
            {
                break;
            }
        }

        return (ids, null);
    }

    private async Task<List<OccurrenceView>> ViewsAsync(IReadOnlyCollection<Guid> commitmentIds, DateOnly today, CancellationToken cancellationToken)
    {
        if (commitmentIds.Count == 0)
        {
            return [];
        }

        var commitments = (await reader.CommitmentsAsync(commitmentIds, cancellationToken)).ToDictionary(c => c.Id);
        var occurrences = await reader.OccurrencesOfAsync(commitmentIds, cancellationToken);
        var files = await reader.CommitmentFilesAsync([.. occurrences.Select(o => o.Id)], cancellationToken);
        var names = await reader.ContractNamesAsync([.. commitments.Values.Select(c => c.ContractId).Distinct()], cancellationToken);
        var byCommitment = occurrences.GroupBy(o => o.CommitmentId).ToDictionary(g => g.Key, g => g.ToList());
        var dependencyCache = new Dictionary<Guid, List<ContractCommitmentOccurrence>>();
        var views = new List<OccurrenceView>();
        foreach (var o in occurrences)
        {
            var c = commitments[o.CommitmentId];
            DateOnly? blocked = null;
            if (c.DependsOnCommitmentId is { } dependency && CommitmentTracker.IsCompletable(o.Status, o.CompletedOn))
            {
                if (!dependencyCache.TryGetValue(dependency, out var dependencyOccurrences))
                {
                    dependencyCache[dependency] = dependencyOccurrences = byCommitment.TryGetValue(dependency, out var known) ? known : await reader.OccurrencesOfAsync([dependency], cancellationToken);
                }

                blocked = CommitmentTracker.DependencyBlock(o.DueDate, dependencyOccurrences.Select(x => (x.Status, x.CompletedOn, x.DueDate)));
            }

            views.Add(ToView(o, c, names.GetValueOrDefault(c.ContractId), files.FirstOrDefault(f => f.OccurrenceId == o.Id)?.FileName, blocked, today));
        }

        return views;
    }

    private async Task<RecordResult<OccurrenceView>> ViewOfAsync(Guid commitmentId, Guid occurrenceId, DateOnly today, CancellationToken cancellationToken)
    {
        var view = (await ViewsAsync([commitmentId], today, cancellationToken)).FirstOrDefault(v => v.Id == occurrenceId);
        return view is null ? RecordResult<OccurrenceView>.Fail(RecordErrorCode.NotFound, "Gerçekleşme bulunamadı.") : RecordResult<OccurrenceView>.Ok(view);
    }

    private static OccurrenceView ToView(ContractCommitmentOccurrence o, ContractCommitment c, (string Number, string Title) contract, string? fileName, DateOnly? blocked, DateOnly today)
    {
        var overdue = CommitmentTracker.IsOverdue(o.Status, o.CompletedOn, o.DueDate, today);
        return new OccurrenceView(
            o.Id, c.Id, c.ContractId, contract.Number ?? string.Empty, contract.Title ?? string.Empty, c.Title, c.Category, c.ResponsibleUserId, o.Sequence, o.DueDate, o.Status,
            overdue, overdue ? today.DayNumber - o.DueDate.DayNumber : 0, o.CompletedOn, o.CompletedBy, o.RecordedAt, o.ActualValue, o.MetTarget, c.MetricName, c.Unit, c.Direction, c.TargetValue,
            c.Tolerance, o.Note, o.EvidenceFileId, fileName, o.BreachedAt, o.BreachReason, o.FollowUpStatus, o.FollowUpActivityId, o.EscalatedAt, o.EscalationActivityId, o.WaivedBy, o.WaiveReason,
            blocked, c.Status == CommitmentStatus.Active && CommitmentTracker.IsCompletable(o.Status, o.CompletedOn) && blocked is null);
    }

    private async Task<DateOnly?> DependencyBlockAsync(ContractCommitment c, ContractCommitmentOccurrence o, CancellationToken cancellationToken) =>
        c.DependsOnCommitmentId is { } dependency
            ? CommitmentTracker.DependencyBlock(o.DueDate, (await reader.OccurrencesOfAsync([dependency], cancellationToken)).Select(x => (x.Status, x.CompletedOn, x.DueDate)))
            : null;

    private static int ExpectedSequences(ContractCommitment c, Contract contract, DateOnly today) =>
        CommitmentSchedule.DueDates(c.Kind, c.DueDate, c.Recurrence, c.RecurrenceEndDate ?? (contract.IsIndefinite ? null : contract.EndDate), today.AddMonths(ContractCommitmentWriter.HorizonMonths)).Count;

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var zoneId = (policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken)).TimeZoneId;
        return ContractPeriod.Today(time.GetUtcNow(), TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) ? zone : TimeZoneInfo.Utc);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static RecordResult<T> Conflict<T>(string message) =>
        RecordResult<T>.Fail(RecordErrorCode.Conflict, message, [new RecordFieldError("status", message)]);
}
