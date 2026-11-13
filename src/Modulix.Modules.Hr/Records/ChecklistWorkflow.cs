using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// İşe giriş/çıkış iş akışı (ADR-0042 dilim 5). Her yazma kayıt boru hattından geçer (izin, kapsam, sürüm 409, audit, outbox). Kurallar:
/// <list type="bullet">
///   <item>Başlat: "süreç yönetimi" izniyle; süreç ve şablonun bütün adımlarından görevler tek işlemde açılır (son tarih = başlangıç + gün farkı).</item>
///   <item>Görevi tamamla: atanan kullanıcı ya da "süreç yönetimi" izni olan kişi (İK görevini yalnızca o); "zimmet iadesi" görevi çalışanın açık
///   zimmeti varken tamamlanamaz. Atla (gerekçeli) ve yeniden aç: yalnızca "süreç yönetimi". İptal edilmiş sürecin görevi değişmez.</item>
///   <item>Süreci iptal et: açık süreçte, "süreç yönetimi" izniyle, gerekçeli.</item>
/// </list>
/// </summary>
public interface IChecklistWorkflow
{
    Task<RecordResult<RecordData>> StartAsync(Guid employeeId, Guid templateId, DateOnly startDate, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<LeaveActionState>>> TaskActionsAsync(Guid taskId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RunTaskAsync(Guid taskId, string action, long version, string? reason, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<LeaveActionState>>> ChecklistActionsAsync(Guid checklistId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RunChecklistAsync(Guid checklistId, string action, long version, string? reason, CancellationToken cancellationToken);
}

/// <summary>Süreç ve görev eylem anahtarları.</summary>
public static class ChecklistActions
{
    public const string Complete = "complete";
    public const string Skip = "skip";
    public const string Reopen = "reopen";
    public const string Cancel = "cancel";
}

internal sealed class ChecklistWorkflow(IRecordPipeline pipeline, HrStoreContext context) : IChecklistWorkflow
{
    private static readonly (string Action, string Label, bool RequiresReason, bool Destructive, string To)[] TaskDefinitions =
    [
        (ChecklistActions.Complete, "Tamamlandı", false, false, ChecklistTask.Statuses.Done),
        (ChecklistActions.Skip, "Atla", true, false, ChecklistTask.Statuses.Skipped),
        (ChecklistActions.Reopen, "Yeniden aç", false, false, ChecklistTask.Statuses.Open),
    ];

    public async Task<RecordResult<RecordData>> StartAsync(Guid employeeId, Guid templateId, DateOnly startDate, CancellationToken cancellationToken)
    {
        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.ChecklistsManage, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Süreç başlatma yetkiniz yok.");
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            RecordResult<RecordData> checklist;
            using (context.Gate.Open(HrModule.Permissions.ChecklistsManage, EmployeeChecklist.Statuses.Open, reason: null))
            {
                checklist = await pipeline.CreateAsync(new RecordCommand(HrModule.Entities.EmployeeChecklist, null, new JsonObject
                {
                    ["employeeId"] = employeeId.ToString(),
                    ["templateId"] = templateId.ToString(),
                    ["startDate"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }), ct);
            }

            if (!checklist.IsSuccess)
            {
                return checklist;
            }

            var items = await context.Data.Set<ChecklistTemplateItem>().AsNoTracking().Where(i => i.TemplateId == templateId)
                .OrderBy(i => i.Sequence).ThenBy(i => i.CreatedAt).ToListAsync(ct);
            foreach (var item in items)
            {
                RecordResult<RecordData> task;
                using (context.Gate.Open(HrModule.Permissions.ChecklistsManage, ChecklistTask.Statuses.Open, reason: null))
                {
                    task = await pipeline.CreateAsync(new RecordCommand(HrModule.Entities.ChecklistTask, null, new JsonObject
                    {
                        ["checklistId"] = checklist.Value!.Id.ToString(),
                        ["sequence"] = item.Sequence,
                        ["title"] = item.Title,
                        ["description"] = item.Description,
                        ["responsibleRole"] = item.ResponsibleRole,
                        ["dueDate"] = startDate.AddDays(item.DueOffsetDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["taskType"] = item.TaskType,
                    }), ct);
                }

                if (!task.IsSuccess)
                {
                    return task;
                }
            }

            return await pipeline.GetAsync(new RecordRef(HrModule.Entities.EmployeeChecklist, checklist.Value!.Id), ct);
        }, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> TaskActionsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.ChecklistTask, taskId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var actions = new List<LeaveActionState>();
        foreach (var d in TaskDefinitions)
        {
            var (permission, denied) = await DecideTaskAsync(record.Value!, d.Action, cancellationToken);
            if (permission is not null || denied is not null)
            {
                actions.Add(new LeaveActionState(d.Action, d.Label, permission is not null, denied, d.RequiresReason, d.Destructive));
            }
        }

        return RecordResult<IReadOnlyList<LeaveActionState>>.Ok([.. actions]);
    }

    public async Task<RecordResult<RecordData>> RunTaskAsync(Guid taskId, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (TaskDefinitions.FirstOrDefault(d => d.Action == action) is not { Action: not null } definition)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.ChecklistTask, taskId), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        var (permission, denied) = await DecideTaskAsync(record.Value!, action, cancellationToken);
        if (Check(permission, denied, definition.RequiresReason, reason, ChecklistTask.NoteMaxLength) is { } failure)
        {
            return failure;
        }

        using (context.Gate.Open(permission!, definition.To, Trim(reason)))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(HrModule.Entities.ChecklistTask, taskId, new JsonObject { ["status"] = definition.To }, version), cancellationToken);
        }
    }

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> ChecklistActionsAsync(Guid checklistId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.EmployeeChecklist, checklistId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var (permission, denied) = await DecideChecklistAsync(record.Value!, cancellationToken);
        return RecordResult<IReadOnlyList<LeaveActionState>>.Ok(permission is null && denied is null
            ? []
            : [new LeaveActionState(ChecklistActions.Cancel, "Süreci iptal et", permission is not null, denied, RequiresReason: true, Destructive: true)]);
    }

    public async Task<RecordResult<RecordData>> RunChecklistAsync(Guid checklistId, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (action != ChecklistActions.Cancel)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.EmployeeChecklist, checklistId), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        var (permission, denied) = await DecideChecklistAsync(record.Value!, cancellationToken);
        if (Check(permission, denied, requiresReason: true, reason, EmployeeChecklist.ReasonMaxLength) is { } failure)
        {
            return failure;
        }

        using (context.Gate.Open(permission!, EmployeeChecklist.Statuses.Cancelled, Trim(reason)))
        {
            return await pipeline.UpdateAsync(new RecordCommand(
                HrModule.Entities.EmployeeChecklist, checklistId, new JsonObject { ["status"] = EmployeeChecklist.Statuses.Cancelled }, version), cancellationToken);
        }
    }

    /// <summary>Görev eylemi için karar; ikisi de boşsa eylem kullanıcıyı ilgilendirmez (gösterilmez, denenirse 403).</summary>
    private async Task<(string? Permission, string? Denied)> DecideTaskAsync(RecordData task, string action, CancellationToken cancellationToken)
    {
        var status = task.Values["status"]?.GetValue<string>();
        var assignee = Guid.TryParse(task.Values["assigneeUserId"]?.GetValue<string>(), out var a) ? a : (Guid?)null;
        var manages = await context.Access.HasPermissionAsync(HrModule.Permissions.ChecklistsManage, cancellationToken);
        var checklistId = Guid.Parse(task.Values["checklistId"]!.GetValue<string>());
        var checklistStatus = await context.Data.Set<EmployeeChecklist>().AsNoTracking().Where(c => c.Id == checklistId).Select(c => c.Status).SingleAsync(cancellationToken);

        if (action == ChecklistActions.Complete)
        {
            var mine = context.CurrentUserId is { } me && assignee == me;
            if (!manages && !mine)
            {
                return (null, null);
            }

            if (checklistStatus == EmployeeChecklist.Statuses.Cancelled)
            {
                return (null, "İptal edilmiş sürecin görevi değişmez.");
            }

            if (status != ChecklistTask.Statuses.Open)
            {
                return (null, "Görev zaten kapanmış.");
            }

            if (task.Values["taskType"]?.GetValue<string>() == Checklists.TaskTypes.AssetReturn)
            {
                var employeeId = Guid.Parse(task.Values["employeeId"]!.GetValue<string>());
                var open = await context.Data.Set<AssetAssignment>().AsNoTracking()
                    .CountAsync(z => z.EmployeeId == employeeId && z.Status == AssetAssignment.Statuses.Active, cancellationToken);
                if (open > 0)
                {
                    return (null, $"Çalışanın {open} açık zimmeti var; önce demirbaşları iade alın.");
                }
            }

            return (manages ? HrModule.Permissions.ChecklistsManage : HrModule.Permissions.ChecklistsRead, null);
        }

        if (!manages)
        {
            return (null, null);
        }

        if (checklistStatus == EmployeeChecklist.Statuses.Cancelled)
        {
            return (null, "İptal edilmiş sürecin görevi değişmez.");
        }

        return action == ChecklistActions.Skip
            ? status == ChecklistTask.Statuses.Open ? (HrModule.Permissions.ChecklistsManage, null) : (null, "Görev zaten kapanmış.")
            : status != ChecklistTask.Statuses.Open ? (HrModule.Permissions.ChecklistsManage, null) : (null, "Görev zaten açık.");
    }

    private async Task<(string? Permission, string? Denied)> DecideChecklistAsync(RecordData checklist, CancellationToken cancellationToken)
    {
        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.ChecklistsManage, cancellationToken))
        {
            return (null, null);
        }

        return checklist.Values["status"]?.GetValue<string>() == EmployeeChecklist.Statuses.Open
            ? (HrModule.Permissions.ChecklistsManage, null)
            : (null, "Yalnızca açık süreç iptal edilir.");
    }

    private static RecordResult<RecordData>? Check(string? permission, string? denied, bool requiresReason, string? reason, int maxLength)
    {
        if (permission is null)
        {
            return denied is null
                ? RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Bu eylem için yetkiniz yok.")
                : RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, denied);
        }

        var trimmed = Trim(reason);
        if (requiresReason && trimmed is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        return trimmed is { } text && text.Length > maxLength
            ? RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Metin çok uzun.", [new RecordFieldError("reason", $"En fazla {maxLength} karakter olabilir.")])
            : null;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
