using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Zimmet iş akışı (ADR-0042 dilim 4). Her geçiş kayıt boru hattından geçer (izin, kapsam, sürüm 409, audit, outbox). Kurallar:
/// <list type="bullet">
///   <item>Teslim aldım: açık ve henüz onaylanmamış zimmeti yalnızca zimmetli çalışanın kendisi onaylar.</item>
///   <item>İade al: açık zimmeti "demirbaş yönetimi" izniyle kapatır; gerekçe alanı iadedeki durumdur (isteğe bağlı). Demirbaş depoya döner.</item>
///   <item>Kayıp: açık zimmeti "demirbaş yönetimi" izniyle kayıp olarak kapatır; açıklama zorunludur. Demirbaş "kayıp" olur.</item>
/// </list>
/// </summary>
public interface IAssetWorkflow
{
    Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Bilinmeyen eylem 422; yetkisi yoksa 403; durum uygun değilse 409; açıklama isteyen eylemde açıklama zorunlu (422).</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken);
}

/// <summary>Zimmet eylem anahtarları (API ve arayüz bunları kullanır).</summary>
public static class AssetActions
{
    public const string Acknowledge = "acknowledge";
    public const string Return = "return";
    public const string Lost = "lost";
}

internal sealed class AssetWorkflow(IRecordPipeline pipeline, HrStoreContext context) : IAssetWorkflow
{
    private static readonly (string Action, string Label, bool RequiresReason, bool Destructive, string To)[] Definitions =
    [
        (AssetActions.Acknowledge, "Teslim aldım", false, false, AssetAssignment.Statuses.Active),
        (AssetActions.Return, "İade al", false, false, AssetAssignment.Statuses.Returned),
        (AssetActions.Lost, "Kayıp olarak kapat", true, true, AssetAssignment.Statuses.Lost),
    ];

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.AssetAssignment, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var actions = new List<LeaveActionState>();
        foreach (var d in Definitions)
        {
            var (permission, denied) = await DecideAsync(record.Value!, d.Action, cancellationToken);
            if (permission is not null || denied is not null)
            {
                actions.Add(new LeaveActionState(d.Action, d.Label, permission is not null, denied, d.RequiresReason, d.Destructive));
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

        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.AssetAssignment, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        var (permission, denied) = await DecideAsync(record.Value!, action, cancellationToken);
        if (permission is null)
        {
            return denied is null
                ? RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Bu eylem için yetkiniz yok.")
                : RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, denied);
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (definition.RequiresReason && trimmed is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Açıklama zorunludur.", [new RecordFieldError("reason", "Açıklama zorunludur.")]);
        }

        if (trimmed is { Length: > AssetAssignment.TextMaxLength })
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Metin çok uzun.",
                [new RecordFieldError("reason", $"En fazla {AssetAssignment.TextMaxLength} karakter olabilir.")]);
        }

        using (context.Gate.Open(permission, definition.To, trimmed))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(HrModule.Entities.AssetAssignment, id, new JsonObject { ["status"] = definition.To }, version), cancellationToken);
        }
    }

    /// <summary>Kullanıcı için karar: yapılabiliyorsa boru hattının isteyeceği izin; değilse (yalnızca ilgiliyse) nedeni. İkisi de boşsa eylem görünmez (403).</summary>
    private async Task<(string? Permission, string? Denied)> DecideAsync(RecordData assignment, string action, CancellationToken cancellationToken)
    {
        var status = assignment.Values["status"]?.GetValue<string>();
        var owner = Guid.TryParse(assignment.Values["employeeUserId"]?.GetValue<string>(), out var o) ? o : (Guid?)null;
        var acknowledged = assignment.Values["acknowledgedAt"] is not null;

        if (action == AssetActions.Acknowledge)
        {
            if (context.CurrentUserId is not { } me || owner != me)
            {
                return (null, null);
            }

            return status != AssetAssignment.Statuses.Active ? (null, "Kapanmış zimmet onaylanmaz.")
                : acknowledged ? (null, "Teslim zaten onaylandı.")
                : (HrModule.Permissions.AssetsRead, null);
        }

        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.AssetsManage, cancellationToken))
        {
            return (null, null);
        }

        return status == AssetAssignment.Statuses.Active
            ? (HrModule.Permissions.AssetsManage, null)
            : (null, "Zimmet zaten kapanmış.");
    }
}
