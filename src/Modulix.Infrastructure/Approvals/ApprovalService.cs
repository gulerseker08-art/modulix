using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Approvals;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Approvals;

/// <summary>
/// Genel onay (ADR-0027 Dilim 4a). Tanım ve talepler tenant verisidir (query filter + RLS). Dört göz kuralı sunucudadır: talep sahibi kendi
/// talebine karar veremez. Onaycı kişi ya da rol olduğu için izin tek başına yetmez; karar veren etkin adımın onaycısı olmalıdır.
/// </summary>
internal sealed class ApprovalService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    IEmailQueue emails,
    INotificationInbox inbox,
    INotificationRecipients recipients,
    TimeProvider time) : IApprovalService
{
    private const int TrendDays = 14;
    private const int SummaryDays = 30;
    private const int EscalationBatch = 200;
    private const string EscalationSource = "core.approval-escalation";

    // Kişinin karar verebildiği kimlikler; Users kendisi ve o an vekili olduğu kişilerdir.
    private sealed record Me(List<Guid> Users, List<Guid> Roles);

    public async Task<IReadOnlyList<ApprovalDefinition>> ListDefinitionsAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        IQueryable<ApprovalDefinition> definitions = db.ApprovalDefinitions.AsNoTracking();
        if (activeOnly)
        {
            definitions = definitions.Where(d => d.IsActive);
        }

        return await definitions.OrderBy(d => d.Name).ToListAsync(cancellationToken);
    }

    public async Task<ApprovalDefinition> CreateDefinitionAsync(ApprovalDefinitionInput input, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        await RequireApproversAsync(input.Steps, cancellationToken);
        ApprovalDefinition definition = Guarded(() =>
            ApprovalDefinition.Create(input.Name, input.Description, input.Currency, input.Steps, tenant.UserId, time.GetUtcNow()));
        db.ApprovalDefinitions.Add(definition);
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalDefinition, definition.Id, AuditAction.Created, null, Snapshot(definition));
        await SaveAsync(cancellationToken);
        return definition;
    }

    public async Task<ApprovalDefinition?> UpdateDefinitionAsync(Guid id, ApprovalDefinitionInput input, long? expectedVersion, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalDefinition? definition = await db.ApprovalDefinitions.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        if (expectedVersion is { } expected && expected != definition.Version)
        {
            throw new ApprovalConflictException("Süreç başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }

        await RequireApproversAsync(input.Steps, cancellationToken);
        JsonObject before = Snapshot(definition);
        Guarded(() =>
        {
            definition.Update(input.Name, input.Description, input.Currency, input.Steps, tenant.UserId, time.GetUtcNow());
            return definition;
        });
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalDefinition, id, AuditAction.Updated, before, Snapshot(definition));
        await SaveAsync(cancellationToken);
        return definition;
    }

    public async Task<ApprovalDefinition?> SetDefinitionActiveAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalDefinition? definition = await db.ApprovalDefinitions.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        JsonObject before = Snapshot(definition);
        definition.SetActive(active, tenant.UserId, time.GetUtcNow());
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalDefinition, id, AuditAction.Updated, before, Snapshot(definition));
        await SaveAsync(cancellationToken);
        return definition;
    }

    public async Task<ApproverOptions> ApproverOptionsAsync(CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        List<ApproverOption> people = await (
            from member in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.TenantId == tenant.TenantId && member.Status == MembershipStatus.Active
            orderby user.DisplayName
            select new ApproverOption(user.Id, user.DisplayName)).ToListAsync(cancellationToken);
        List<ApproverOption> roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new ApproverOption(r.Id, r.Name)).ToListAsync(cancellationToken);
        return new ApproverOptions(people, roles);
    }

    public async Task<ApprovalOverview> OverviewAsync(bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        Me me = await MeAsync(tenant, cancellationToken);
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset summaryStart = now.AddDays(-SummaryDays);
        IQueryable<ApprovalRequest> visible = Visible(tenant, me, seeAll);
        IQueryable<ApprovalRequestStep> activeSteps =
            from step in db.ApprovalRequestSteps.AsNoTracking()
            join request in visible on step.RequestId equals request.Id
            where step.Status == ApprovalStepStatus.Active && request.Status == ApprovalRequestStatus.Pending
            select step;

        int waiting = await WaitingForMe(tenant, me).CountAsync(cancellationToken);
        int overdue = await activeSteps.Where(s => s.DueAt != null && s.DueAt < now).Select(s => s.RequestId).Distinct().CountAsync(cancellationToken);
        int myOpen = await db.ApprovalRequests.AsNoTracking()
            .CountAsync(r => r.RequesterId == tenant.UserId && r.Status == ApprovalRequestStatus.Pending, cancellationToken);

        var completed = await visible
            .Where(r => r.CompletedAt >= summaryStart && (r.Status == ApprovalRequestStatus.Approved || r.Status == ApprovalRequestStatus.Rejected))
            .Select(r => new { r.Status, r.SubmittedAt, CompletedAt = r.CompletedAt!.Value })
            .ToListAsync(cancellationToken);
        decimal? averageHours = completed.Count == 0
            ? null
            : Math.Round((decimal)completed.Average(c => (c.CompletedAt - c.SubmittedAt).TotalHours), 1);

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        List<ApprovalDayCount> trend = Enumerable.Range(0, TrendDays)
            .Select(i => today.AddDays(i - TrendDays + 1))
            .Select(day =>
            {
                var ofDay = completed.Where(c => DateOnly.FromDateTime(c.CompletedAt.UtcDateTime) == day).ToList();
                return new ApprovalDayCount(
                    day, ofDay.Count(c => c.Status == ApprovalRequestStatus.Approved), ofDay.Count(c => c.Status == ApprovalRequestStatus.Rejected));
            }).ToList();

        var openCounts = await visible.Where(r => r.Status == ApprovalRequestStatus.Pending)
            .GroupBy(r => r.DefinitionName)
            .Select(g => new { Name = g.Key, Open = g.Count() })
            .OrderByDescending(p => p.Open).Take(6)
            .ToListAsync(cancellationToken);
        List<ApprovalProcessCount> byProcess = [.. openCounts.Select(p => new ApprovalProcessCount(p.Name, p.Open))];

        int orphaned = 0;
        if (seeAll)
        {
            List<ApprovalRequestStep> open = await activeSteps.ToListAsync(cancellationToken);
            HashSet<Guid> covered = await CoveredAsync(tenant, open, cancellationToken);
            orphaned = open.Where(s => !IsCovered(s, covered)).Select(s => s.RequestId).Distinct().Count();
        }

        return new ApprovalOverview(
            waiting, overdue, myOpen, orphaned,
            completed.Count(c => c.Status == ApprovalRequestStatus.Approved),
            completed.Count(c => c.Status == ApprovalRequestStatus.Rejected),
            averageHours, trend, byProcess);
    }

    public async Task<ApprovalRequestPage> ListRequestsAsync(ApprovalRequestQuery query, bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        Me me = await MeAsync(tenant, cancellationToken);
        int page = Math.Max(1, query.Page);
        int pageSize = Math.Clamp(query.PageSize, 1, ApprovalRequestQuery.MaxPageSize);
        IQueryable<ApprovalRequest> requests = query.View switch
        {
            ApprovalRequestView.Waiting => WaitingForMe(tenant, me),
            ApprovalRequestView.Mine => db.ApprovalRequests.AsNoTracking().Where(r => r.RequesterId == tenant.UserId),
            _ => Visible(tenant, me, seeAll),
        };

        if (query.Status is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }

        if (query.DefinitionId is { } definitionId)
        {
            requests = requests.Where(r => r.DefinitionId == definitionId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search.Trim();
            requests = requests.Where(r => r.Title.Contains(search) || r.DefinitionName.Contains(search));
        }

        int total = await requests.CountAsync(cancellationToken);
        requests = query.Sort switch
        {
            "title" => requests.OrderBy(r => r.Title).ThenBy(r => r.Id),
            "-title" => requests.OrderByDescending(r => r.Title).ThenBy(r => r.Id),
            "process" => requests.OrderBy(r => r.DefinitionName).ThenBy(r => r.Id),
            "-process" => requests.OrderByDescending(r => r.DefinitionName).ThenBy(r => r.Id),
            "amount" => requests.OrderBy(r => r.Amount).ThenBy(r => r.Id),
            "-amount" => requests.OrderByDescending(r => r.Amount).ThenBy(r => r.Id),
            "submitted" => requests.OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id),
            _ => requests.OrderByDescending(r => r.SubmittedAt).ThenBy(r => r.Id),
        };
        List<ApprovalRequest> items = await requests.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new ApprovalRequestPage(await DetailsAsync(tenant, me, items, cancellationToken), page, pageSize, total);
    }

    public async Task<ApprovalRequestDetail?> GetRequestAsync(Guid id, bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        Me me = await MeAsync(tenant, cancellationToken);
        ApprovalRequest? request = await Visible(tenant, me, seeAll).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return request is null ? null : (await DetailsAsync(tenant, me, [request], cancellationToken))[0];
    }

    public async Task<ApprovalRequestDetail> OpenRequestAsync(ApprovalRequestInput input, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalDefinition definition = await db.ApprovalDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == input.DefinitionId && d.IsActive, cancellationToken)
            ?? throw new ApprovalException("Onay süreci bulunamadı ya da kapalı.");

        DateTimeOffset now = time.GetUtcNow();
        (ApprovalRequest request, List<ApprovalRequestStep> steps) = Guarded(() =>
            ApprovalRequest.Open(definition, input.Title, input.Description, input.Amount, input.Currency, tenant.UserId, now));

        // Süreç açılışta sahipsiz kalmasın: her adımda talep sahibinden başka etkin bir onaycı olmalı (dört göz).
        foreach (ApprovalRequestStep step in steps)
        {
            if ((await ApproversAsync(tenant, step, withDelegates: false, cancellationToken)).All(id => id == tenant.UserId))
            {
                throw new ApprovalException($"\"{step.Name}\" adımında talep sahibinden başka etkin onaycı yok; yöneticinize başvurun.");
            }
        }

        db.ApprovalRequests.Add(request);
        db.ApprovalRequestSteps.AddRange(steps);
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalRequest, request.Id, AuditAction.Created, null, Snapshot(request));
        await NotifyActiveAsync(tenant, request, steps, cancellationToken);
        await SaveAsync(cancellationToken);
        Me me = await MeAsync(tenant, cancellationToken);
        return (await DetailsAsync(tenant, me, [request], cancellationToken))[0];
    }

    public async Task<ApprovalRequestDetail?> DecideAsync(Guid id, bool approve, string? comment, long expectedVersion, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        Me me = await MeAsync(tenant, cancellationToken);

        // Görmediği talebin varlığını öğrenmesin: yalnızca onaycısı olduğu ya da sahibi olduğu talepler "var"dır.
        ApprovalRequest? request = await Tracked(Visible(tenant, me, seeAll: false)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return null;
        }

        CheckVersion(request, expectedVersion);
        if (request.Status != ApprovalRequestStatus.Pending)
        {
            throw new ApprovalConflictException("Talep artık karar beklemiyor.");
        }

        if (request.RequesterId == tenant.UserId)
        {
            throw new ApprovalForbiddenException("Kendi talebinize karar veremezsiniz.");
        }

        List<ApprovalRequestStep> steps = await StepsAsync(id, cancellationToken);
        Me decider = Without(me, request.RequesterId);
        ApprovalRequestStep step = steps.FirstOrDefault(s => s.Status == ApprovalStepStatus.Active && IsMine(s, decider))
            ?? throw new ApprovalForbiddenException("Bu talebin bekleyen adımında onaycı değilsiniz.");
        Guid? onBehalfOf = step.ApproverUserId is { } approver && approver != tenant.UserId && decider.Users.Contains(approver) ? approver : null;

        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            throw new ApprovalException("Ret için gerekçe yazın.");
        }

        JsonObject before = Snapshot(request);
        DateTimeOffset now = time.GetUtcNow();
        Guarded(() =>
        {
            if (approve)
            {
                request.Approve(step, steps, tenant.UserId, comment, now, onBehalfOf);
            }
            else
            {
                request.Reject(step, steps, tenant.UserId, comment!, now, onBehalfOf);
            }

            return request;
        });

        JsonObject after = Snapshot(request);
        after["step"] = step.Name;
        after["decision"] = approve ? "Approved" : "Rejected";
        after["comment"] = step.Comment;
        after["onBehalfOf"] = onBehalfOf is { } principal ? await UserNameAsync(principal, cancellationToken) : null;
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalRequest, id, AuditAction.Updated, before, after);

        if (request.Status == ApprovalRequestStatus.Pending)
        {
            await NotifyActiveAsync(tenant, request, [.. steps.Where(s => s.ActivatedAt == now)], cancellationToken);
        }
        else
        {
            await NotifyRequesterAsync(request, cancellationToken);
        }

        await SaveAsync(cancellationToken);
        return (await DetailsAsync(tenant, me, [request], cancellationToken))[0];
    }

    public async Task<ApprovalRequestDetail?> WithdrawAsync(Guid id, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalRequest? request = await db.ApprovalRequests.SingleOrDefaultAsync(r => r.Id == id && r.RequesterId == tenant.UserId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        if (request.Status != ApprovalRequestStatus.Pending)
        {
            throw new ApprovalConflictException("Yalnızca bekleyen talep geri çekilebilir.");
        }

        JsonObject before = Snapshot(request);
        request.Withdraw(await StepsAsync(id, cancellationToken), time.GetUtcNow());
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalRequest, id, AuditAction.Updated, before, Snapshot(request));
        await SaveAsync(cancellationToken);
        return (await DetailsAsync(tenant, await MeAsync(tenant, cancellationToken), [request], cancellationToken))[0];
    }

    public async Task<ApprovalRequestDetail?> ReassignAsync(Guid id, Guid stepId, Guid? userId, Guid? roleId, long expectedVersion, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalRequest? request = await db.ApprovalRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return null;
        }

        CheckVersion(request, expectedVersion);
        List<ApprovalRequestStep> steps = await StepsAsync(id, cancellationToken);
        ApprovalRequestStep step = steps.FirstOrDefault(s => s.Id == stepId && s.Status == ApprovalStepStatus.Active)
            ?? throw new ApprovalException("Yalnızca bekleyen adımın onaycısı değiştirilebilir.");
        if (userId is null == roleId is null)
        {
            throw new ApprovalException("Bir kişi ya da bir rol seçin.");
        }

        if (userId == request.RequesterId)
        {
            throw new ApprovalException("Talep sahibi kendi talebinin onaycısı olamaz.");
        }

        await RequireApproversAsync([new ApprovalStepDefinition(step.Name, step.StepOrder, userId, roleId, null, null)], cancellationToken);
        JsonObject before = new() { ["step"] = step.Name, ["approver"] = await ApproverNameAsync(step, cancellationToken) };
        step.Reassign(userId, roleId);
        request.Touch();
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalRequest, id, AuditAction.Updated, before,
            new JsonObject { ["step"] = step.Name, ["approver"] = await ApproverNameAsync(step, cancellationToken) });
        await NotifyActiveAsync(tenant, request, [step], cancellationToken);
        await SaveAsync(cancellationToken);
        return (await DetailsAsync(tenant, await MeAsync(tenant, cancellationToken), [request], cancellationToken))[0];
    }

    private static bool IsMine(ApprovalRequestStep step, Me me) =>
        Matches(step.ApproverUserId, step.ApproverRoleId, me)
        || (step.EscalatedAt is not null && Matches(step.EscalationUserId, step.EscalationRoleId, me));

    private static bool Matches(Guid? user, Guid? role, Me me) =>
        (user is { } userId && me.Users.Contains(userId)) || (role is { } roleId && me.Roles.Contains(roleId));

    // Dört göz vekâletle aşılamaz: talep sahibinin vekili, talep sahibi adına karar veremez.
    private static Me Without(Me me, Guid requesterId) => me with { Users = [.. me.Users.Where(u => u != requesterId)] };

    private static bool IsCovered(ApprovalRequestStep step, HashSet<Guid> covered) =>
        covered.Contains(step.ApproverUserId ?? step.ApproverRoleId ?? Guid.Empty)
        || (step.EscalatedAt is not null && covered.Contains(step.EscalationUserId ?? step.EscalationRoleId ?? Guid.Empty));

    public async Task<int> EscalateDueAsync(CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        DateTimeOffset now = time.GetUtcNow();
        List<ApprovalRequestStep> due = await db.ApprovalRequestSteps.AsTracking()
            .Where(s => s.Status == ApprovalStepStatus.Active && s.DueAt < now && s.EscalatedAt == null
                && db.ApprovalRequests.Any(r => r.Id == s.RequestId && r.Status == ApprovalRequestStatus.Pending))
            .OrderBy(s => s.DueAt).Take(EscalationBatch).ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        List<Guid> ids = [.. due.Select(s => s.RequestId).Distinct()];
        List<ApprovalRequest> requests = await db.ApprovalRequests.AsTracking().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        foreach (ApprovalRequest request in requests)
        {
            foreach (ApprovalRequestStep step in due.Where(s => s.RequestId == request.Id))
            {
                step.Escalate(now);
                bool hasTarget = (step.EscalationUserId ?? step.EscalationRoleId) is not null;
                Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalRequest, request.Id, AuditAction.Updated,
                    new JsonObject { ["step"] = step.Name },
                    new JsonObject { ["step"] = step.Name, ["escalated"] = hasTarget ? await EscalationNameAsync(step, cancellationToken) : "Hatırlatma" },
                    background: true);
                foreach (Guid userId in await ApproversAsync(tenant, step, withDelegates: true, cancellationToken))
                {
                    if (userId == request.RequesterId)
                    {
                        continue;
                    }

                    await inbox.PostAsync(new InboxMessage(
                        userId, "approval.overdue", $"{request.DefinitionName} sürecinde adımın süresi geçti", CenterPath(request), $"approval:{step.Id}:overdue"), cancellationToken);
                    if (await recipients.FindAsync(userId, cancellationToken) is { } person)
                    {
                        Send(person, $"Süresi geçti: {request.Title}", $"<strong>{Encode(request.Title)}</strong> talebinin <strong>{Encode(step.Name)}</strong> adımının süresi geçti, kararınız bekleniyor.", recipients.Link(CenterPath(request)), request);
                    }
                }
            }

            request.Touch();
        }

        await SaveAsync(cancellationToken);
        return due.Count;
    }

    public async Task<IReadOnlyList<ApprovalDelegationDetail>> ListDelegationsAsync(bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        IQueryable<ApprovalDelegation> delegations = db.ApprovalDelegations.AsNoTracking();
        if (!seeAll)
        {
            delegations = delegations.Where(d => d.PrincipalUserId == tenant.UserId || d.DelegateUserId == tenant.UserId);
        }

        return await DelegationDetailsAsync(await delegations.OrderByDescending(d => d.StartsAt).Take(100).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<ApprovalDelegationDetail> CreateDelegationAsync(ApprovalDelegationInput input, bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        Guid principal = input.PrincipalUserId ?? tenant.UserId;
        if (principal != tenant.UserId && !seeAll)
        {
            throw new ApprovalForbiddenException("Başkası adına vekâlet yalnızca yönetici açabilir.");
        }

        await RequireApproversAsync([new ApprovalStepDefinition("Vekâlet", 1, principal, null, null, null), new ApprovalStepDefinition("Vekâlet", 1, input.DelegateUserId, null, null, null)], cancellationToken);
        ApprovalDelegation delegation = Guarded(() => ApprovalDelegation.Create(
            principal, input.DelegateUserId, input.StartsAt, input.EndsAt, input.Reason, tenant.UserId, time.GetUtcNow()));
        if (await db.ApprovalDelegations.AsNoTracking().AnyAsync(
            d => d.PrincipalUserId == principal && d.RevokedAt == null && d.StartsAt < delegation.EndsAt && d.EndsAt > delegation.StartsAt, cancellationToken))
        {
            throw new ApprovalException("Bu tarihlerle çakışan bir vekâlet zaten var.");
        }

        db.ApprovalDelegations.Add(delegation);
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalDelegation, delegation.Id, AuditAction.Created, null, await SnapshotAsync(delegation, cancellationToken));
        await inbox.PostAsync(new InboxMessage(
            delegation.DelegateUserId, "approval.delegation", $"{await UserNameAsync(principal, cancellationToken)} size onay vekâleti verdi", "onaylar", $"delegation:{delegation.Id}"), cancellationToken);
        await SaveAsync(cancellationToken);
        return (await DelegationDetailsAsync([delegation], cancellationToken))[0];
    }

    public async Task<ApprovalDelegationDetail?> RevokeDelegationAsync(Guid id, bool seeAll, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        ApprovalDelegation? delegation = await db.ApprovalDelegations.SingleOrDefaultAsync(
            d => d.Id == id && (seeAll || d.PrincipalUserId == tenant.UserId), cancellationToken);
        if (delegation is null)
        {
            return null;
        }

        JsonObject before = await SnapshotAsync(delegation, cancellationToken);
        delegation.Revoke(time.GetUtcNow());
        Audit(tenant, CoreModule.AuditedEntityKeys.ApprovalDelegation, id, AuditAction.Updated, before, await SnapshotAsync(delegation, cancellationToken));
        await SaveAsync(cancellationToken);
        return (await DelegationDetailsAsync([delegation], cancellationToken))[0];
    }

    private async Task<List<ApprovalDelegationDetail>> DelegationDetailsAsync(IReadOnlyList<ApprovalDelegation> delegations, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. delegations.SelectMany(d => new[] { d.PrincipalUserId, d.DelegateUserId }).Distinct()];
        Dictionary<Guid, string> users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        DateTimeOffset now = time.GetUtcNow();
        return [.. delegations.Select(d => new ApprovalDelegationDetail(
            d.Id, d.PrincipalUserId, users.GetValueOrDefault(d.PrincipalUserId, "—"), d.DelegateUserId, users.GetValueOrDefault(d.DelegateUserId, "—"),
            d.StartsAt, d.EndsAt, d.Reason, d.RevokedAt is null && d.StartsAt <= now && d.EndsAt > now, d.RevokedAt))];
    }

    private async Task<JsonObject> SnapshotAsync(ApprovalDelegation delegation, CancellationToken cancellationToken) => new()
    {
        ["principal"] = await UserNameAsync(delegation.PrincipalUserId, cancellationToken),
        ["delegate"] = await UserNameAsync(delegation.DelegateUserId, cancellationToken),
        ["startsAt"] = delegation.StartsAt.ToUniversalTime().ToString("O"),
        ["endsAt"] = delegation.EndsAt.ToUniversalTime().ToString("O"),
        ["reason"] = delegation.Reason,
        ["revokedAt"] = delegation.RevokedAt?.ToUniversalTime().ToString("O"),
    };

    private async Task<string> EscalationNameAsync(ApprovalRequestStep step, CancellationToken cancellationToken) =>
        step.EscalationUserId is { } userId
            ? await UserNameAsync(userId, cancellationToken)
            : await db.Roles.AsNoTracking().Where(r => r.Id == step.EscalationRoleId).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken) ?? "—";

    private static IQueryable<ApprovalRequest> Tracked(IQueryable<ApprovalRequest> query) => query.AsTracking();

    private static void CheckVersion(ApprovalRequest request, long expectedVersion)
    {
        if (request.Version != expectedVersion)
        {
            throw new ApprovalConflictException("Talep başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private static T Guarded<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            throw new ApprovalException(ex.Message);
        }
    }

    private static JsonObject Snapshot(ApprovalDefinition definition) => new()
    {
        ["name"] = definition.Name,
        ["description"] = definition.Description,
        ["currency"] = definition.Currency,
        ["isActive"] = definition.IsActive,
        ["revision"] = definition.Revision,
        ["steps"] = string.Join(" → ", definition.Steps.Select(s => s.Name)),
    };

    private static JsonObject Snapshot(ApprovalRequest request) => new()
    {
        ["title"] = request.Title,
        ["process"] = request.DefinitionName,
        ["amount"] = request.Amount,
        ["currency"] = request.Currency,
        ["status"] = request.Status.ToString(),
    };

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan onay işlemi yapılamaz.");

    // Kişinin karar kimlikleri: kendisi, o an vekili olduğu kişiler ve doğrudan rolleri.
    private async Task<Me> MeAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<Guid> principals = await db.ApprovalDelegations.AsNoTracking()
            .Where(d => d.DelegateUserId == tenant.UserId && d.RevokedAt == null && d.StartsAt <= now && d.EndsAt > now)
            .Select(d => d.PrincipalUserId).ToListAsync(cancellationToken);
        List<Guid> roles = await db.MembershipRoles.AsNoTracking()
            .Where(r => r.MembershipId == tenant.MembershipId).Select(r => r.RoleId).ToListAsync(cancellationToken);
        return new Me([tenant.UserId, .. principals], roles);
    }

    // Görünürlük sorgu seviyesindedir: talep sahibi, adımlarda onaycı ya da karar veren olanlar; yönetici hepsini görür.
    private IQueryable<ApprovalRequest> Visible(TenantContext tenant, Me me, bool seeAll)
    {
        IQueryable<ApprovalRequest> requests = db.ApprovalRequests.AsNoTracking();
        List<Guid> users = me.Users;
        List<Guid> roles = me.Roles;
        return seeAll
            ? requests
            : requests.Where(r => r.RequesterId == tenant.UserId || db.ApprovalRequestSteps.Any(s => s.RequestId == r.Id
                && (s.DecidedBy == tenant.UserId
                    || (s.ApproverUserId != null && users.Contains(s.ApproverUserId.Value))
                    || (s.ApproverRoleId != null && roles.Contains(s.ApproverRoleId.Value))
                    || (s.EscalatedAt != null && s.EscalationUserId != null && users.Contains(s.EscalationUserId.Value))
                    || (s.EscalatedAt != null && s.EscalationRoleId != null && roles.Contains(s.EscalationRoleId.Value)))));
    }

    private IQueryable<ApprovalRequest> WaitingForMe(TenantContext tenant, Me me)
    {
        List<Guid> users = me.Users;
        List<Guid> roles = me.Roles;
        return db.ApprovalRequests.AsNoTracking().Where(r => r.Status == ApprovalRequestStatus.Pending && r.RequesterId != tenant.UserId
            && db.ApprovalRequestSteps.Any(s => s.RequestId == r.Id && s.Status == ApprovalStepStatus.Active
                && ((s.ApproverUserId != null && s.ApproverUserId != r.RequesterId && users.Contains(s.ApproverUserId.Value))
                    || (s.ApproverRoleId != null && roles.Contains(s.ApproverRoleId.Value))
                    || (s.EscalatedAt != null && s.EscalationUserId != null && s.EscalationUserId != r.RequesterId && users.Contains(s.EscalationUserId.Value))
                    || (s.EscalatedAt != null && s.EscalationRoleId != null && roles.Contains(s.EscalationRoleId.Value)))));
    }

    private Task<List<ApprovalRequestStep>> StepsAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.ApprovalRequestSteps.AsTracking().Where(s => s.RequestId == requestId).OrderBy(s => s.StepOrder).ThenBy(s => s.Id).ToListAsync(cancellationToken);

    private async Task<List<ApprovalRequestDetail>> DetailsAsync(
        TenantContext tenant, Me me, IReadOnlyList<ApprovalRequest> requests, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. requests.Select(r => r.Id)];
        List<ApprovalRequestStep> steps = await db.ApprovalRequestSteps.AsNoTracking().Where(s => ids.Contains(s.RequestId))
            .OrderBy(s => s.StepOrder).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        HashSet<Guid> covered = await CoveredAsync(tenant, steps, cancellationToken);
        List<Guid> userIds = [.. steps.SelectMany(s => new[] { s.ApproverUserId, s.DecidedBy, s.OnBehalfOf, s.EscalationUserId }).OfType<Guid>().Concat(requests.Select(r => r.RequesterId)).Distinct()];
        List<Guid> roleIds = [.. steps.SelectMany(s => new[] { s.ApproverRoleId, s.EscalationRoleId }).OfType<Guid>().Distinct()];
        Dictionary<Guid, string> users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        Dictionary<Guid, string> roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        DateTimeOffset now = time.GetUtcNow();

        return [.. requests.Select(request =>
        {
            List<ApprovalRequestStep> own = [.. steps.Where(s => s.RequestId == request.Id)];
            List<ApprovalStepDetail> details = [.. own.Select(s => new ApprovalStepDetail(
                s.Id, s.StepOrder, s.Name, s.ApproverUserId, s.ApproverRoleId,
                s.ApproverUserId is { } user ? users.GetValueOrDefault(user, "—") : s.ApproverRoleId is { } role ? roles.GetValueOrDefault(role, "—") : "—",
                s.Status, s.ActivatedAt, s.DueAt,
                s.Status == ApprovalStepStatus.Active && s.DueAt < now,
                s.Status == ApprovalStepStatus.Active && !IsCovered(s, covered),
                s.DecidedBy, s.DecidedBy is { } decider ? users.GetValueOrDefault(decider, "—") : null, s.DecidedAt, s.Comment,
                s.OnBehalfOf is { } principal ? users.GetValueOrDefault(principal, "—") : null,
                s.EscalationUserId is { } target ? users.GetValueOrDefault(target, "—") : s.EscalationRoleId is { } targetRole ? roles.GetValueOrDefault(targetRole, "—") : null,
                s.EscalatedAt is not null))];
            bool pending = request.Status == ApprovalRequestStatus.Pending;
            return new ApprovalRequestDetail(
                request.Id, request.DefinitionId, request.DefinitionName, request.DefinitionRevision, request.Title, request.Description,
                request.Amount, request.Currency, request.RequesterId, users.GetValueOrDefault(request.RequesterId, "—"), request.Status,
                request.SubmittedAt, request.CompletedAt, request.Version, details,
                pending && request.RequesterId != tenant.UserId && own.Any(s => s.Status == ApprovalStepStatus.Active && IsMine(s, Without(me, request.RequesterId))),
                pending && request.RequesterId == tenant.UserId);
        })];
    }

    // Adımı üstlenebilecek biri olan kişi ve rol kimlikleri: etkin üyeler (destek üyeliği sayılmaz, ADR-0028) ve etkin üyesi olan roller.
    private async Task<HashSet<Guid>> CoveredAsync(TenantContext tenant, IReadOnlyList<ApprovalRequestStep> steps, CancellationToken cancellationToken)
    {
        List<Guid> users = [.. steps.SelectMany(s => new[] { s.ApproverUserId, s.EscalationUserId }).OfType<Guid>().Distinct()];
        List<Guid> roles = [.. steps.SelectMany(s => new[] { s.ApproverRoleId, s.EscalationRoleId }).OfType<Guid>().Distinct()];
        List<Guid> activeUsers = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenant.TenantId && m.Status == MembershipStatus.Active && users.Contains(m.UserId))
            .Select(m => m.UserId).ToListAsync(cancellationToken);
        List<Guid> activeRoles = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join member in db.Memberships.AsNoTracking() on assignment.MembershipId equals member.Id
            where roles.Contains(assignment.RoleId) && member.Status == MembershipStatus.Active
            select assignment.RoleId).Distinct().ToListAsync(cancellationToken);
        return [.. activeUsers, .. activeRoles];
    }

    private async Task<List<Guid>> ApproversAsync(TenantContext tenant, ApprovalRequestStep step, bool withDelegates, CancellationToken cancellationToken)
    {
        List<Guid> approvers = await ResolveAsync(tenant, step.ApproverUserId, step.ApproverRoleId, cancellationToken);
        if (step.EscalatedAt is not null)
        {
            approvers.AddRange(await ResolveAsync(tenant, step.EscalationUserId, step.EscalationRoleId, cancellationToken));
        }

        if (withDelegates && step.ApproverUserId is { } principal)
        {
            DateTimeOffset now = time.GetUtcNow();
            approvers.AddRange(await db.ApprovalDelegations.AsNoTracking()
                .Where(d => d.PrincipalUserId == principal && d.RevokedAt == null && d.StartsAt <= now && d.EndsAt > now)
                .Select(d => d.DelegateUserId).ToListAsync(cancellationToken));
        }

        return [.. approvers.Distinct()];
    }

    private async Task<List<Guid>> ResolveAsync(TenantContext tenant, Guid? userId, Guid? roleId, CancellationToken cancellationToken)
    {
        if (userId is { } id)
        {
            return await db.Memberships.AsNoTracking()
                .Where(m => m.TenantId == tenant.TenantId && m.UserId == id && m.Status == MembershipStatus.Active)
                .Select(m => m.UserId).ToListAsync(cancellationToken);
        }

        return roleId is null ? [] : await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join member in db.Memberships.AsNoTracking() on assignment.MembershipId equals member.Id
            where assignment.RoleId == roleId && member.Status == MembershipStatus.Active
            select member.UserId).ToListAsync(cancellationToken);
    }

    private async Task<string> UserNameAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "—";

    private async Task<string> ApproverNameAsync(ApprovalRequestStep step, CancellationToken cancellationToken) =>
        step.ApproverUserId is { } userId
            ? await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "—"
            : await db.Roles.AsNoTracking().Where(r => r.Id == step.ApproverRoleId).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken) ?? "—";

    // Tanımdaki kişi ve rol bu firmaya ait olmalı; başka tenant'ın kimliği yazılamaz.
    private async Task RequireApproversAsync(IReadOnlyList<ApprovalStepDefinition> steps, CancellationToken cancellationToken)
    {
        TenantContext tenant = RequireTenant();
        List<Guid> users = [.. (steps ?? []).SelectMany(s => new[] { s.ApproverUserId, s.EscalateToUserId }).OfType<Guid>().Distinct()];
        List<Guid> roles = [.. (steps ?? []).SelectMany(s => new[] { s.ApproverRoleId, s.EscalateToRoleId }).OfType<Guid>().Distinct()];
        int knownUsers = await db.Memberships.AsNoTracking()
            .CountAsync(m => m.TenantId == tenant.TenantId && m.Status == MembershipStatus.Active && users.Contains(m.UserId), cancellationToken);
        int knownRoles = await db.Roles.AsNoTracking().CountAsync(r => roles.Contains(r.Id), cancellationToken);
        if (knownUsers != users.Count || knownRoles != roles.Count)
        {
            throw new ApprovalException("Seçilen onaycı bu firmanın etkin üyesi ya da rolü değil.");
        }
    }

    private async Task NotifyActiveAsync(TenantContext tenant, ApprovalRequest request, IEnumerable<ApprovalRequestStep> steps, CancellationToken cancellationToken)
    {
        string? link = recipients.Link(CenterPath(request));
        HashSet<Guid> sent = [request.RequesterId];
        foreach (ApprovalRequestStep step in steps.Where(s => s.Status == ApprovalStepStatus.Active))
        {
            foreach (Guid userId in await ApproversAsync(tenant, step, withDelegates: true, cancellationToken))
            {
                if (!sent.Add(userId))
                {
                    continue;
                }

                await inbox.PostAsync(new InboxMessage(
                    userId, "approval.waiting", $"{request.DefinitionName} sürecinde kararınız bekleniyor", CenterPath(request), $"approval:{step.Id}:waiting"), cancellationToken);
                if (await recipients.FindAsync(userId, cancellationToken) is { } person)
                {
                    Send(person, $"Onayınız bekleniyor: {request.Title}", $"<strong>{Encode(request.DefinitionName)}</strong> sürecinde <strong>{Encode(request.Title)}</strong> talebi kararınızı bekliyor (adım: {Encode(step.Name)}).", link, request);
                }
            }
        }
    }

    private static string CenterPath(ApprovalRequest request) => $"onaylar?talep={request.Id}";

    private async Task NotifyRequesterAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        string outcome = request.Status == ApprovalRequestStatus.Approved ? "onaylandı" : "reddedildi";
        await inbox.PostAsync(new InboxMessage(
            request.RequesterId, "approval.result", $"{request.DefinitionName} talebiniz {outcome}", CenterPath(request), $"approval:{request.Id}:result"), cancellationToken);
        if (await recipients.FindAsync(request.RequesterId, cancellationToken) is { } person)
        {
            string result = request.Status == ApprovalRequestStatus.Approved ? "onaylandı" : "reddedildi";
            Send(person, $"Talebiniz {result}: {request.Title}", $"<strong>{Encode(request.Title)}</strong> talebiniz {result}.", recipients.Link($"onaylar?talep={request.Id}"), request);
        }
    }

    private void Send(NotificationRecipient person, string subject, string html, string? link, ApprovalRequest request)
    {
        string body = link is null ? $"<p>{html}</p>" : $"<p>{html}</p><p><a href=\"{Encode(link)}\">Onay merkezinde aç</a></p>";
        emails.Enqueue(new EmailMessage(person.Email, subject, body) { ToName = person.DisplayName, Reference = $"core.approval:{request.Id}" });
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private void Audit(TenantContext tenant, string entityKey, Guid recordId, AuditAction action, JsonObject? before, JsonObject? after, bool background = false)
    {
        JsonObject changes = RecordDiff.Compute(before, after);
        if (action == AuditAction.Updated && changes.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(
            tenant.TenantId, tenant.UserId, entityKey, recordId, action, changes,
            background ? ExecutionSource.BackgroundJob : ExecutionSource.Api, Activity.Current?.TraceId.ToString())
        {
            Subscription = background ? EscalationSource : null,
        });
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ApprovalConflictException("Kayıt başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_ApprovalDefinitions_TenantId_Name", StringComparison.Ordinal) == true)
        {
            throw new ApprovalException("Bu adla bir onay süreci zaten var.");
        }
    }
}
