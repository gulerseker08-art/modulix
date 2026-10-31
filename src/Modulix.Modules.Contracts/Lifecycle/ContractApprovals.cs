using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Approvals;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Plandaki bir adımın görünümü: adlı onaylayıcılar (boşsa onay izni olan herkes), tamamlanma ve onaylayanlar.</summary>
public sealed record ContractApprovalStepView(int Step, IReadOnlyList<Guid> Approvers, bool Completed, IReadOnlyList<Guid> ApprovedBy, bool IsCurrent);

/// <summary>Verilmiş bir karar: kim, hangi adımda, kimler adına (vekâlet), ne karar verdi ve gerekçesi.</summary>
public sealed record ContractApprovalDecisionView(
    Guid Id, int VersionNo, int StepNo, Guid ActorId, IReadOnlyList<Guid> OnBehalfOf, string Decision, string? Reason, DateTimeOffset At);

/// <summary>
/// Bir sözleşmenin güncel sürümünün onay durumu: eşleşen kurallar (kural adı/sürümü ve neden; tutar içermez), adımlar, kararlar, şu anki adım ve
/// geçerli kullanıcının karar verip veremeyeceği (verememe nedeniyle birlikte). Plan onaya gönderimde dondurulur; kural sonradan değişse de değişmez.
/// </summary>
public sealed record ContractApprovalView(
    string Status,
    int VersionNo,
    IReadOnlyList<ContractApprovalMatch> Rules,
    string? Note,
    IReadOnlyList<ContractApprovalStepView> Steps,
    IReadOnlyList<ContractApprovalDecisionView> Decisions,
    int CurrentStep,
    bool CanDecide,
    bool ActingAsDelegate,
    string? CannotDecideReason,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DueAt,
    bool IsOverdue);

/// <summary>Onay iş listesi satırı: onay bekleyen bir sözleşme ve geçerli kullanıcının durumu (karar verebilir mi, vekil olarak mı).</summary>
public sealed record ContractApprovalInboxItem(
    Guid ContractId,
    string Number,
    string Title,
    string Type,
    int VersionNo,
    Guid? OwnerId,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    int CurrentStep,
    int StepCount,
    IReadOnlyList<Guid> WaitingFor,
    bool CanDecide,
    bool ActingAsDelegate,
    string? CannotDecideReason,
    IReadOnlyList<string> Rules);

/// <summary>
/// Sözleşme iç onay okuma servisi (ADR-0022, dilim 5): sözleşmenin onay planı/ilerlemesi ve onay iş listesi. Yazma (karar verme) iş akışı
/// eylemleriyle yapılır (<see cref="IContractWorkflow"/>); bu servis salt okunurdur. Görünürlük kayıt boru hattında (izin ve kapsam) doğrulanır.
/// </summary>
public interface IContractApprovals
{
    Task<RecordResult<ContractApprovalView>> GetAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>Onay bekleyen görünür sözleşmeler (hedef tarihi en yakın olan önce). <paramref name="onlyMine"/> doluysa yalnızca karar verebileceğiniz sözleşmeler.</summary>
    Task<RecordResult<IReadOnlyList<ContractApprovalInboxItem>>> InboxAsync(bool onlyMine, CancellationToken cancellationToken);
}

/// <summary>Bir sürümün güncel onay turunun hesaplanmış durumu (iç).</summary>
internal sealed record ContractApprovalState(
    ContractVersion? Version,
    ContractApprovalPlan Plan,
    IReadOnlyList<ContractApprovalDecision> Decisions,
    IReadOnlyList<ApprovalStepState> Steps,
    ApprovalStepState? Current,
    IReadOnlySet<Guid> Principals,
    bool ActingAsDelegate);

/// <summary>
/// Onay hesabı (iç): plan kurma, adım ilerlemesi ve karar denetimi. Adım ve vekâlet hesabı CRM ile ortaktır (<see cref="ApprovalFlow"/>).
/// Karar yetkisi: adlı adımda yalnızca adı geçen kişi ya da onun vekili; adsız adımda onay izni olan herkes. Aynı sürümde önceki bir adımda
/// karar vermiş kişi (ya da adına vekâlet edilen) sonraki adımda karar veremez (dört göz).
/// </summary>
internal sealed class ContractApprovalEngine(ContractReader reader, ITenantContextAccessor tenant, TimeProvider time)
{
    public Guid? CurrentUserId => tenant.Current?.UserId;

    public async Task<ContractApprovalFacts> FactsAsync(Contract c, CancellationToken cancellationToken) => new(
        c.Type,
        c.Currency,
        c.GrossTotal > 0 ? c.GrossTotal : c.ContractValue ?? 0m,
        await reader.MaxLineDiscountAsync(c.Id, cancellationToken),
        ContractApprovalEvaluator.TermMonths(c.StartDate, c.EndDate, c.IsIndefinite),
        c.IsIndefinite,
        c.HasNonStandardTerms,
        c.RiskLevel);

    /// <summary>Onaya gönderimde: etkin kuralları sözleşme özetiyle eşleştirip planı kurar.</summary>
    public async Task<ContractApprovalPlan> BuildPlanAsync(Contract c, CancellationToken cancellationToken)
    {
        var matches = ContractApprovalEvaluator.Match(await reader.ActiveRulesAsync(cancellationToken), await FactsAsync(c, cancellationToken));
        return new ContractApprovalPlan(matches, ContractApprovalEvaluator.Note(matches));
    }

    public async Task<ContractApprovalState> StateAsync(Contract c, CancellationToken cancellationToken)
    {
        var version = await reader.VersionAsync(c.Id, c.CurrentVersionNo, cancellationToken);
        var decisions = await reader.DecisionsAsync(c.Id, cancellationToken);
        var delegations = await reader.ActiveDelegationsAsync(time.GetUtcNow(), cancellationToken);
        return Compute(version, c.CurrentVersionNo, decisions, delegations, CurrentUserId);
    }

    /// <summary>Saf hesap: güncel turun kararlarından adımları ve geçerli kullanıcının "adına" saydığı kişileri çıkarır.</summary>
    public static ContractApprovalState Compute(
        ContractVersion? version, int versionNo, IEnumerable<ContractApprovalDecision> decisions, IReadOnlyList<ContractApprovalDelegation> delegations, Guid? actor)
    {
        var plan = ContractApprovalPlans.Deserialize(version?.ApprovalPlan);
        var round = version?.ApprovalRound ?? 0;
        var current = decisions.Where(d => d.VersionNo == versionNo && d.Round == round).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToList();
        var steps = ApprovalFlow.Steps(plan.Specs(), current.Where(d => d.Decision == ApprovalDecisionKind.Approved).Select(Given));
        var open = ApprovalFlow.Current(steps);

        var principals = new HashSet<Guid>();
        if (actor is { } id)
        {
            principals.Add(id);
            foreach (var d in delegations.Where(d => d.ToUserId == id))
            {
                principals.Add(d.FromUserId);
            }
        }

        var asDelegate = actor is { } me && open is { Approvers.Count: > 0 } && !open.Approvers.Contains(me) && open.Approvers.Any(a => principals.Contains(a));
        return new ContractApprovalState(version, plan, current, steps, open, principals, asDelegate);
    }

    public static ApprovalGiven Given(ContractApprovalDecision d)
    {
        var onBehalf = new HashSet<Guid>(d.OnBehalfIds()) { d.ActorId };
        return new ApprovalGiven(d.ActorId, onBehalf);
    }

    /// <summary>Geçerli kullanıcı şu anki adımda karar verebilir mi? Veremezse nedeni (kullanıcıya gösterilir).</summary>
    public static (bool Allowed, string? Reason) CanDecide(ContractApprovalState state)
    {
        if (state.Current is not { } step)
        {
            return (false, "Bu sürümün bütün onay adımları tamamlandı.");
        }

        if (!ApprovalFlow.CanDecide(step, state.Principals))
        {
            return (false, "Bu onay adımı belirli onaylayıcılara atanmıştır; sizin adınıza karar verilemez.");
        }

        if (step.Approvers.Count > 0 && !step.Approvers.Any(a => state.Principals.Contains(a) && !step.ApprovedBy.Contains(a)))
        {
            return (false, "Bu adım için onayınız zaten verildi; diğer onaylayıcılar bekleniyor.");
        }

        var earlier = new HashSet<Guid>();
        foreach (var d in state.Decisions.Where(d => d.StepNo < step.Step && d.Decision == ApprovalDecisionKind.Approved))
        {
            earlier.Add(d.ActorId);
            earlier.UnionWith(d.OnBehalfIds());
        }

        return earlier.Overlaps(state.Principals)
            ? (false, "Bu sürümün önceki bir adımında karar verdiniz; sonraki adımı başka bir kişi onaylamalıdır.")
            : (true, null);
    }

    /// <summary>Verilecek onayla bütün adımlar tamamlanır mı?</summary>
    public static bool CompletesAll(ContractApprovalState state, Guid actor) =>
        ApprovalFlow.CompletesAll(
            state.Plan.Specs(),
            state.Decisions.Where(d => d.Decision == ApprovalDecisionKind.Approved).Select(Given),
            new ApprovalGiven(actor, state.Principals));

    public DateTimeOffset Now => time.GetUtcNow();
}

internal sealed class ContractApprovals(
    IRecordPipeline pipeline,
    ICurrentAccess access,
    ContractReader reader,
    ContractApprovalEngine engine,
    ITenantContextAccessor tenant,
    TimeProvider time,
    IContractPolicyProvider? policy = null) : IContractApprovals
{
    private const string Key = ContractsModule.Entities.Contract;

    public async Task<RecordResult<ContractApprovalView>> GetAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, contractId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<ContractApprovalView>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var contract = await reader.ContractAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return RecordResult<ContractApprovalView>.Fail(RecordErrorCode.NotFound, "Sözleşme bulunamadı.");
        }

        var state = await engine.StateAsync(contract, cancellationToken);
        var pending = contract.ApprovalStatus == ApprovalStatus.Pending;
        var (canDecide, reason) = pending ? ContractApprovalEngine.CanDecide(state) : (false, "Onay bekleyen sürüm yok.");
        if (canDecide && !await access.HasPermissionAsync(ContractsModule.Permissions.ContractsApprove, cancellationToken))
        {
            (canDecide, reason) = (false, "Onay izniniz yok.");
        }

        if (canDecide && await SelfBlockedAsync(contractId, cancellationToken))
        {
            (canDecide, reason) = (false, "Kendi onaya gönderdiğiniz sözleşmeyi onaylayamazsınız.");
        }

        var submissions = await reader.SubmissionsAsync([contractId], cancellationToken);
        submissions.TryGetValue(contractId, out var submitted);
        var dueAt = state.Version?.ApprovalDueAt;
        var view = new ContractApprovalView(
            contract.ApprovalStatus,
            contract.CurrentVersionNo,
            state.Plan.Matches,
            state.Plan.Note,
            [.. state.Steps.Select(s => new ContractApprovalStepView(s.Step, s.Approvers, s.Completed, s.ApprovedBy, state.Current is { } c && c.Step == s.Step))],
            [.. state.Decisions.Select(d => new ContractApprovalDecisionView(d.Id, d.VersionNo, d.StepNo, d.ActorId, d.OnBehalfIds(), d.Decision, d.Reason, d.CreatedAt))],
            state.Current?.Step ?? 0,
            canDecide,
            canDecide && state.ActingAsDelegate,
            canDecide ? null : reason,
            submitted.ActorId,
            submissions.ContainsKey(contractId) ? submitted.At : null,
            dueAt,
            pending && dueAt is { } due && due < time.GetUtcNow());
        return RecordResult<ContractApprovalView>.Ok(view);
    }

    public async Task<RecordResult<IReadOnlyList<ContractApprovalInboxItem>>> InboxAsync(bool onlyMine, CancellationToken cancellationToken)
    {
        var page = await pipeline.QueryAsync(
            new RecordQuery(Key, 1, RecordQuery.MaxPageSize, null, new Dictionary<string, string> { ["approvalStatus"] = ApprovalStatus.Pending }), cancellationToken);
        if (!page.IsSuccess)
        {
            return RecordResult<IReadOnlyList<ContractApprovalInboxItem>>.Fail(page.Error!.Code, page.Error.Message, page.Error.FieldErrors);
        }

        var items = new List<ContractApprovalInboxItem>();
        var rows = page.Value!.Items;
        if (rows.Count == 0)
        {
            return RecordResult<IReadOnlyList<ContractApprovalInboxItem>>.Ok(items);
        }

        var ids = rows.Select(r => r.Id).ToList();
        var versions = await reader.VersionsForAsync(ids, cancellationToken);
        var decisions = await reader.DecisionsAsync(ids, cancellationToken);
        var delegations = await reader.ActiveDelegationsAsync(time.GetUtcNow(), cancellationToken);
        var submissions = await reader.SubmissionsAsync(ids, cancellationToken);
        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var me = tenant.Current?.UserId;
        var canApprove = await access.HasPermissionAsync(ContractsModule.Permissions.ContractsApprove, cancellationToken);
        var now = time.GetUtcNow();

        foreach (var row in rows)
        {
            var versionNo = (int)(Number(row, "currentVersionNo") ?? 1);
            var version = versions.FirstOrDefault(v => v.ContractId == row.Id && v.VersionNo == versionNo);
            var state = ContractApprovalEngine.Compute(version, versionNo, decisions.Where(d => d.ContractId == row.Id), delegations, me);
            submissions.TryGetValue(row.Id, out var submitted);
            var (allowed, reason) = ContractApprovalEngine.CanDecide(state);
            if (allowed && !canApprove)
            {
                (allowed, reason) = (false, "Onay izniniz yok.");
            }

            if (allowed && !settings.AllowSelfApproval && submitted.ActorId is { } submitter && submitter == me)
            {
                (allowed, reason) = (false, "Kendi onaya gönderdiğiniz sözleşmeyi onaylayamazsınız.");
            }

            if (onlyMine && !allowed)
            {
                continue;
            }

            var dueAt = version?.ApprovalDueAt;
            var waiting = state.Current is { } current
                ? current.Approvers.Where(a => !current.ApprovedBy.Contains(a)).ToList()
                : [];
            items.Add(new ContractApprovalInboxItem(
                row.Id,
                Text(row, "number") ?? string.Empty,
                Text(row, "title") ?? string.Empty,
                Text(row, "type") ?? string.Empty,
                versionNo,
                Guid.TryParse(Text(row, ContractsRecordStoreKeys.Owner), out var owner) ? owner : null,
                submitted.ActorId,
                submissions.ContainsKey(row.Id) ? submitted.At : null,
                dueAt,
                dueAt is { } due && due < now,
                state.Current?.Step ?? 0,
                state.Steps.Count,
                waiting,
                allowed,
                allowed && state.ActingAsDelegate,
                allowed ? null : reason,
                [.. state.Plan.Matches.Select(m => m.Name).Distinct()]));
        }

        // Gecikmiş ve hedef tarihi yakın olanlar önce; hedefi olmayanlar sonda.
        var ordered = items.OrderBy(i => i.DueAt is null ? 1 : 0).ThenBy(i => i.DueAt).ThenBy(i => i.Number, StringComparer.Ordinal).ToList();
        return RecordResult<IReadOnlyList<ContractApprovalInboxItem>>.Ok(ordered);
    }

    private async Task<bool> SelfBlockedAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        return !settings.AllowSelfApproval && await reader.LastSubmitterAsync(contractId, cancellationToken) is { } submitter && submitter == tenant.Current?.UserId;
    }

    private static string? Text(RecordData row, string field) =>
        row.Values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Number(RecordData row, string field) =>
        row.Values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}

/// <summary>Kayıt verisindeki alan adları (iş listesi okuması).</summary>
internal static class ContractsRecordStoreKeys
{
    public const string Owner = Modulix.Core.Contracts.Modules.OwnedRecordType.OwnerField;
}
