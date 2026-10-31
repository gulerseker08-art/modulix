using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Tarihçe satırı (API görünümü).</summary>
public sealed record ContractHistoryItem(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. Yetkisi olmayan eylem listede hiç yer almaz.</summary>
public sealed record ContractActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <summary>Dondurulmuş sürüm (API görünümü); <see cref="Snapshot"/> izne göre maskelenmiş olabilir.</summary>
public sealed record ContractVersionView(Guid Id, int VersionNo, string Kind, string Status, string Sha256, string? Reason, Guid? CreatedBy, DateTimeOffset FrozenAt, ContractSnapshot? Snapshot);

/// <summary>Tenant ayarlarından gelen sözleşme politikası; sağlayıcı yoksa varsayılan.</summary>
public sealed record ContractPolicySettings(bool AllowSelfApproval)
{
    public static ContractPolicySettings Default { get; } = new(false);
}

/// <summary>Ayarları okur (API host'u tenant ayar servisiyle uygular; modül Core'un ayar servisini doğrudan görmez).</summary>
public interface IContractPolicyProvider
{
    Task<ContractPolicySettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sözleşme iş akışı (ADR-0021, dilim 2). Tek yazma yolu: iş akışı kapısı eylemin izniyle açılır ve kayıt boru hattı üzerinden güncelleme
/// yapılır; kapsam, hassas alan, audit, olay ve sürüm denetimi (409) depoda uygulanır. Her eylem kaydın sürümünü ister. Ön koşullar
/// <see cref="ContractTransitions"/> ile arayüzle ortaktır. Sürüm dondurma geçişle aynı işlemde yapılır (kapı etkisi).
/// </summary>
public interface IContractWorkflow
{
    Task<RecordResult<IReadOnlyList<ContractHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<ContractActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Eylemi uygular; bilinmeyen eylem 422. Gerekçe isteyen eylemde gerekçe zorunludur.</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<ContractVersionView>>> VersionsAsync(Guid id, CancellationToken cancellationToken);

    Task<RecordResult<ContractVersionView>> VersionAsync(Guid id, int versionNo, CancellationToken cancellationToken);

    /// <summary>İki dondurulmuş sürümü karşılaştırır; <paramref name="withVersionNo"/> 0 ise güncel taslakla karşılaştırır.</summary>
    Task<RecordResult<SnapshotDiff>> CompareAsync(Guid id, int versionNo, int withVersionNo, CancellationToken cancellationToken);
}

internal sealed class ContractWorkflow(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    ITenantContextAccessor tenant,
    ContractsStoreContext storeContext,
    IContractPolicyProvider? policy = null) : IContractWorkflow
{
    private const string Key = ContractsModule.Entities.Contract;

    private static readonly IReadOnlyDictionary<string, string> PermissionOf = new Dictionary<string, string>
    {
        [ContractAction.SendToReview] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.MarkReady] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.ReopenDraft] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.SubmitForApproval] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.WithdrawApproval] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.Approve] = ContractsModule.Permissions.ContractsApprove,
        [ContractAction.RejectApproval] = ContractsModule.Permissions.ContractsApprove,
        [ContractAction.Cancel] = ContractsModule.Permissions.ContractsWrite,
        [ContractAction.Revise] = ContractsModule.Permissions.ContractsWrite,
    };

    public async Task<RecordResult<IReadOnlyList<ContractHistoryItem>>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!contract.IsSuccess)
        {
            return Fail<IReadOnlyList<ContractHistoryItem>>(contract.Error!);
        }

        var events = await reader.HistoryAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<ContractHistoryItem>>.Ok(
            [.. events.Select(e => new ContractHistoryItem(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.Reason))]);
    }

    public async Task<RecordResult<IReadOnlyList<ContractActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return Fail<IReadOnlyList<ContractActionState>>(record.Error!);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        var state = StateOf(record.Value!);
        var list = new List<ContractActionState>();
        foreach (var action in ContractAction.All.Where(a => granted.Contains(PermissionOf[a])))
        {
            var rule = ContractTransitions.Can(action, state);
            if (rule.Allowed && action is ContractAction.Approve or ContractAction.RejectApproval && await SelfApprovalBlockedAsync(id, cancellationToken))
            {
                rule = ContractRuleResult.Deny("self-approval", "Kendi onaya gönderdiğiniz sözleşmeyi onaylayamazsınız.");
            }

            list.Add(new ContractActionState(action, rule.Allowed, rule.Code, rule.Message, ContractTransitions.RequiresReason(action)));
        }

        return RecordResult<IReadOnlyList<ContractActionState>>.Ok(list);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (!PermissionOf.TryGetValue(action, out var permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (ContractTransitions.RequiresReason(action) && text is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        var contract = loaded.Value!;
        var rule = ContractTransitions.Can(action, StateOf(contract));
        if (!rule.Allowed)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        if (action is ContractAction.Approve or ContractAction.RejectApproval && await SelfApprovalBlockedAsync(id, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Kendi onaya gönderdiğiniz sözleşmeyi onaylayamazsınız.",
                [new RecordFieldError("approvalStatus", "Kendi onaya gönderdiğiniz sözleşmeyi onaylayamazsınız.")]);
        }

        var approval = Text(contract, "approvalStatus") ?? ApprovalStatus.NotRequired;
        (JsonObject Values, string EventKind, Func<ContractsStoreContext, Contract, CancellationToken, Task>? Effect) step = action switch
        {
            ContractAction.SendToReview => (Set(("preparationStatus", PreparationStatus.InReview)), ContractEventKinds.SentToReview, null),
            ContractAction.MarkReady => (Set(("preparationStatus", PreparationStatus.ReadyToPublish)), ContractEventKinds.MarkedReady, null),
            ContractAction.ReopenDraft => (
                Set(("preparationStatus", PreparationStatus.Draft), ("approvalStatus", ApprovalStatus.NotRequired), ("statusReason", text)),
                approval is ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.Withdrawn ? ContractEventKinds.ApprovalInvalidated : ContractEventKinds.ReopenedDraft,
                Effect(async (ctx, c, ct) =>
                {
                    // Dondurulmuş içerik varsa yeniden düzenleme yeni bir revizyondur; eski sürüm geçersiz kalır.
                    if (await reader.VersionAsync(c.Id, c.CurrentVersionNo, ct) is not null)
                    {
                        await ContractVersions.OpenNextVersionAsync(ctx, c, ct);
                    }
                })),
            ContractAction.SubmitForApproval => (
                Set(("approvalStatus", ApprovalStatus.Pending), ("statusReason", text)),
                ContractEventKinds.SubmittedForApproval,
                Effect((ctx, c, ct) => ContractVersions.FreezeAsync(ctx, c, VersionKinds.Approval, text, ct))),
            ContractAction.WithdrawApproval => (
                Set(("approvalStatus", ApprovalStatus.Withdrawn), ("statusReason", text)),
                ContractEventKinds.ApprovalWithdrawn,
                Effect((ctx, c, ct) => ContractVersions.SetStatusAsync(ctx, c, VersionStatus.Withdrawn, ct))),
            ContractAction.Approve => (
                Set(("approvalStatus", ApprovalStatus.Approved), ("statusReason", text)),
                ContractEventKinds.Approved,
                Effect((ctx, c, ct) => ContractVersions.SetStatusAsync(ctx, c, VersionStatus.Approved, ct))),
            ContractAction.RejectApproval => (
                Set(("approvalStatus", ApprovalStatus.Rejected), ("statusReason", text)),
                ContractEventKinds.ApprovalRejected,
                Effect((ctx, c, ct) => ContractVersions.SetStatusAsync(ctx, c, VersionStatus.Rejected, ct))),
            ContractAction.Cancel => (
                Set(("effectiveStatus", EffectiveStatus.Cancelled), ("statusReason", text),
                    ("signatureStatus", Text(contract, "signatureStatus") == SignatureStatus.Pending ? SignatureStatus.Cancelled : Text(contract, "signatureStatus")),
                    ("approvalStatus", approval == ApprovalStatus.Pending ? ApprovalStatus.Withdrawn : approval)),
                ContractEventKinds.Cancelled,
                null),
            ContractAction.Revise => (
                Set(("preparationStatus", PreparationStatus.Draft), ("approvalStatus", ApprovalStatus.NotRequired), ("signatureStatus", SignatureStatus.NotSent), ("statusReason", text)),
                ContractEventKinds.Revised,
                Effect((ctx, c, ct) => ContractVersions.OpenNextVersionAsync(ctx, c, ct))),
            _ => throw new InvalidOperationException(action),
        };

        using (gate.Open([permission], step.EventKind, text, step.Effect))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, step.Values, version) { Reason = $"contract.{step.EventKind}" }, cancellationToken);
        }
    }

    public async Task<RecordResult<IReadOnlyList<ContractVersionView>>> VersionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!contract.IsSuccess)
        {
            return Fail<IReadOnlyList<ContractVersionView>>(contract.Error!);
        }

        var versions = await reader.VersionsAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<ContractVersionView>>.Ok([.. versions.Select(v => View(v, snapshot: null))]);
    }

    public async Task<RecordResult<ContractVersionView>> VersionAsync(Guid id, int versionNo, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!contract.IsSuccess)
        {
            return Fail<ContractVersionView>(contract.Error!);
        }

        var version = await reader.VersionAsync(id, versionNo, cancellationToken);
        if (version is null)
        {
            return RecordResult<ContractVersionView>.Fail(RecordErrorCode.NotFound, "Sürüm bulunamadı.");
        }

        var snapshot = ContractSnapshots.Deserialize(version.Snapshot);
        return RecordResult<ContractVersionView>.Ok(View(version, await CanSeeFinancialsAsync(cancellationToken) ? snapshot : snapshot?.WithoutFinancials()));
    }

    public async Task<RecordResult<SnapshotDiff>> CompareAsync(Guid id, int versionNo, int withVersionNo, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!contract.IsSuccess)
        {
            return Fail<SnapshotDiff>(contract.Error!);
        }

        var before = await SnapshotAsync(id, versionNo, cancellationToken);
        var after = await SnapshotAsync(id, withVersionNo, cancellationToken);
        if (before is null || after is null)
        {
            return RecordResult<SnapshotDiff>.Fail(RecordErrorCode.NotFound, "Karşılaştırılacak sürüm bulunamadı.");
        }

        return RecordResult<SnapshotDiff>.Ok(SnapshotComparer.Compare(before, after, await CanSeeFinancialsAsync(cancellationToken)));
    }

    // ---- yardımcılar

    /// <summary>Sürüm 0: güncel (kaydedilmiş) taslağın anlık görüntüsü; aksi halde dondurulmuş sürüm.</summary>
    private async Task<ContractSnapshot?> SnapshotAsync(Guid id, int versionNo, CancellationToken cancellationToken)
    {
        if (versionNo == 0)
        {
            return await reader.ContractAsync(id, cancellationToken) is { } current ? await ContractVersions.BuildSnapshotAsync(storeContext, current, cancellationToken) : null;
        }

        return await reader.VersionAsync(id, versionNo, cancellationToken) is { } version ? ContractSnapshots.Deserialize(version.Snapshot) : null;
    }

    private Task<bool> CanSeeFinancialsAsync(CancellationToken cancellationToken) =>
        access.HasPermissionAsync(ContractsModule.Permissions.FinancialsRead, cancellationToken);

    private async Task<bool> SelfApprovalBlockedAsync(Guid id, CancellationToken cancellationToken)
    {
        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        return !settings.AllowSelfApproval && await reader.LastSubmitterAsync(id, cancellationToken) is { } submitter && submitter == tenant.Current?.UserId;
    }

    private static ContractVersionView View(ContractVersion v, ContractSnapshot? snapshot) =>
        new(v.Id, v.VersionNo, v.Kind, v.Status, v.Sha256, v.Reason, v.CreatedBy, v.CreatedAt, snapshot);

    private static ContractState StateOf(RecordData c) => new(
        Text(c, "preparationStatus") ?? PreparationStatus.Draft,
        Text(c, "approvalStatus") ?? ApprovalStatus.NotRequired,
        Text(c, "signatureStatus") ?? SignatureStatus.NotSent,
        Text(c, "effectiveStatus") ?? EffectiveStatus.NotInForce,
        HasStartDate: Text(c, "startDate") is not null,
        HasEndOrIndefinite: Text(c, "endDate") is not null || c.Values["isIndefinite"]?.GetValue<bool>() == true,
        HasParty: Text(c, "partyId") is not null);

    private static string? Text(RecordData record, string field) =>
        record.Values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Set(params (string Field, string? Value)[] fields)
    {
        var values = new JsonObject();
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return values;
    }

    private static Func<ContractsStoreContext, Contract, CancellationToken, Task>? Effect(Func<ContractsStoreContext, Contract, CancellationToken, Task> effect) => effect;

    private static RecordResult<T> Fail<T>(RecordError error) => RecordResult<T>.Fail(error.Code, error.Message, error.FieldErrors);
}
