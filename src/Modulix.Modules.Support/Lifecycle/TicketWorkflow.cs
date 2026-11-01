using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. Yetkisi olmayan eylem listede hiç yer almaz.</summary>
public sealed record TicketActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <summary>Eylemin girdileri. Sürüm zorunludur (eşzamanlı çakışmada 409); diğer alanlar yalnızca ilgili eylemde kullanılır.</summary>
public sealed record TicketActionInput(
    long Version,
    string? Reason = null,
    Guid? OwnerId = null,
    Guid? QueueId = null,
    string? ResolutionCode = null,
    string? ResolutionNote = null,
    string? RootCause = null,
    string? CorrectiveAction = null,
    string? Priority = null);

/// <summary>Ayarları okur (API host'u tenant ayar servisiyle uygular; modül Core'un ayar servisini doğrudan görmez).</summary>
public interface ISupportPolicyProvider
{
    Task<TicketPolicySettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Talep iş akışı (ADR-0024). Tek yazma yolu: iş akışı kapısı eylemin izniyle açılır ve kayıt boru hattı üzerinden güncelleme yapılır;
/// kapsam, audit, olay ve sürüm denetimi (409) depoda uygulanır. Ön koşullar <see cref="TicketTransitions"/> ile arayüzle ortaktır.
/// Genel güncelleme (PUT) durumu, sorumluyu, kuyruğu, önceliği ya da çözümü değiştiremez; kurallar bu servisle atlanamaz.
/// </summary>
public interface ITicketWorkflow
{
    Task<RecordResult<IReadOnlyList<TicketActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Eylemi uygular; bilinmeyen eylem 422. Gerekçe isteyen eylemde gerekçe zorunludur.</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, TicketActionInput input, CancellationToken cancellationToken);
}

internal sealed class TicketWorkflow(
    IRecordPipeline pipeline,
    TicketWorkflowGate gate,
    ICurrentAccess access,
    SupportStoreContext storeContext,
    IChoiceLists choiceLists,
    SurveyIssuer surveys,
    ISupportPolicyProvider? policy = null) : ITicketWorkflow
{
    private const string Key = SupportModule.Entities.Ticket;

    private static readonly IReadOnlyDictionary<string, string> PermissionOf = new Dictionary<string, string>
    {
        [TicketAction.Triage] = SupportModule.Permissions.TicketsWrite,
        [TicketAction.Assign] = SupportModule.Permissions.TicketsAssign,
        [TicketAction.Take] = SupportModule.Permissions.TicketsTake,
        [TicketAction.Transfer] = SupportModule.Permissions.TicketsAssign,
        [TicketAction.Start] = SupportModule.Permissions.TicketsWrite,
        [TicketAction.WaitCustomer] = SupportModule.Permissions.TicketsWrite,
        [TicketAction.WaitInternal] = SupportModule.Permissions.TicketsWrite,
        [TicketAction.Resume] = SupportModule.Permissions.TicketsWrite,
        [TicketAction.Resolve] = SupportModule.Permissions.TicketsResolve,
        [TicketAction.Close] = SupportModule.Permissions.TicketsClose,
        [TicketAction.Reopen] = SupportModule.Permissions.TicketsReopen,
        [TicketAction.Cancel] = SupportModule.Permissions.TicketsCancel,
        [TicketAction.SetPriority] = SupportModule.Permissions.PriorityOverride,
    };

    private static readonly IReadOnlyDictionary<string, string> EventOf = new Dictionary<string, string>
    {
        [TicketAction.Triage] = TicketEventKinds.Triaged,
        [TicketAction.Assign] = TicketEventKinds.Assigned,
        [TicketAction.Take] = TicketEventKinds.Taken,
        [TicketAction.Transfer] = TicketEventKinds.Transferred,
        [TicketAction.Start] = TicketEventKinds.Started,
        [TicketAction.WaitCustomer] = TicketEventKinds.WaitingCustomer,
        [TicketAction.WaitInternal] = TicketEventKinds.WaitingInternal,
        [TicketAction.Resume] = TicketEventKinds.Resumed,
        [TicketAction.Resolve] = TicketEventKinds.Resolved,
        [TicketAction.Close] = TicketEventKinds.Closed,
        [TicketAction.Reopen] = TicketEventKinds.Reopened,
        [TicketAction.Cancel] = TicketEventKinds.Cancelled,
        [TicketAction.SetPriority] = TicketEventKinds.PriorityChanged,
    };

    public async Task<RecordResult<IReadOnlyList<TicketActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<TicketActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        var state = StateOf(record.Value!, await PolicyAsync(cancellationToken));
        var blockers = await OpenBlockersAsync(id, cancellationToken);
        var list = new List<TicketActionState>();
        foreach (var action in TicketAction.All.Where(a => granted.Contains(PermissionOf[a])))
        {
            var rule = Evaluate(action, state, record.Value!);
            if (rule.Allowed && action == TicketAction.Resolve && blockers > 0)
            {
                rule = BlockedByTasks(blockers);
            }

            list.Add(new TicketActionState(action, rule.Allowed, rule.Code, rule.Message, TicketTransitions.RequiresReason(action)));
        }

        return RecordResult<IReadOnlyList<TicketActionState>>.Ok(list);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid id, string action, TicketActionInput input, CancellationToken cancellationToken)
    {
        if (!PermissionOf.TryGetValue(action, out var permission))
        {
            return Invalid("action", "Bilinmeyen eylem.");
        }

        // Kayıt önce kapsamla okunur: kapsam dışı talep 404 olur; izinsiz eylem 403.
        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (TicketTransitions.RequiresReason(action) && reason is null)
        {
            return Invalid("reason", "Gerekçe zorunludur.");
        }

        var settings = await PolicyAsync(cancellationToken);
        var record = loaded.Value!;
        var state = StateOf(record, settings);
        var rule = Evaluate(action, state, record);
        if (!rule.Allowed)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        // Engelleyici açık alt iş varken talep çözülemez (sunucuda uygulanır).
        if (action == TicketAction.Resolve && await OpenBlockersAsync(id, cancellationToken) is > 0 and var blockers)
        {
            var blocked = BlockedByTasks(blockers);
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, blocked.Message!, [new RecordFieldError(blocked.Code!, blocked.Message!)]);
        }

        var values = new JsonObject { ["lastActivityAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        var currentOwner = Id(record, "ownerId");
        var currentQueue = Id(record, "queueId");
        var target = TicketTransitions.TargetStatus(action, state.Status);

        switch (action)
        {
            case TicketAction.Assign:
            {
                if (input.OwnerId is null && input.QueueId is null)
                {
                    return Invalid("ownerId", "Sorumlu ya da kuyruk seçin.");
                }

                if (input.OwnerId is { } owner && currentOwner is not null && owner != currentOwner && reason is null)
                {
                    return Invalid("reason", "Sorumlusu olan talebin sorumlusunu değiştirmek için gerekçe zorunludur.");
                }

                if (input.QueueId is { } assignQueue && !await QueueIsActiveAsync(assignQueue, cancellationToken))
                {
                    return Invalid("queueId", "Kuyruk bulunamadı ya da pasif.");
                }

                if (input.OwnerId is not null)
                {
                    values["ownerId"] = input.OwnerId.Value.ToString();
                }
                else
                {
                    // Yalnızca kuyruk değişiyorsa sorumlu korunur ve durum değişmez.
                    target = state.Status;
                }

                if (input.QueueId is not null)
                {
                    values["queueId"] = input.QueueId.Value.ToString();
                }

                break;
            }

            case TicketAction.Take:
                values["ownerId"] = storeContext.CurrentUserId?.ToString();
                break;

            case TicketAction.Transfer:
            {
                if (input.QueueId is not { } queue || !await QueueIsActiveAsync(queue, cancellationToken))
                {
                    return Invalid("queueId", "Devredilecek kuyruğu seçin (aktif olmalı).");
                }

                if (queue == currentQueue && (input.OwnerId is null || input.OwnerId == currentOwner))
                {
                    return Invalid("queueId", "Talep zaten bu kuyrukta.");
                }

                values["queueId"] = queue.ToString();
                // Devirde sorumlu sıfırlanır (yeni ekip üzerine alır) ya da yeni sorumlu verilir; sahipsiz talep Atandı durumunda kalmaz.
                values["ownerId"] = input.OwnerId?.ToString();
                target = input.OwnerId is not null
                    ? (state.Status is TicketStatus.New or TicketStatus.Triage or TicketStatus.Reopened ? TicketStatus.Assigned : state.Status)
                    : (state.Status is TicketStatus.Assigned or TicketStatus.InProgress or TicketStatus.Reopened ? TicketStatus.Triage : state.Status);
                break;
            }

            case TicketAction.WaitCustomer:
            case TicketAction.WaitInternal:
            case TicketAction.Cancel:
                values["statusReason"] = reason;
                break;

            case TicketAction.Resume:
            case TicketAction.Start:
                values["statusReason"] = null;
                break;

            case TicketAction.Resolve:
            {
                var isComplaint = await IsComplaintAsync(Text(record, "typeKey"), cancellationToken);
                var errors = ResolutionRules.Validate(input.ResolutionCode, input.ResolutionNote, input.RootCause, input.CorrectiveAction, isComplaint, settings);
                if (errors.Count > 0)
                {
                    return RecordResult<RecordData>.Fail(
                        RecordErrorCode.ValidationFailed, "Çözüm bilgileri eksik.", [.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);
                }

                values["resolutionCode"] = input.ResolutionCode!.Trim();
                values["resolutionNote"] = input.ResolutionNote!.Trim();
                values["rootCause"] = string.IsNullOrWhiteSpace(input.RootCause) ? null : input.RootCause.Trim();
                values["correctiveAction"] = string.IsNullOrWhiteSpace(input.CorrectiveAction) ? null : input.CorrectiveAction.Trim();
                values["resolvedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                values["resolvedBy"] = storeContext.CurrentUserId?.ToString();
                values["statusReason"] = null;
                break;
            }

            case TicketAction.Close:
                values["closedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                break;

            case TicketAction.Reopen:
                values["reopenCount"] = Count(record, "reopenCount") + 1;
                values["closedAt"] = null;
                values["statusReason"] = reason;
                break;

            case TicketAction.SetPriority:
            {
                if (input.Priority is null || !TicketPriority.All.Contains(input.Priority))
                {
                    return Invalid("priority", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketPriority.All)}.");
                }

                if (input.Priority == Text(record, "priority"))
                {
                    return Invalid("priority", "Talep zaten bu önceliğe sahip.");
                }

                values["priority"] = input.Priority;
                values["priorityOverridden"] = true;
                values["priorityReason"] = reason;
                break;
            }
        }

        values["status"] = target;
        var kind = EventOf[action];

        // Geçiş ve yan etkileri tek kaydetmede yazılır; sürüm çakışmasında hiçbiri yazılmaz.
        // Çözümde ayar açıksa memnuniyet anketi aynı işlemde açılır ve e-postası kuyruğa yazılır.
        Func<SupportStoreContext, SupportTicket, CancellationToken, Task>? effect = action == TicketAction.Resolve
            ? (ctx, ticket, ct) => surveys.IssueAsync(ctx, ticket, settings, ct)
            : null;
        using (gate.Open([permission], kind, reason, effect))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, values, input.Version) { Reason = $"support.{kind}" }, cancellationToken);
        }
    }

    private TicketRuleResult Evaluate(string action, TicketState state, RecordData record)
    {
        var rule = TicketTransitions.Can(action, state);
        if (!rule.Allowed)
        {
            return rule;
        }

        var owner = Id(record, "ownerId");
        if (action == TicketAction.Take)
        {
            if (owner is not null && owner == storeContext.CurrentUserId)
            {
                return TicketRuleResult.Deny("already-yours", "Talep zaten sizin üzerinizde.");
            }

            if (owner is not null)
            {
                return TicketRuleResult.Deny("owned", "Talebin sorumlusu var; devralmak için atama yetkisi olan biri atamalıdır.");
            }
        }

        return rule;
    }

    private Task<int> OpenBlockersAsync(Guid ticketId, CancellationToken cancellationToken) =>
        storeContext.Data.Set<TicketTask>().AsNoTracking().CountAsync(t => t.TicketId == ticketId && t.IsBlocking && !t.IsDone, cancellationToken);

    private static TicketRuleResult BlockedByTasks(int count) =>
        TicketRuleResult.Deny("blocking-tasks", $"{count} engelleyici alt iş açık; talebi çözmeden önce bunları tamamlayın.");

    private async Task<TicketPolicySettings> PolicyAsync(CancellationToken cancellationToken) =>
        policy is null ? TicketPolicySettings.Default : await policy.GetAsync(cancellationToken);

    private async Task<bool> IsComplaintAsync(string? typeKey, CancellationToken cancellationToken) =>
        typeKey is not null && (await choiceLists.GetAsync(SupportModule.Lists.TicketType, cancellationToken)).Find(typeKey)?.Category == SupportModule.TypeCategories.Complaint;

    private Task<bool> QueueIsActiveAsync(Guid queueId, CancellationToken cancellationToken) =>
        storeContext.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.Id == queueId && q.IsActive, cancellationToken);

    private static TicketState StateOf(RecordData ticket, TicketPolicySettings settings) =>
        new(Text(ticket, "status") ?? TicketStatus.New, Id(ticket, "ownerId") is not null, Time(ticket, "closedAt"), DateTimeOffset.UtcNow, settings.ReopenWindowDays);

    private static RecordResult<RecordData> Invalid(string field, string message) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    internal static string? Text(RecordData data, string field) => data.Values[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static Guid? Id(RecordData data, string field) => Text(data, field) is { } s && Guid.TryParse(s, out var id) ? id : null;

    internal static DateTimeOffset? Time(RecordData data, string field) =>
        Text(data, field) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static int Count(RecordData data, string field) => data.Values[field] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
}
