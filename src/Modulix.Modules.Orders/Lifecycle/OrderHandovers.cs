using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

public sealed record HandoverItemView(Guid Id, long Version, int Position, string Text, bool IsRequired, bool IsDone, Guid? DoneBy, DateTimeOffset? DoneAt, string? Note, bool IsDefault);

public sealed record HandoverEventView(Guid Id, int Round, string Kind, DateTimeOffset At, Guid? ActorId, string? Note);

/// <summary>Bir devir eyleminin kullanıcı için durumu. Yetkisi olmayan eylem listede hiç yer almaz.</summary>
public sealed record HandoverActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresNote);

/// <summary>Devir görünümü: kayıt alanları, kontrol listesi, eksik bilgi listesi, yapılabilir eylemler ve geçmiş.</summary>
public sealed record OrderHandoverView(
    RecordData Handover, IReadOnlyList<HandoverItemView> Items, IReadOnlyList<string> Missing, IReadOnlyList<HandoverActionState> Actions, IReadOnlyList<HandoverEventView> History);

/// <summary>
/// Satıştan operasyona devir (ADR-0023, dilim 3). Satış sorumlusu yapılandırılmış devir kaydını hazırlar; sistem zorunlu bilgi ve kontrol listesini
/// sunucuda denetler (eksik varsa gönderilemez); sorumlu operasyon kullanıcısı devri kabul eder ya da gerekçeyle bilgi ister; satış tamamlayıp yeniden
/// gönderir. Gönderen, kabul eden, tarihler, bilgi talepleri ve yeniden gönderimler geçmişte tutulur. Gönderimde operasyon için, bilgi isteğinde satış
/// için takip aktivitesi açılır (aktivite modülü yoksa atlanır); kabul bekleme süresi aşılırsa zamanlayıcı satış sorumlusuna bir kez takip açar.
/// Kabul edilen devir için operasyon işi oluşturma sonraki dilimdedir.
/// </summary>
public interface IOrderHandovers
{
    /// <summary>Siparişin devir kaydı; yoksa (ya da sipariş görünmüyorsa) "bulunamadı".</summary>
    Task<RecordResult<OrderHandoverView>> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    Task<RecordResult<OrderHandoverView>> GetAsync(Guid handoverId, CancellationToken cancellationToken);

    /// <summary>Devir kaydını varsayılan kontrol listesiyle açar (sipariş başına tek).</summary>
    Task<RecordResult<OrderHandoverView>> CreateAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Eylemi uygular. <paramref name="responsibleUserId"/> yalnızca kabulde: sorumlu operasyon kullanıcısı atanır.</summary>
    Task<RecordResult<OrderHandoverView>> RunAsync(Guid handoverId, string action, long version, string? note, Guid? responsibleUserId, CancellationToken cancellationToken);
}

internal sealed class OrderHandovers(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrderReader reader,
    OrdersStoreContext storeContext,
    TimeProvider time,
    IOperationJobGenerator jobs,
    IOrderPolicyProvider? policy = null,
    IFollowUpActivities? followUps = null) : IOrderHandovers
{
    private static readonly IReadOnlyDictionary<string, string> PermissionOf = new Dictionary<string, string>
    {
        [HandoverAction.Send] = OrdersModule.Permissions.HandoversSend,
        [HandoverAction.RequestInfo] = OrdersModule.Permissions.HandoversAccept,
        [HandoverAction.Accept] = OrdersModule.Permissions.HandoversAccept,
    };

    public async Task<RecordResult<OrderHandoverView>> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var query = await pipeline.QueryAsync(
            new RecordQuery(OrdersModule.Entities.Handover, 1, 1, null, new Dictionary<string, string> { ["orderId"] = orderId.ToString() }), cancellationToken);
        if (!query.IsSuccess)
        {
            return RecordResult<OrderHandoverView>.Fail(query.Error!.Code, query.Error.Message, query.Error.FieldErrors);
        }

        return query.Value!.Items.Count == 0
            ? RecordResult<OrderHandoverView>.Fail(RecordErrorCode.NotFound, "Bu sipariş için devir kaydı yok.")
            : RecordResult<OrderHandoverView>.Ok(await ViewAsync(query.Value.Items[0], cancellationToken));
    }

    public async Task<RecordResult<OrderHandoverView>> GetAsync(Guid handoverId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Handover, handoverId), cancellationToken);
        return record.IsSuccess
            ? RecordResult<OrderHandoverView>.Ok(await ViewAsync(record.Value!, cancellationToken))
            : RecordResult<OrderHandoverView>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
    }

    public async Task<RecordResult<OrderHandoverView>> CreateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!order.IsSuccess)
        {
            return RecordResult<OrderHandoverView>.Fail(order.Error!.Code, order.Error.Message, order.Error.FieldErrors);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.HandoversWrite))
        {
            return RecordResult<OrderHandoverView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{OrdersModule.Permissions.HandoversWrite}' izni gerekir.");
        }

        var existing = await GetByOrderAsync(orderId, cancellationToken);
        if (existing.IsSuccess)
        {
            return RecordResult<OrderHandoverView>.Fail(RecordErrorCode.Conflict, "Bu siparişin devir kaydı zaten var.");
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([OrdersModule.Permissions.HandoversWrite], HandoverEventKinds.Created);
            var created = await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.Handover, null, new JsonObject { ["orderId"] = orderId.ToString() }) { Reason = "handover.Created" }, ct);
            return created.IsSuccess
                ? RecordResult<OrderHandoverView>.Ok(await ViewAsync(created.Value!, ct))
                : RecordResult<OrderHandoverView>.Fail(created.Error!.Code, created.Error.Message, created.Error.FieldErrors);
        }, cancellationToken);
    }

    public async Task<RecordResult<OrderHandoverView>> RunAsync(
        Guid handoverId, string action, long version, string? note, Guid? responsibleUserId, CancellationToken cancellationToken)
    {
        if (!PermissionOf.TryGetValue(action, out var permission))
        {
            return Invalid("action", "Bilinmeyen eylem.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Handover, handoverId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<OrderHandoverView>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<OrderHandoverView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var handover = (await reader.HandoverAsync(handoverId, cancellationToken))!;
        var order = (await reader.OrderAsync(handover.OrderId, cancellationToken))!;
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (HandoverRules.RequiresNote(action, handover.Status) && text is null)
        {
            return Invalid("note", action == HandoverAction.RequestInfo ? "Bilgi istemek için gerekçe zorunludur." : "Yeniden göndermek için neyin tamamlandığını yazın.");
        }

        var missing = await MissingAsync(handover, cancellationToken);
        var rule = HandoverRules.Can(action, handover.Status, order.Status, missing);
        if (!rule.Allowed)
        {
            return RecordResult<OrderHandoverView>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        var now = time.GetUtcNow();
        var values = new JsonObject();
        string kind;
        switch (action)
        {
            case HandoverAction.Send:
                kind = handover.Round == 0 ? HandoverEventKinds.Sent : HandoverEventKinds.Resubmitted;
                values["status"] = HandoverStatus.Sent;
                values["round"] = handover.Round + 1;
                values["sentAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                values["sentBy"] = storeContext.CurrentUserId?.ToString();
                values["operationsActivityId"] = null;
                values["salesActivityId"] = null;
                break;
            case HandoverAction.RequestInfo:
                kind = HandoverEventKinds.InfoRequested;
                values["status"] = HandoverStatus.InfoRequested;
                values["infoRequest"] = text;
                break;
            default:
                kind = HandoverEventKinds.Accepted;
                values["status"] = HandoverStatus.Accepted;
                values["acceptedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                values["acceptedBy"] = storeContext.CurrentUserId?.ToString();
                if (responsibleUserId is { } responsible)
                {
                    values["responsibleUserId"] = responsible.ToString();
                }

                break;
        }

        var result = await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([permission], kind, text, handoverEffect: async (ctx, h, token) =>
            {
                HandoverSync.AddEvent(ctx, h, kind, text);
                await HandoverSync.MirrorAsync(ctx, h, token);
            });
            var updated = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Handover, handoverId, values, version) { Reason = $"handover.{kind}" }, ct);
            if (!updated.IsSuccess)
            {
                return RecordResult<OrderHandoverView>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }

            // Kabul edilen devir için operasyon işleri aynı işlemde üretilir (bir kez; hata halinde kabul de geri alınır ve yeniden denenebilir).
            if (action == HandoverAction.Accept)
            {
                var generated = await jobs.GenerateAsync(handoverId, permission, ct);
                if (!generated.IsSuccess)
                {
                    return RecordResult<OrderHandoverView>.Fail(generated.Error!.Code, generated.Error.Message, generated.Error.FieldErrors);
                }
            }

            return RecordResult<OrderHandoverView>.Ok(await ViewAsync(updated.Value!, ct));
        }, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        // Takip aktivitesi karar kaydından sonra, en iyi çabayla açılır; açılamazsa devir kararı geçerli kalır.
        if (action is HandoverAction.Send or HandoverAction.RequestInfo)
        {
            await TryFollowUpAsync(action, handover, order, permission, text, cancellationToken);
            return await GetAsync(handoverId, cancellationToken);
        }

        return result;
    }

    private async Task TryFollowUpAsync(string action, OrderHandover handover, SalesOrder order, string permission, string? note, CancellationToken cancellationToken)
    {
        if (followUps is null)
        {
            return;
        }

        var settings = policy is null ? OrderPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var send = action == HandoverAction.Send;
        var fresh = (await reader.HandoverAsync(handover.Id, cancellationToken))!;
        var due = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(send ? Math.Max(1, settings.HandoverAcceptDueDays) : 2);
        var request = new FollowUpRequest(
            send ? $"Devir kabulü bekliyor: {order.Number}" : $"Devir için bilgi isteniyor: {order.Number}",
            send ? $"{order.Title} siparişinin satış devri operasyona gönderildi; kabul edin ya da gerekçeyle bilgi isteyin." : $"Operasyon devir için bilgi istedi: {note}",
            due,
            send ? fresh.ResponsibleUserId : order.OwnerId,
            OrdersModule.Entities.Order,
            order.Id);
        var created = await followUps.CreateAsync(request, cancellationToken);
        if (created.ActivityId is not { } activityId)
        {
            return;
        }

        var values = new JsonObject { [send ? "operationsActivityId" : "salesActivityId"] = activityId.ToString() };
        using var _ = gate.Open([permission], send ? "FollowUpCreated" : "FollowUpCreated");
        await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Handover, handover.Id, values, fresh.Version) { Reason = "handover.FollowUpCreated" }, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> MissingAsync(OrderHandover handover, CancellationToken cancellationToken)
    {
        var items = await reader.HandoverItemsAsync(handover.Id, cancellationToken);
        return HandoverRules.Missing(HandoverSync.Snapshot(handover), [.. items.Select(i => new HandoverChecklistState(i.Text, i.IsRequired, i.IsDone))]);
    }

    private async Task<OrderHandoverView> ViewAsync(RecordData record, CancellationToken cancellationToken)
    {
        var handover = (await reader.HandoverAsync(record.Id, cancellationToken))!;
        var order = (await reader.OrderAsync(handover.OrderId, cancellationToken))!;
        var items = await reader.HandoverItemsAsync(handover.Id, cancellationToken);
        var missing = HandoverRules.Missing(HandoverSync.Snapshot(handover), [.. items.Select(i => new HandoverChecklistState(i.Text, i.IsRequired, i.IsDone))]);
        var granted = await access.GetPermissionsAsync(cancellationToken);
        var actions = new List<HandoverActionState>();
        foreach (var action in HandoverAction.All.Where(a => granted.Contains(PermissionOf[a])))
        {
            var rule = HandoverRules.Can(action, handover.Status, order.Status, missing);
            actions.Add(new HandoverActionState(action, rule.Allowed, rule.Code, rule.Message, HandoverRules.RequiresNote(action, handover.Status)));
        }

        var events = await reader.HandoverEventsAsync(handover.Id, cancellationToken);
        return new OrderHandoverView(
            record,
            [.. items.Select(i => new HandoverItemView(i.Id, i.Version, i.Position, i.Text, i.IsRequired, i.IsDone, i.DoneBy, i.DoneAt, i.Note, i.IsDefault))],
            missing,
            actions,
            [.. events.Select(e => new HandoverEventView(e.Id, e.Round, e.Kind, e.CreatedAt, e.ActorId, e.Note))]);
    }

    private static RecordResult<OrderHandoverView> Invalid(string field, string message) =>
        RecordResult<OrderHandoverView>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
