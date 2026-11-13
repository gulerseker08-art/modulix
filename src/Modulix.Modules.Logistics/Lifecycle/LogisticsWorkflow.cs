using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Approvals;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics.Lifecycle;

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. İzni olmayan eylem listede hiç yer almaz.</summary>
public sealed record LogisticsActionState(string Action, string Label, bool Allowed, string? Message, bool RequiresReason, bool Destructive);

/// <summary>
/// Taşıma talebi, sevkiyat ve sefer iş akışı (ADR-0038 K7, dilim 4–5). Durum yalnızca bu komutlarla değişir; her geçiş kayıt boru hattından geçer
/// (izin, sürüm denetimi 409, audit, outbox olayı). Talep onayı aynı işlemde sevkiyatı açar ve talebe bağlar. Sevkiyatın "Planlandı" durumu sefer
/// atamasıdır (<see cref="ITripPlanner"/>). Seferin planı uygunluk denetimi geçince onaylanır (uyarılar seferde saklanır), sefere çıkışta denetim
/// yinelenir; sefer bütün duraklara varış girilince tamamlanır; iptal seferdeki yüklenmemiş sevkiyatları planlamaya döndürür. Teslim eylemleri
/// teslim kanıtıyla gelir (dilim 6).
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
    public const string Confirm = "confirm";
    public const string Reopen = "reopen";
    public const string Start = "start";
    public const string Complete = "complete";
    public const string StartLoading = "startLoading";
    public const string Depart = "depart";
    public const string Resubmit = "resubmit";
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

        new(LogisticsModule.Entities.Shipment, LogisticsActions.StartLoading, "Yükleme başladı", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Planned], Shipment.Statuses.Loading, false, "Yükleme yalnızca planlanmış sevkiyatta başlar."),
        new(LogisticsModule.Entities.Shipment, LogisticsActions.Depart, "Yola çıktı", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Loading], Shipment.Statuses.InTransit, false, "Yalnızca yüklenen sevkiyat yola çıkabilir."),
        new(LogisticsModule.Entities.Shipment, LogisticsActions.Cancel, "İptal et", LogisticsModule.Permissions.ShipmentsWrite,
            [Shipment.Statuses.Pending, Shipment.Statuses.Planned, Shipment.Statuses.Loading], Shipment.Statuses.Cancelled, true,
            "Yola çıkmış ya da kapanmış sevkiyat iptal edilemez.", Destructive: true),

        new(LogisticsModule.Entities.TripExpense, LogisticsActions.Approve, "Onayla", LogisticsModule.Permissions.ExpensesApprove,
            [TripExpense.Statuses.Pending], TripExpense.Statuses.Approved, false, "Yalnızca bekleyen masraf onaylanabilir."),
        new(LogisticsModule.Entities.TripExpense, LogisticsActions.Reject, "Reddet", LogisticsModule.Permissions.ExpensesApprove,
            [TripExpense.Statuses.Pending], TripExpense.Statuses.Rejected, true, "Yalnızca bekleyen masraf reddedilebilir.", Destructive: true),
        new(LogisticsModule.Entities.TripExpense, LogisticsActions.Resubmit, "Yeniden onaya gönder", LogisticsModule.Permissions.ExpensesWrite,
            [TripExpense.Statuses.Rejected], TripExpense.Statuses.Pending, false, "Yalnızca reddedilen masraf yeniden beklemeye alınır."),

        new(LogisticsModule.Entities.Trip, LogisticsActions.Confirm, "Planı onayla", LogisticsModule.Permissions.TripsWrite,
            [Trip.Statuses.Planning], Trip.Statuses.Ready, false, "Yalnızca planlanan sefer onaylanır."),
        new(LogisticsModule.Entities.Trip, LogisticsActions.Reopen, "Planlamaya geri al", LogisticsModule.Permissions.TripsWrite,
            [Trip.Statuses.Ready], Trip.Statuses.Planning, false, "Yalnızca hazır sefer planlamaya geri alınır."),
        new(LogisticsModule.Entities.Trip, LogisticsActions.Start, "Sefere çıkar", LogisticsModule.Permissions.TripsWrite,
            [Trip.Statuses.Ready], Trip.Statuses.InProgress, false, "Sefere yalnızca planı onaylanmış (hazır) sefer çıkar."),
        new(LogisticsModule.Entities.Trip, LogisticsActions.Complete, "Seferi tamamla", LogisticsModule.Permissions.TripsWrite,
            [Trip.Statuses.InProgress], Trip.Statuses.Completed, false, "Yalnızca yoldaki sefer tamamlanır."),
        new(LogisticsModule.Entities.Trip, LogisticsActions.Cancel, "İptal et", LogisticsModule.Permissions.TripsWrite,
            [Trip.Statuses.Planning, Trip.Statuses.Ready], Trip.Statuses.Cancelled, true, "Yola çıkmış ya da kapanmış sefer iptal edilemez.", Destructive: true),
    ];

    public static Transition? Find(string entityKey, string action) =>
        All.FirstOrDefault(t => t.EntityKey == entityKey && t.Action == action);
}

internal sealed class LogisticsWorkflow(
    IRecordPipeline pipeline, LogisticsWorkflowGate gate, ICurrentAccess access, ITripPlanner planner, LogisticsStoreContext context,
    IApprovalGate approvalGate) : ILogisticsWorkflow
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
        var actions = new List<LogisticsActionState>();
        foreach (var t in LogisticsTransitions.All.Where(t => t.EntityKey == entityKey && granted.Contains(t.Permission)))
        {
            var denied = !t.From.Contains(status) ? t.DeniedMessage : await PreconditionAsync(entityKey, id, t.Action, cancellationToken);
            actions.Add(new LogisticsActionState(t.Action, t.Label, denied is null, denied, t.RequiresReason, t.Destructive));
        }

        return RecordResult<IReadOnlyList<LogisticsActionState>>.Ok([.. actions]);
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

        if (await PreconditionAsync(entityKey, id, transition.Action, cancellationToken) is { } blocked)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, blocked);
        }

        if (entityKey == LogisticsModule.Entities.Trip)
        {
            return await RunTripAsync(id, transition, version, trimmed, cancellationToken);
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

    /// <summary>
    /// Durum dışı ön koşul (409 iletisi): sefer bütün duraklara varış girilmeden tamamlanmaz; yüklemesi başlamış sevkiyatı olan sefer iptal edilmez.
    /// Plan onayı ve sefere çıkışın uygunluk denetimi eylemde yapılır (<see cref="RunTripAsync"/>).
    /// </summary>
    private async Task<string?> PreconditionAsync(string entityKey, Guid id, string action, CancellationToken cancellationToken)
    {
        if (entityKey == LogisticsModule.Entities.TripExpense)
        {
            // Firma masrafı onaylamayı bir onay sürecine bağladıysa son talep onaylanmadan masraf onaylanmaz (Ç6a.3); karar vermek yine izne bağlıdır.
            return action == LogisticsActions.Approve
                ? await approvalGate.BlockReasonAsync(new RecordRef(entityKey, id), LogisticsModule.ApprovalCommands.ApproveExpense, cancellationToken)
                : null;
        }

        if (entityKey != LogisticsModule.Entities.Trip)
        {
            return null;
        }

        return action switch
        {
            LogisticsActions.Complete when await context.Data.Set<TripStop>().AnyAsync(s => s.TripId == id && s.ArrivedAt == null, cancellationToken) =>
                "Bütün duraklara varış saati girilmeden sefer tamamlanamaz.",
            LogisticsActions.Cancel when await context.Data.Set<Shipment>().AnyAsync(
                    s => s.TripId == id && (s.Status == Shipment.Statuses.Loading || s.Status == Shipment.Statuses.InTransit), cancellationToken) =>
                "Yüklemesi başlamış sevkiyatı olan sefer iptal edilemez.",
            _ => null,
        };
    }

    /// <summary>
    /// Sefer eylemleri. Plan onayı ve sefere çıkış uygunluk denetiminden geçer: engel varsa 409 (bulgular iletide); onayda uyarılar seferin notuna
    /// yazılır. İptal tek işlemde seferdeki planlanmış sevkiyatları "Planlama bekliyor"a döndürür ve seferden ayırır (duraklar geçmiş olarak kalır).
    /// </summary>
    private async Task<RecordResult<RecordData>> RunTripAsync(
        Guid id, LogisticsTransitions.Transition transition, long version, string? reason, CancellationToken cancellationToken)
    {
        if (transition.Action is LogisticsActions.Confirm or LogisticsActions.Start)
        {
            var check = await planner.CheckAsync(id, cancellationToken);
            if (!check.IsSuccess)
            {
                return RecordResult<RecordData>.Fail(check.Error!.Code, check.Error.Message, check.Error.FieldErrors);
            }

            if (!check.Value!.CanConfirm)
            {
                return RecordResult<RecordData>.Fail(
                    RecordErrorCode.Conflict, "Sefer hazır değil: " + string.Join(" ", check.Value.Issues.Where(i => i.Blocking).Select(i => i.Message)));
            }

            if (transition.Action == LogisticsActions.Confirm && check.Value.Issues.Count > 0)
            {
                var warnings = "Uyarılarla onaylandı: " + string.Join(" ", check.Value.Issues.Select(i => i.Message));
                reason = warnings.Length > Trip.ReasonMaxLength ? warnings[..(Trip.ReasonMaxLength - 1)] + "…" : warnings;
            }
        }

        if (transition.Action != LogisticsActions.Cancel)
        {
            return await TransitionAsync(LogisticsModule.Entities.Trip, id, transition, version, reason, linkedId: null, cancellationToken);
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            var planned = await context.Data.Set<Shipment>().AsNoTracking()
                .Where(s => s.TripId == id && s.Status == Shipment.Statuses.Planned).Select(s => s.Id).ToListAsync(ct);
            foreach (var shipmentId in planned)
            {
                var shipment = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Shipment, shipmentId), ct);
                if (!shipment.IsSuccess)
                {
                    return shipment;
                }

                RecordResult<RecordData> released;
                using (gate.Open(LogisticsModule.Permissions.TripsWrite, Shipment.Statuses.Pending, reason: null))
                {
                    released = await pipeline.UpdateAsync(new RecordCommand(
                        LogisticsModule.Entities.Shipment, shipmentId, new JsonObject { ["status"] = Shipment.Statuses.Pending }, shipment.Value!.Version), ct);
                }

                if (!released.IsSuccess)
                {
                    return released;
                }
            }

            return await TransitionAsync(LogisticsModule.Entities.Trip, id, transition, version, reason, linkedId: null, ct);
        }, cancellationToken);
    }

    private static string StatusOf(RecordData record) => record.Values["status"]?.GetValue<string>() ?? string.Empty;
}
