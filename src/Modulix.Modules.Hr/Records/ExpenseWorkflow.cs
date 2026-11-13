using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Masraf beyanı iş akışı (ADR-0042 dilim 3). Durum yalnızca bu komutlarla değişir; her geçiş kayıt boru hattından geçer (izin, kapsam, sürüm 409,
/// audit, outbox). Kurallar:
/// <list type="bullet">
///   <item>Gönder: taslak, en az bir satırlı beyanı sahibi ya da İK gönderir; onaylayan o anki yöneticinin kullanıcısıdır ve değişmez.</item>
///   <item>Onayla, iade et (düzeltmeye, gerekçeli), reddet (gerekçeli): gönderilmiş beyanda, "masraf onayı" izniyle; beyanın onaylayanı ya da
///   "masraf yönetimi" izni olan kişi. Kimse kendi beyanına karar veremez.</item>
///   <item>Ödendi: onaylanmış beyanda "masraf ödeme" izniyle; ödeme referansı isteğe bağlıdır; kimse kendi beyanının ödemesini işaretleyemez.</item>
///   <item>İptal: taslak ya da gönderilmiş beyanı sahibi ya da İK iptal eder.</item>
/// </list>
/// </summary>
public interface IExpenseWorkflow
{
    Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Bilinmeyen eylem 422; yetkisi yoksa 403; durum ya da kural uygun değilse 409; gerekçe isteyen eylemde gerekçe zorunlu (422).</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken);
}

/// <summary>Masraf eylem anahtarları (API ve arayüz bunları kullanır).</summary>
public static class ExpenseActions
{
    public const string Submit = "submit";
    public const string Approve = "approve";
    public const string Return = "return";
    public const string Reject = "reject";
    public const string Pay = "pay";
    public const string Cancel = "cancel";
}

internal sealed class ExpenseWorkflow(IRecordPipeline pipeline, HrStoreContext context) : IExpenseWorkflow
{
    private static readonly (string Action, string Label, bool RequiresReason, bool Destructive, string To)[] Definitions =
    [
        (ExpenseActions.Submit, "Onaya gönder", false, false, ExpenseClaim.Statuses.Submitted),
        (ExpenseActions.Approve, "Onayla", false, false, ExpenseClaim.Statuses.Approved),
        (ExpenseActions.Return, "Düzeltmeye iade et", true, false, ExpenseClaim.Statuses.Draft),
        (ExpenseActions.Reject, "Reddet", true, true, ExpenseClaim.Statuses.Rejected),
        (ExpenseActions.Pay, "Ödendi olarak işaretle", false, false, ExpenseClaim.Statuses.Paid),
        (ExpenseActions.Cancel, "İptal et", false, true, ExpenseClaim.Statuses.Cancelled),
    ];

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.ExpenseClaim, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var actions = new List<LeaveActionState>();
        foreach (var d in Definitions)
        {
            var decision = await DecideAsync(record.Value!, d.Action, cancellationToken);
            if (decision.Permission is not null || decision.Denied is not null)
            {
                actions.Add(new LeaveActionState(d.Action, d.Label, decision.Permission is not null, decision.Denied, d.RequiresReason, d.Destructive));
            }
        }

        return RecordResult<IReadOnlyList<LeaveActionState>>.Ok([.. actions]);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (Definitions.FirstOrDefault(d => d.Action == action) is not { Action: not null } definition)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        // Önce kayıt okunur: göremeyen için kayıt yoktur (404); yetki denetimi varlığı sızdırmaz.
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.ExpenseClaim, id), cancellationToken);
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
        if (definition.RequiresReason && trimmed is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (trimmed is { Length: > ExpenseClaim.ReasonMaxLength })
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Metin çok uzun.",
                [new RecordFieldError("reason", $"En fazla {ExpenseClaim.ReasonMaxLength} karakter olabilir.")]);
        }

        var approver = action == ExpenseActions.Submit ? await ApproverAsync(record.Value!, cancellationToken) : null;
        using (context.Gate.Open(permission, definition.To, trimmed, approver))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(HrModule.Entities.ExpenseClaim, id, new JsonObject { ["status"] = definition.To }, version), cancellationToken);
        }
    }

    /// <summary>Gönderim anındaki onaylayan: çalışanın ayrılmamış yöneticisinin kullanıcısı (çalışanın kendisi değilse).</summary>
    private async Task<Guid?> ApproverAsync(RecordData claim, CancellationToken cancellationToken)
    {
        var employeeId = Guid.Parse(claim.Values["employeeId"]!.GetValue<string>());
        var employees = context.Data.Set<Employee>().AsNoTracking();
        var employee = await employees.Where(e => e.Id == employeeId).Select(e => new { e.UserId, e.ManagerEmployeeId }).SingleAsync(cancellationToken);
        if (employee.ManagerEmployeeId is not { } manager)
        {
            return null;
        }

        var approver = await employees.Where(e => e.Id == manager && e.Status != Employee.Statuses.Terminated).Select(e => e.UserId).SingleOrDefaultAsync(cancellationToken);
        return approver == employee.UserId ? null : approver;
    }

    /// <summary>Eylemin kullanıcı için kararı: yapılabiliyorsa boru hattının isteyeceği izin; değilse neden ve bunun yetki mi durum mu olduğu.</summary>
    private async Task<Decision> DecideAsync(RecordData claim, string action, CancellationToken cancellationToken)
    {
        var status = claim.Values["status"]?.GetValue<string>();
        var me = context.CurrentUserId;
        var owner = Guid.TryParse(claim.Values["employeeUserId"]?.GetValue<string>(), out var o) ? o : (Guid?)null;
        var approver = Guid.TryParse(claim.Values["approverUserId"]?.GetValue<string>(), out var a) ? a : (Guid?)null;
        var mine = me is not null && owner == me;
        var manages = await context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesManage, cancellationToken);

        switch (action)
        {
            case ExpenseActions.Approve or ExpenseActions.Return or ExpenseActions.Reject:
                if (!await context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesApprove, cancellationToken) || !(manages || (me is not null && approver == me)))
                {
                    return Decision.Hidden();
                }

                if (mine)
                {
                    return Decision.Refused("Kendi masraf beyanınıza karar veremezsiniz.", forbidden: true);
                }

                return status == ExpenseClaim.Statuses.Submitted
                    ? Decision.Allowed(HrModule.Permissions.ExpensesApprove)
                    : Decision.Refused("Yalnızca onaya gönderilmiş beyana karar verilir.");

            case ExpenseActions.Pay:
                if (!await context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesPay, cancellationToken))
                {
                    return Decision.Hidden();
                }

                if (mine)
                {
                    return Decision.Refused("Kendi beyanınızın ödemesini işaretleyemezsiniz.", forbidden: true);
                }

                return status == ExpenseClaim.Statuses.Approved
                    ? Decision.Allowed(HrModule.Permissions.ExpensesPay)
                    : Decision.Refused("Yalnızca onaylanmış beyan ödendi olarak işaretlenir.");

            default:
                var ownClaim = mine && await context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesRequest, cancellationToken);
                if (!manages && !ownClaim)
                {
                    return Decision.Hidden();
                }

                var permission = manages ? HrModule.Permissions.ExpensesManage : HrModule.Permissions.ExpensesRequest;
                if (action == ExpenseActions.Submit)
                {
                    if (status != ExpenseClaim.Statuses.Draft)
                    {
                        return Decision.Refused("Yalnızca taslak beyan onaya gönderilir.");
                    }

                    return claim.Values["lineCount"]?.GetValue<int>() > 0
                        ? Decision.Allowed(permission)
                        : Decision.Refused("Onaya göndermeden önce en az bir masraf satırı ekleyin.");
                }

                return status is ExpenseClaim.Statuses.Draft or ExpenseClaim.Statuses.Submitted
                    ? Decision.Allowed(permission)
                    : Decision.Refused("Yalnızca taslak ya da onay bekleyen beyan iptal edilir.");
        }
    }

    private sealed record Decision(string? Permission, string? Denied, bool Forbidden)
    {
        public static Decision Allowed(string permission) => new(permission, null, false);

        public static Decision Refused(string message, bool forbidden = false) => new(null, message, forbidden);

        /// <summary>Kullanıcının bu eylemle hiç ilgisi yok: listede gösterilmez, denenirse 403.</summary>
        public static Decision Hidden() => new(null, null, true);
    }
}
