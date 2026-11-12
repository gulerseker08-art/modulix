using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics.Lifecycle;

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. İzni olmayan eylem listede hiç yer almaz.</summary>
public sealed record LogisticsActionState(string Action, string Label, bool Allowed, string? Message, bool RequiresReason, bool Destructive);

/// <summary>
/// Taşıma talebi ve sevkiyat iş akışı (ADR-0038 K7, dilim 4). Durum yalnızca bu komutlarla değişir; her geçiş kayıt boru hattından geçer (izin,
/// sürüm denetimi 409, audit, outbox olayı). Talep onayı aynı işlemde sevkiyatı açar ve talebe bağlar. Teslim eylemleri teslim kanıtıyla gelir
/// (dilim 6); sefer ataması "Planlandı"yı otomatik verir (dilim 5), o zamana kadar planlama elle işaretlenir.
/// </summary>
public interface ILogisticsWorkflow
{
    /// <summary>Kaydın eylemleri (kullanıcının izni olanlar, ön koşul sonucuyla). Kayıt okunamıyorsa boru hattının hatası.</summary>
    Task<RecordResult<IReadOnlyList<LogisticsActionState>>> ActionsAsync(string entityKey, Guid id, CancellationToken cancellationToken);

    /// <summary>Eylemi uygular. Bilinmeyen eylem 422; izin yoksa 403; ön koşul geçmezse 409; gerekçe isteyen eylemde gerekçe zorunlu (422).</summary>
    Task<RecordResult<RecordData>> RunAsync(string entityKey, Guid id, string action, long version, string? reason, CancellationToken cancellationToken);
}

/// <summary>Eylem anahtarları (API ve arayüz bunları kullanır).</summary>
public static class LogisticsActions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Cancel = "cancel";
    public const string Plan = "plan";
    public const string StartLoading = "startLoading";
    public const string Depart = "depart";
}

/// <summary>Geçiş tablosu: eylem, izin, hangi durumlardan, hangi duruma, gerekçe gerekir mi.</summary>
public static class LogisticsTransitions
{
    public sealed record Transition(
        string EntityKey, string Action, string Label, string Permission, IReadOnlyCollection<string> From, string To, bool RequiresReason, string DeniedMessage,
        bool Destructive = false);

    public static IReadOnlyList<Transition> All { get; } =
    [
        new(LogisticsModule.Entities.TransportRequest, LogisticsActions.Approve, "Onayla ve sevkiyat aç", LogisticsModule.Permissions.RequestsApprove,
            [TransportRequest.Statuses.New], TransportRequest.Statuses.Approved, false, "Yalnızca yeni talep onaylanabilir."),
        new(LogisticsModule.Entities.TransportRequest, LogisticsActions.Reject, "Reddet", LogisticsModule.Permissions.RequestsApprove,
            [TransportRequest.Statuses.New], TransportRequest.Statuses.Rejected, true, "Yalnızca yeni talep reddedilebilir.", Destructive: true),
        new(LogisticsModule.Entities.TransportRequest, LogisticsActions.Cancel, "İptal et", LogisticsModule.Permissions.RequestsWrite,
            [TransportRequest.Statuses.New], TransportRequest.Statuses.Cancelled, true, "Yalnızca yeni talep iptal edilebilir; onaylanan talebin sevkiyatını iptal edin.", Destructive: true),

        new(LogisticsModule.Entities.Shipment, LogisticsActions.Plan, "Planlandı", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Pending], Shipment.Statuses.Planned, false, "Yalnızca planlama bekleyen sevkiyat planlanabilir."),
        new(LogisticsModule.Entities.Shipment, LogisticsActions.StartLoading, "Yükleme başladı", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Planned], Shipment.Statuses.Loading, false, "Yükleme yalnızca planlanmış sevkiyatta başlar."),
        new(LogisticsModule.Entities.Shipment, LogisticsActions.Depart, "Yola çıktı", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Loading], Shipment.Statuses.InTransit, false, "Yalnızca yüklenen sevkiyat yola çıkabilir."),
        new(LogisticsModule.Entities.Shipment, LogisticsActions.Cancel, "İptal et", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Pending, Shipment.Statuses.Planned, Shipment.Statuses.Loading], Shipment.Statuses.Cancelled, true,
            "Yola çıkmış ya da kapanmış sevkiyat iptal edilemez.", Destructive: true),
    ];

    public static Transition? Find(string entityKey, string action) =>
        All.FirstOrDefault(t => t.EntityKey == entityKey && t.Action == action);
}

internal sealed class LogisticsWorkflow(IRecordPipeline pipeline, LogisticsWorkflowGate gate, ICurrentAccess access) : ILogisticsWorkflow
{
    /// <summary>Talepten sevkiyata kopyalanan alanlar (yük, güzergâh, taraflar).</summary>
    private static readonly string[] CargoKeys =
    [
        "customerPartyId", "senderPartyId", "receiverPartyId", "transportModeKey", "pickupCity", "pickupAddress", "deliveryCity", "deliveryAddress",
        "pickupDate", "deliveryBy", "cargoDescription", "weightKg", "volumeM3", "palletCount", "loadingMeters", "hazardous", "notes",
    ];

    public async Task<RecordResult<IReadOnlyList<LogisticsActionState>>> ActionsAsync(string entityKey, Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(entityKey, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LogisticsActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        var status = StatusOf(record.Value!);
        IReadOnlyList<LogisticsActionState> actions =
        [
            .. LogisticsTransitions.All
                .Where(t => t.EntityKey == entityKey && granted.Contains(t.Permission))
                .Select(t => new LogisticsActionState(
                    t.Action, t.Label, t.From.Contains(status), t.From.Contains(status) ? null : t.DeniedMessage, t.RequiresReason, t.Destructive)),
        ];
        return RecordResult<IReadOnlyList<LogisticsActionState>>.Ok(actions);
    }

    public async Task<RecordResult<RecordData>> RunAsync(string entityKey, Guid id, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (LogisticsTransitions.Find(entityKey, action) is not { } transition)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        // Önce kayıt okunur: göremeyen için kayıt yoktur (404), izin denetimi varlığı sızdırmaz.
        var record = await pipeline.GetAsync(new RecordRef(entityKey, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        if (!await access.HasPermissionAsync(transition.Permission, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Bu eylem için yetkiniz yok.");
        }

        if (!transition.From.Contains(StatusOf(record.Value!)))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, transition.DeniedMessage);
        }

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (transition.RequiresReason && trimmed is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (trimmed is { Length: > CargoEntity.ReasonMaxLength })
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe çok uzun.",
                [new RecordFieldError("reason", $"En fazla {CargoEntity.ReasonMaxLength} karakter olabilir.")]);
        }

        return transition.Action == LogisticsActions.Approve && entityKey == LogisticsModule.Entities.TransportRequest
            ? await pipeline.RunAtomicAsync(ct => ApproveAsync(record.Value!, transition, version, ct), cancellationToken)
            : await TransitionAsync(entityKey, id, transition, version, trimmed, linkedId: null, cancellationToken);
    }

    /// <summary>Talep onayı: sevkiyat açılır (talebin yük ve güzergâhıyla), talep "Onaylandı" olur ve sevkiyata bağlanır; ikisi tek işlemde.</summary>
    private async Task<RecordResult<RecordData>> ApproveAsync(RecordData request, LogisticsTransitions.Transition transition, long version, CancellationToken cancellationToken)
    {
        var values = new JsonObject { ["requestId"] = request.Id.ToString() };
        foreach (var key in CargoKeys)
        {
            if (request.Values[key] is { } node)
            {
                values[key] = node.DeepClone();
            }
        }

        // Özel alanlar türe özgüdür; talebinkiler sevkiyata taşınmaz.
        RecordResult<RecordData> shipment;
        using (gate.Open(transition.Permission, status: null, reason: null))
        {
            shipment = await pipeline.CreateAsync(new RecordCommand(LogisticsModule.Entities.Shipment, null, values), cancellationToken);
        }

        if (!shipment.IsSuccess)
        {
            return shipment;
        }

        return await TransitionAsync(request.EntityKey, request.Id, transition, version, reason: null, shipment.Value!.Id, cancellationToken);
    }

    private async Task<RecordResult<RecordData>> TransitionAsync(
        string entityKey, Guid id, LogisticsTransitions.Transition transition, long version, string? reason, Guid? linkedId, CancellationToken cancellationToken)
    {
        using (gate.Open(transition.Permission, transition.To, reason, linkedId))
        {
            return await pipeline.UpdateAsync(new RecordCommand(entityKey, id, new JsonObject { ["status"] = transition.To }, version), cancellationToken);
        }
    }

    private static string StatusOf(RecordData record) => record.Values["status"]?.GetValue<string>() ?? string.Empty;
}
