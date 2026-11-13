using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. Yetkisi hiç olmayan eylem listede yer almaz.</summary>
public sealed record LeaveActionState(string Action, string Label, bool Allowed, string? Message, bool RequiresReason, bool Destructive);

/// <summary>Gün sayısı önizlemesi: iş günü ve (yıllık izinde) o yılın kalan hakkı.</summary>
public sealed record LeavePreview(decimal Days, decimal? RemainingDays, decimal? PendingDays);

/// <summary>
/// İzin talebi iş akışı (ADR-0042 dilim 2). Durum yalnızca bu komutlarla değişir; her geçiş kayıt boru hattından geçer (izin, kapsam, sürüm 409,
/// audit, outbox). Kurallar:
/// <list type="bullet">
///   <item>Onay ve ret: bekleyen talepte, "izin onayı" izniyle; talebin onaylayanı (talep anındaki yönetici) ya da "izin yönetimi" izni olan
///   kişi verir. Kimse kendi talebini onaylayamaz, reddedemez. Ret gerekçe ister.</item>
///   <item>İptal: bekleyen talebi sahibi ya da İK; onaylanmış talebi yalnızca İK iptal eder (yıllık izin bakiyeye geri eklenir).</item>
/// </list>
/// </summary>
public interface ILeaveWorkflow
{
    Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Bilinmeyen eylem 422; yetkisi yoksa 403; durum ya da kural uygun değilse 409; gerekçe isteyen eylemde gerekçe zorunlu (422).</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken);

    /// <summary>Talep açmadan gün sayısı (yazmaz). Çalışanı görme izni yoksa 404; tarihler geçersizse 422.</summary>
    Task<RecordResult<LeavePreview>> PreviewAsync(Guid employeeId, string? leaveTypeKey, DateOnly start, DateOnly end, bool halfDay, CancellationToken cancellationToken);
}

/// <summary>Eylem anahtarları (API ve arayüz bunları kullanır).</summary>
public static class LeaveActions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Cancel = "cancel";
}

internal sealed class LeaveWorkflow(IRecordPipeline pipeline, HrStoreContext context) : ILeaveWorkflow
{
    private const string Approve = LeaveActions.Approve;
    private const string Reject = LeaveActions.Reject;
    private const string Cancel = LeaveActions.Cancel;

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.LeaveRequest, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var actions = new List<LeaveActionState>();
        foreach (var (action, label, requiresReason, destructive) in new[]
                 {
                     (Approve, "Onayla", false, false), (Reject, "Reddet", true, true), (Cancel, "İptal et", false, true),
                 })
        {
            var decision = await DecideAsync(record.Value!, action, cancellationToken);
            if (decision.Permission is not null || decision.Denied is not null)
            {
                actions.Add(new LeaveActionState(action, label, decision.Permission is not null, decision.Denied, requiresReason, destructive));
            }
        }

        return RecordResult<IReadOnlyList<LeaveActionState>>.Ok([.. actions]);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (action is not (Approve or Reject or Cancel))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        // Önce kayıt okunur: göremeyen için kayıt yoktur (404); yetki denetimi varlığı sızdırmaz.
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.LeaveRequest, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        var decision = await DecideAsync(record.Value!, action, cancellationToken);
        if (decision.Permission is not { } permission)
        {
            return decision.Forbidden
                ? RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, decision.Denied ?? "Bu eylem için yetkiniz yok.")
                : RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, decision.Denied ?? "Bu eylem şu an yapılamaz.");
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (action == Reject && trimmed is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (trimmed is { Length: > LeaveRequest.ReasonMaxLength })
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe çok uzun.",
                [new RecordFieldError("reason", $"En fazla {LeaveRequest.ReasonMaxLength} karakter olabilir.")]);
        }

        var to = action switch { Approve => LeaveRequest.Statuses.Approved, Reject => LeaveRequest.Statuses.Rejected, _ => LeaveRequest.Statuses.Cancelled };
        using (context.Gate.Open(permission, to, trimmed))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(HrModule.Entities.LeaveRequest, id, new JsonObject { ["status"] = to }, version), cancellationToken);
        }
    }

    public async Task<RecordResult<LeavePreview>> PreviewAsync(
        Guid employeeId, string? leaveTypeKey, DateOnly start, DateOnly end, bool halfDay, CancellationToken cancellationToken)
    {
        var employee = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Employee, employeeId), cancellationToken);
        if (!employee.IsSuccess)
        {
            return RecordResult<LeavePreview>.Fail(employee.Error!.Code, employee.Error.Message);
        }

        if (end < start || end.Year != start.Year || (halfDay && end != start))
        {
            return RecordResult<LeavePreview>.Fail(RecordErrorCode.ValidationFailed, "Tarih aralığı geçersiz.",
                [new RecordFieldError("endDate", "Bitiş başlangıçtan önce olamaz, talep tek yıl içinde kalır; yarım gün yalnızca tek günlük talepte.")]);
        }

        var days = await LeaveCalendar.DaysAsync(context, start, end, halfDay, cancellationToken);
        var category = leaveTypeKey is null ? null : (await context.ChoiceLists.GetAsync(HrModule.Lists.LeaveType, cancellationToken)).Find(leaveTypeKey)?.Category;
        if (category != HrModule.LeaveCategories.Annual)
        {
            return RecordResult<LeavePreview>.Ok(new LeavePreview(days, null, null));
        }

        // Kalan hak, kullanıcının görebildiği hak kaydından okunur (kapsam ve izin boru hattında).
        var balances = await pipeline.QueryAsync(new RecordQuery(HrModule.Entities.LeaveBalance)
        {
            Filters = new Dictionary<string, string> { ["employeeId"] = employeeId.ToString(), ["year"] = start.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        }, cancellationToken);
        if (!balances.IsSuccess || balances.Value!.Items.FirstOrDefault() is not { } balance)
        {
            return RecordResult<LeavePreview>.Ok(new LeavePreview(days, null, null));
        }

        var pending = await pipeline.QueryAsync(new RecordQuery(HrModule.Entities.LeaveRequest)
        {
            Filters = new Dictionary<string, string>
            {
                ["employeeId"] = employeeId.ToString(), ["status"] = LeaveRequest.Statuses.Pending,
                ["from"] = $"{start.Year}-01-01", ["to"] = $"{start.Year}-12-31",
            },
            PageSize = RecordQuery.MaxPageSize,
        }, cancellationToken);
        var pendingDays = pending.IsSuccess
            ? pending.Value!.Items.Where(r => r.Values["leaveCategory"]?.GetValue<string>() == HrModule.LeaveCategories.Annual)
                .Sum(r => r.Values["days"]?.GetValue<decimal>() ?? 0m)
            : 0m;
        return RecordResult<LeavePreview>.Ok(new LeavePreview(days, balance.Values["remainingDays"]?.GetValue<decimal>(), pendingDays));
    }

    /// <summary>Eylemin kullanıcı için kararı: yapılabiliyorsa boru hattının isteyeceği izin; değilse neden ve bunun yetki mi durum mu olduğu.</summary>
    private async Task<Decision> DecideAsync(RecordData request, string action, CancellationToken cancellationToken)
    {
        var status = request.Values["status"]?.GetValue<string>();
        var me = context.CurrentUserId;
        var owner = Guid.TryParse(request.Values["employeeUserId"]?.GetValue<string>(), out var o) ? o : (Guid?)null;
        var approver = Guid.TryParse(request.Values["approverUserId"]?.GetValue<string>(), out var a) ? a : (Guid?)null;
        var manages = await context.Access.HasPermissionAsync(HrModule.Permissions.LeavesManage, cancellationToken);

        if (action is Approve or Reject)
        {
            if (!await context.Access.HasPermissionAsync(HrModule.Permissions.LeavesApprove, cancellationToken) || !(manages || (me is not null && approver == me)))
            {
                return Decision.Hidden();
            }

            if (me is not null && owner == me)
            {
                return Decision.Refused("Kendi izin talebinizi onaylayamaz ya da reddedemezsiniz.", forbidden: true);
            }

            return status == LeaveRequest.Statuses.Pending
                ? Decision.Allowed(HrModule.Permissions.LeavesApprove)
                : Decision.Refused("Yalnızca onay bekleyen talep onaylanır ya da reddedilir.");
        }

        var ownRequest = me is not null && owner == me && await context.Access.HasPermissionAsync(HrModule.Permissions.LeavesRequest, cancellationToken);
        if (!manages && !ownRequest)
        {
            return Decision.Hidden();
        }

        return status switch
        {
            LeaveRequest.Statuses.Pending => Decision.Allowed(manages ? HrModule.Permissions.LeavesManage : HrModule.Permissions.LeavesRequest),
            LeaveRequest.Statuses.Approved when manages => Decision.Allowed(HrModule.Permissions.LeavesManage),
            LeaveRequest.Statuses.Approved => Decision.Refused("Onaylanmış izni yalnızca İK iptal edebilir; İK'ya başvurun."),
            _ => Decision.Refused("Reddedilmiş ya da iptal edilmiş talep iptal edilemez."),
        };
    }

    private sealed record Decision(string? Permission, string? Denied, bool Forbidden)
    {
        public static Decision Allowed(string permission) => new(permission, null, false);

        public static Decision Refused(string message, bool forbidden = false) => new(null, message, forbidden);

        /// <summary>Kullanıcının bu eylemle hiç ilgisi yok: listede gösterilmez, denenirse 403.</summary>
        public static Decision Hidden() => new(null, null, true);
    }
}
