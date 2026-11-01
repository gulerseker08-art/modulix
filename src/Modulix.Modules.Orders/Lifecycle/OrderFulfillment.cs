using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

public sealed record DeliveryLineView(Guid OrderLineId, string Name, string? Unit, decimal Planned, decimal Delivered);

public sealed record EvidenceView(Guid Id, string FileName, string ContentType, int SizeBytes, string Sha256, string? Description, DateTimeOffset At, Guid? UploadedBy);

public sealed record FulfillmentActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

public sealed record DeliveryView(
    Guid Id, long Version, Guid OrderId, Guid? JobId, string Number, string Status, DateOnly PlannedDate, DateOnly? ActualDate, string? ReceiverName, string? ReceiverNote,
    string? FailureReason, Guid? RescheduledFromId, string? RescheduleReason, string? Notes, DateTimeOffset? DeliveredAt, Guid? DeliveredBy, bool IsOverdue,
    IReadOnlyList<DeliveryLineView> Lines, IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<FulfillmentActionState> Actions);

/// <summary>Ürün kaleminin teslimat özeti: sipariş edilen, etkin (iptal/azaltma sonrası), teslim edilen, kalan ve açık teslimatlarda planlanan miktar.</summary>
public sealed record ProductLineView(Guid LineId, string Name, string? Unit, decimal Ordered, decimal Effective, decimal Delivered, decimal Remaining, decimal PlannedOpen);

public sealed record ServiceStartView(
    Guid Id, long Version, string Status, DateOnly? PlannedStartDate, DateTimeOffset? ActualStartAt, Guid? StartedBy, DateTimeOffset? CustomerConfirmedAt, string? CustomerConfirmedBy,
    string? ConfirmationNote, string? DelayReason, DateTimeOffset? CompletedAt, Guid? CompletedBy);

/// <summary>Hizmet kaleminin başlangıç görünümü. <see cref="OngoingObligation"/>: süreli (tekrarlayan) hizmette başlamış ama tamamlanmamış devam eden yükümlülük.</summary>
public sealed record ServiceLineView(
    Guid LineId, string Name, string Kind, decimal Quantity, string? RecurringPeriod, ServiceStartView? Start, IReadOnlyList<string> Blockers,
    IReadOnlyList<FulfillmentActionState> Actions, bool OngoingObligation, bool IsOverdue);

public sealed record FulfillmentView(
    Guid OrderId, string OrderNumber, string Status, string? ContractNumber, IReadOnlyList<ProductLineView> Products, IReadOnlyList<DeliveryView> Deliveries,
    IReadOnlyList<ServiceLineView> Services);

public sealed record PlanDeliveryInput(DateOnly PlannedDate, IReadOnlyList<QuantityRequest>? Lines, Guid? JobId, string? Notes);

/// <summary>Teslimat eylemi girdisi. <c>deliver</c>: teslim alan kişi, gerçekleşen tarih ve (isteğe bağlı) kalem miktarları (boşsa planlanan); <c>fail</c>/<c>cancel</c>: gerekçe; <c>reschedule</c>: yeni tarih ve gerekçe.</summary>
public sealed record DeliveryActionInput(
    string? Reason, string? ReceiverName, string? ReceiverNote, DateOnly? ActualDate, IReadOnlyList<QuantityRequest>? Lines, DateOnly? PlannedDate);

public sealed record ServiceActionInput(string? ConfirmedBy, string? Note);

public sealed record EvidenceFile(string FileName, string ContentType, byte[] Content, string Sha256);

/// <summary>
/// Teslimat ve hizmet başlangıcı (ADR-0023, dilim 5). Ürün kalemleri çoklu ve kısmi teslimatla, hizmet kalemleri başlangıç kaydıyla ayrı izlenir; sipariş özeti
/// (gerçekleşme durumu) ikisinden türetilir ve her değişiklikte aynı işlemde güncellenir. Fazla teslimat sunucuda ve <c>CHECK</c> kısıtıyla engellenir; başarısız
/// teslimat gerekçeyle işaretlenip yeniden planlanır; teslim edilen kayıtlar silinmez. Hizmet başlangıcı eksik ön koşulla (onaysız sipariş, kabul edilmemiş devir,
/// bitmemiş bağımlı iş ya da ön koşul görevi) kaydedilemez ve hizmetin tamamlanması sayılmaz. Teslim kanıtı sınırlı boyut ve türde dosyadır; indirme yetki ve kapsamdan geçer.
/// </summary>
public interface IOrderFulfillment
{
    Task<RecordResult<FulfillmentView>> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<RecordResult<DeliveryView>> PlanDeliveryAsync(Guid orderId, PlanDeliveryInput input, CancellationToken cancellationToken);

    Task<RecordResult<DeliveryView>> RunDeliveryAsync(Guid deliveryId, string action, long version, DeliveryActionInput input, CancellationToken cancellationToken);

    Task<RecordResult<DeliveryView>> AddEvidenceAsync(Guid deliveryId, string? fileName, string? contentType, byte[] content, string? description, CancellationToken cancellationToken);

    Task<RecordResult<EvidenceFile>> GetEvidenceAsync(Guid deliveryId, Guid evidenceId, CancellationToken cancellationToken);

    Task<RecordResult<ServiceLineView>> PlanServiceAsync(Guid orderId, Guid orderLineId, DateOnly? plannedStartDate, CancellationToken cancellationToken);

    Task<RecordResult<ServiceLineView>> RunServiceAsync(Guid serviceStartId, string action, long version, ServiceActionInput input, CancellationToken cancellationToken);
}

internal sealed class OrderFulfillment(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrderReader reader,
    OrdersStoreContext storeContext,
    ITransactionLock transactionLock,
    TimeProvider time) : IOrderFulfillment
{
    private const string Permission = OrdersModule.Permissions.FulfillmentRecord;

    // ---- Görünüm

    public async Task<RecordResult<FulfillmentView>> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<FulfillmentView>(loaded.Error!);
        }

        return RecordResult<FulfillmentView>.Ok(await BuildAsync(orderId, cancellationToken));
    }

    private async Task<FulfillmentView> BuildAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = (await reader.OrderAsync(orderId, cancellationToken))!;
        var lines = (await reader.LinesAsync(orderId, cancellationToken)).Where(l => !l.IsCancelled && l.EffectiveQuantity > 0m).ToList();
        var deliveries = await reader.DeliveriesOfOrderAsync(orderId, cancellationToken);
        var deliveryLines = await reader.DeliveryLinesOfOrderAsync(orderId, cancellationToken);
        var starts = await reader.ServiceStartsOfOrderAsync(orderId, cancellationToken);
        var granted = await access.GetPermissionsAsync(cancellationToken);
        var canRecord = granted.Contains(Permission);
        var today = Today();
        var plannedOpen = deliveryLines
            .Where(l => deliveries.Any(d => d.Id == l.DeliveryId && d.Status == DeliveryStatus.Planned))
            .GroupBy(l => l.OrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.PlannedQuantity));

        var products = lines.Where(l => LineKinds.IsDeliverable(l.Kind))
            .Select(l => new ProductLineView(l.Id, l.Name, l.UnitName, l.Quantity, l.EffectiveQuantity, l.DeliveredQuantity, Math.Max(0m, l.EffectiveQuantity - l.DeliveredQuantity), plannedOpen.GetValueOrDefault(l.Id)))
            .ToList();
        var lineById = (await reader.LinesAsync(orderId, cancellationToken)).ToDictionary(l => l.Id);

        var deliveryViews = new List<DeliveryView>();
        foreach (var d in deliveries)
        {
            var evidence = (await reader.EvidenceListAsync(d.Id, cancellationToken)).Select(e => new EvidenceView(e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.Description, e.At, e.UploadedBy)).ToList();
            var actions = new List<FulfillmentActionState>();
            if (canRecord)
            {
                var rescheduled = deliveries.Any(x => x.RescheduledFromId == d.Id);
                foreach (var action in DeliveryAction.All)
                {
                    var rule = FulfillmentTransitions.CanDeliveryAct(action, d.Status, order.Status, rescheduled);
                    actions.Add(new FulfillmentActionState(action, rule.Allowed, rule.Code, rule.Message, FulfillmentTransitions.DeliveryRequiresReason(action) || action == DeliveryAction.Reschedule));
                }
            }

            deliveryViews.Add(new DeliveryView(
                d.Id, d.Version, d.OrderId, d.JobId, d.Number, d.Status, d.PlannedDate, d.ActualDate, d.ReceiverName, d.ReceiverNote, d.FailureReason, d.RescheduledFromId,
                d.RescheduleReason, d.Notes, d.DeliveredAt, d.DeliveredBy, d.Status == DeliveryStatus.Planned && d.PlannedDate < today,
                [.. deliveryLines.Where(l => l.DeliveryId == d.Id).Select(l => new DeliveryLineView(l.OrderLineId, lineById.TryGetValue(l.OrderLineId, out var ol) ? ol.Name : "—", lineById.TryGetValue(l.OrderLineId, out var ol2) ? ol2.UnitName : null, l.PlannedQuantity, l.DeliveredQuantity))],
                evidence, actions));
        }

        var handover = await reader.HandoverOfOrderAsync(orderId, cancellationToken);
        var serviceViews = new List<ServiceLineView>();
        foreach (var line in lines.Where(l => !LineKinds.IsDeliverable(l.Kind)))
        {
            serviceViews.Add(await ServiceViewAsync(order, handover, line, starts.FirstOrDefault(s => s.OrderLineId == line.Id), canRecord, today, cancellationToken));
        }

        return new FulfillmentView(orderId, order.Number, order.FulfillmentStatus, order.ContractNumber, products, deliveryViews, serviceViews);
    }

    private async Task<ServiceLineView> ServiceViewAsync(
        SalesOrder order, OrderHandover? handover, SalesOrderLine line, OrderServiceStart? start, bool canRecord, DateOnly today, CancellationToken cancellationToken)
    {
        var blockers = start is { Status: ServiceStatus.Planned } ? await ServiceBlockersAsync(order, handover, line, cancellationToken) : [];
        var actions = new List<FulfillmentActionState>();
        if (canRecord && start is not null)
        {
            foreach (var action in ServiceAction.All)
            {
                var rule = FulfillmentTransitions.CanServiceAct(action, start.Status);
                if (rule.Allowed && action == ServiceAction.Start && blockers.Count > 0)
                {
                    rule = OrderRuleResult.Deny("blocked", "Başlangıç engelleniyor: " + blockers[0] + (blockers.Count > 1 ? $" (+{blockers.Count - 1} engel daha)" : string.Empty));
                }

                actions.Add(new FulfillmentActionState(action, rule.Allowed, rule.Code, rule.Message, false));
            }
        }

        var ongoing = start is { Status: ServiceStatus.Started } && !string.IsNullOrEmpty(line.RecurringPeriod);
        var overdue = start is { Status: ServiceStatus.Planned, PlannedStartDate: { } planned } && planned < today;
        return new ServiceLineView(
            line.Id, line.Name, line.Kind, line.EffectiveQuantity, line.RecurringPeriod,
            start is null ? null : new ServiceStartView(
                start.Id, start.Version, start.Status, start.PlannedStartDate, start.ActualStartAt, start.StartedBy, start.CustomerConfirmedAt, start.CustomerConfirmedBy,
                start.ConfirmationNote, start.DelayReason, start.CompletedAt, start.CompletedBy),
            blockers, actions, ongoing, overdue);
    }

    // ---- Teslimat

    public async Task<RecordResult<DeliveryView>> PlanDeliveryAsync(Guid orderId, PlanDeliveryInput input, CancellationToken cancellationToken)
    {
        var (order, error) = await RequireAsync<DeliveryView>(orderId, cancellationToken);
        if (order is null)
        {
            return error!;
        }

        if (order.Status != OrderStatus.Approved)
        {
            return Invalid<DeliveryView>("orderId", "Teslimat yalnızca onaylanmış sipariş için planlanır.");
        }

        var handover = await reader.HandoverOfOrderAsync(orderId, cancellationToken);
        if (handover is not { Status: HandoverStatus.Accepted })
        {
            return Invalid<DeliveryView>("orderId", "Teslimat planlamadan önce satış devri operasyon tarafından kabul edilmiş olmalıdır.");
        }

        if (input.JobId is { } jobId && await reader.JobAsync(jobId, cancellationToken) is not { Status: not JobStatus.Cancelled } job || input.JobId is { } j2 && (await reader.JobAsync(j2, cancellationToken))?.OrderId != orderId)
        {
            return Invalid<DeliveryView>("jobId", "Operasyon işi bu siparişte bulunamadı ya da iptal edilmiş.");
        }

        var capacity = await CapacityAsync(orderId, excludeDelivery: null, cancellationToken);
        var requested = input.Lines is { Count: > 0 } given ? given : [.. capacity.Lines.Where(kv => LineKinds.IsDeliverable(kv.Value.Kind) && kv.Value.Remaining - capacity.PlannedElsewhere.GetValueOrDefault(kv.Key) > 0m)
            .Select(kv => new QuantityRequest(kv.Key, kv.Value.Remaining - capacity.PlannedElsewhere.GetValueOrDefault(kv.Key)))];
        var errors = DeliveryRules.ValidatePlan(requested, capacity.Lines, capacity.PlannedElsewhere);
        if (errors.Count > 0)
        {
            return FailFrom<DeliveryView>(errors);
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(orderId, ct);
            var freshPlan = await CapacityAsync(orderId, null, ct);
            if (DeliveryRules.ValidatePlan(requested, freshPlan.Lines, freshPlan.PlannedElsewhere) is { Count: > 0 } stalePlan)
            {
                return FailFrom<DeliveryView>(stalePlan);
            }

            var created = await CreateDeliveryAsync(order, input.PlannedDate, requested, input.JobId, input.Notes, null, null, FulfillmentEventKinds.DeliveryPlanned, ct);
            if (!created.IsSuccess)
            {
                return Fail<DeliveryView>(created.Error!);
            }

            var synced = await SyncAsync(orderId, ct);
            return synced is not null ? Fail<DeliveryView>(synced) : RecordResult<DeliveryView>.Ok(await DeliveryViewAsync(created.Value!.Id, ct));
        }, cancellationToken);
    }

    public async Task<RecordResult<DeliveryView>> RunDeliveryAsync(Guid deliveryId, string action, long version, DeliveryActionInput input, CancellationToken cancellationToken)
    {
        if (!DeliveryAction.All.Contains(action))
        {
            return Invalid<DeliveryView>("action", "Bilinmeyen eylem.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Delivery, deliveryId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<DeliveryView>(loaded.Error!);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(Permission))
        {
            return RecordResult<DeliveryView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{Permission}' izni gerekir.");
        }

        var delivery = (await reader.DeliveryAsync(deliveryId, cancellationToken))!;
        var order = (await reader.OrderAsync(delivery.OrderId, cancellationToken))!;
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if ((FulfillmentTransitions.DeliveryRequiresReason(action) || action == DeliveryAction.Reschedule) && reason is null)
        {
            return Invalid<DeliveryView>("reason", "Gerekçe zorunludur.");
        }

        var alreadyRescheduled = (await reader.DeliveriesOfOrderAsync(order.Id, cancellationToken)).Any(d => d.RescheduledFromId == deliveryId);
        var rule = FulfillmentTransitions.CanDeliveryAct(action, delivery.Status, order.Status, alreadyRescheduled);
        if (!rule.Allowed)
        {
            return RecordResult<DeliveryView>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        if (action == DeliveryAction.Reschedule)
        {
            return await RescheduleAsync(order, delivery, input, reason!, cancellationToken);
        }

        var values = new JsonObject();
        string kind;
        Func<OrdersStoreContext, OrderDelivery, CancellationToken, Task> effect;
        Func<CancellationToken, Task<IReadOnlyList<DeliveryError>>>? recheck = null;
        switch (action)
        {
            case DeliveryAction.Deliver:
                var receiver = string.IsNullOrWhiteSpace(input.ReceiverName) ? null : input.ReceiverName.Trim();
                if (receiver is null)
                {
                    return Invalid<DeliveryView>("receiverName", "Teslim alan kişi zorunludur.");
                }

                var actualDate = input.ActualDate ?? Today();
                if (actualDate > Today())
                {
                    return Invalid<DeliveryView>("actualDate", "Gerçekleşen teslim tarihi gelecekte olamaz.");
                }

                var plannedLines = await reader.DeliveryLinesAsync(deliveryId, cancellationToken);
                var capacity = await CapacityAsync(order.Id, deliveryId, cancellationToken);
                var actual = input.Lines is { Count: > 0 } given ? given : [.. plannedLines.Select(l => new QuantityRequest(l.OrderLineId, l.PlannedQuantity))];
                var remaining = plannedLines.ToDictionary(l => l.OrderLineId, l => (capacity.Lines.TryGetValue(l.OrderLineId, out var info) ? info.Name : "—", capacity.Lines.TryGetValue(l.OrderLineId, out var info2) ? info2.Remaining : 0m));
                var errors = DeliveryRules.ValidateDeliver(actual, remaining);
                if (errors.Count > 0)
                {
                    return FailFrom<DeliveryView>(errors);
                }

                // Kilit alındıktan sonra kalan miktar yeniden okunup doğrulanır (eşzamanlı teslimler birbirini görür).
                recheck = async token =>
                {
                    var fresh = await CapacityAsync(order.Id, deliveryId, token);
                    var left = (await reader.DeliveryLinesAsync(deliveryId, token)).ToDictionary(
                        l => l.OrderLineId, l => (fresh.Lines.TryGetValue(l.OrderLineId, out var known) ? known.Name : "—", fresh.Lines.TryGetValue(l.OrderLineId, out var knownToo) ? knownToo.Remaining : 0m));
                    return DeliveryRules.ValidateDeliver(actual, left);
                };
                values["status"] = DeliveryStatus.Delivered;
                values["actualDate"] = actualDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                values["receiverName"] = receiver;
                values["receiverNote"] = Normalize(input.ReceiverNote ?? reason);
                values["deliveredAt"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                values["deliveredBy"] = storeContext.CurrentUserId?.ToString();
                kind = FulfillmentEventKinds.Delivered;
                var actualByLine = actual.ToDictionary(a => a.LineId, a => a.Quantity);
                effect = async (ctx, d, token) =>
                {
                    // Teslim edilen miktar teslimat satırına ve sipariş kalemine aynı kaydetmede yazılır; kalem sürüm belirteci ve CHECK eşzamanlı fazla teslimatı reddeder.
                    var dls = await reader.DeliveryLinesAsync(d.Id, token);
                    var orderLines = (await reader.LinesAsync(d.OrderId, token)).ToDictionary(l => l.Id);
                    foreach (var dl in dls)
                    {
                        var quantity = actualByLine.GetValueOrDefault(dl.OrderLineId);
                        dl.DeliveredQuantity = quantity;
                        dl.Touch();
                        if (quantity > 0m)
                        {
                            var ol = orderLines[dl.OrderLineId];
                            ol.DeliveredQuantity += quantity;
                            ol.Touch();
                        }
                    }

                    OrderHistory.Add(ctx, order, kind, $"{d.Number}: teslim alan {receiver}");
                };
                break;
            case DeliveryAction.Fail:
                values["status"] = DeliveryStatus.Failed;
                values["failureReason"] = reason;
                kind = FulfillmentEventKinds.DeliveryFailed;
                effect = (ctx, d, token) =>
                {
                    OrderHistory.Add(ctx, order, kind, $"{d.Number}: {reason}");
                    return Task.CompletedTask;
                };
                break;
            default:
                values["status"] = DeliveryStatus.Cancelled;
                values["failureReason"] = reason;
                kind = FulfillmentEventKinds.DeliveryCancelled;
                effect = (ctx, d, token) =>
                {
                    OrderHistory.Add(ctx, order, kind, $"{d.Number}: {reason}");
                    return Task.CompletedTask;
                };
                break;
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(order.Id, ct);
            if (recheck is not null && await recheck(ct) is { Count: > 0 } staleDeliver)
            {
                return FailFrom<DeliveryView>(staleDeliver);
            }

            using (gate.Open([Permission], kind, reason, deliveryEffect: effect))
            {
                var updated = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Delivery, deliveryId, values, version) { Reason = $"delivery.{kind}" }, ct);
                if (!updated.IsSuccess)
                {
                    return Fail<DeliveryView>(updated.Error!);
                }
            }

            var synced = await SyncAsync(order.Id, ct);
            return synced is not null ? Fail<DeliveryView>(synced) : RecordResult<DeliveryView>.Ok(await DeliveryViewAsync(deliveryId, ct));
        }, cancellationToken);
    }

    /// <summary>Başarısız teslimatı yeniden planlar: yeni teslimat önceki kayda bağlanır, gerekçe saklanır; önceki başarısız kayıt silinmez.</summary>
    private async Task<RecordResult<DeliveryView>> RescheduleAsync(SalesOrder order, OrderDelivery failed, DeliveryActionInput input, string reason, CancellationToken cancellationToken)
    {
        if (input.PlannedDate is not { } plannedDate)
        {
            return Invalid<DeliveryView>("plannedDate", "Yeniden planlama için yeni teslim tarihi zorunludur.");
        }

        var plannedLines = await reader.DeliveryLinesAsync(failed.Id, cancellationToken);
        var capacity = await CapacityAsync(order.Id, null, cancellationToken);
        var requested = plannedLines.Select(l => new QuantityRequest(l.OrderLineId, l.PlannedQuantity)).ToList();
        var errors = DeliveryRules.ValidatePlan(requested, capacity.Lines, capacity.PlannedElsewhere);
        if (errors.Count > 0)
        {
            return FailFrom<DeliveryView>(errors);
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(order.Id, ct);
            var freshResched = await CapacityAsync(order.Id, null, ct);
            if (DeliveryRules.ValidatePlan(requested, freshResched.Lines, freshResched.PlannedElsewhere) is { Count: > 0 } staleResched)
            {
                return FailFrom<DeliveryView>(staleResched);
            }

            var created = await CreateDeliveryAsync(order, plannedDate, requested, failed.JobId, failed.Notes, failed.Id, reason, FulfillmentEventKinds.DeliveryRescheduled, ct);
            if (!created.IsSuccess)
            {
                return Fail<DeliveryView>(created.Error!);
            }

            var synced = await SyncAsync(order.Id, ct);
            return synced is not null ? Fail<DeliveryView>(synced) : RecordResult<DeliveryView>.Ok(await DeliveryViewAsync(created.Value!.Id, ct));
        }, cancellationToken);
    }

    private async Task<RecordResult<RecordData>> CreateDeliveryAsync(
        SalesOrder order, DateOnly plannedDate, IReadOnlyList<QuantityRequest> lines, Guid? jobId, string? notes, Guid? rescheduledFrom, string? rescheduleReason, string kind,
        CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["orderId"] = order.Id.ToString(),
            ["jobId"] = jobId?.ToString(),
            ["status"] = DeliveryStatus.Planned,
            ["plannedDate"] = plannedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["notes"] = Normalize(notes),
            ["rescheduledFromId"] = rescheduledFrom?.ToString(),
            ["rescheduleReason"] = rescheduleReason,
        };
        using var _ = gate.Open([Permission], kind, rescheduleReason, deliveryEffect: (ctx, d, token) =>
        {
            foreach (var line in lines)
            {
                ctx.Data.Set<OrderDeliveryLine>().Add(new OrderDeliveryLine { DeliveryId = d.Id, OrderId = order.Id, OrderLineId = line.LineId, PlannedQuantity = line.Quantity });
            }

            OrderHistory.Add(ctx, order, kind, $"{d.Number}: {plannedDate:dd.MM.yyyy}" + (rescheduleReason is null ? string.Empty : $" · {rescheduleReason}"));
            return Task.CompletedTask;
        });
        return await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.Delivery, null, values) { Reason = $"delivery.{kind}" }, cancellationToken);
    }

    /// <summary>
    /// Kalem kapasitesi: ürün kalemlerinin kalan miktarı (etkin − teslim edilen) ve (<paramref name="excludeDelivery"/> dışındaki) açık teslimatlarda planlanan miktar.
    /// </summary>
    private async Task<(Dictionary<Guid, (string Name, string Kind, decimal Remaining)> Lines, Dictionary<Guid, decimal> PlannedElsewhere)> CapacityAsync(
        Guid orderId, Guid? excludeDelivery, CancellationToken cancellationToken)
    {
        var lines = (await reader.LinesAsync(orderId, cancellationToken)).Where(l => !l.IsCancelled && l.EffectiveQuantity > 0m)
            .ToDictionary(l => l.Id, l => (l.Name, l.Kind, Remaining: Math.Max(0m, l.EffectiveQuantity - l.DeliveredQuantity)));
        var deliveries = await reader.DeliveriesOfOrderAsync(orderId, cancellationToken);
        var open = deliveries.Where(d => d.Status == DeliveryStatus.Planned && d.Id != excludeDelivery).Select(d => d.Id).ToHashSet();
        var plannedElsewhere = (await reader.DeliveryLinesOfOrderAsync(orderId, cancellationToken)).Where(l => open.Contains(l.DeliveryId))
            .GroupBy(l => l.OrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.PlannedQuantity));
        return (lines, plannedElsewhere);
    }

    // ---- Kanıt

    public async Task<RecordResult<DeliveryView>> AddEvidenceAsync(Guid deliveryId, string? fileName, string? contentType, byte[] content, string? description, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Delivery, deliveryId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<DeliveryView>(loaded.Error!);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(Permission))
        {
            return RecordResult<DeliveryView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{Permission}' izni gerekir.");
        }

        var delivery = (await reader.DeliveryAsync(deliveryId, cancellationToken))!;
        if (delivery.Status == DeliveryStatus.Cancelled)
        {
            return Invalid<DeliveryView>("deliveryId", "İptal edilmiş teslimata kanıt eklenemez.");
        }

        if (EvidenceRules.Validate(fileName, contentType, content) is { } problem)
        {
            return Invalid<DeliveryView>("file", problem);
        }

        if (await reader.EvidenceCountAsync(deliveryId, cancellationToken) >= OrderDeliveryEvidence.MaxPerDelivery)
        {
            return Invalid<DeliveryView>("file", $"Bir teslimata en fazla {OrderDeliveryEvidence.MaxPerDelivery} kanıt eklenebilir.");
        }

        var order = (await reader.OrderAsync(delivery.OrderId, cancellationToken))!;
        var type = contentType!.Split(';')[0].Trim().ToLowerInvariant();
        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(delivery.OrderId, ct);
            storeContext.Data.Set<OrderDeliveryEvidence>().Add(new OrderDeliveryEvidence
            {
                DeliveryId = deliveryId,
                OrderId = delivery.OrderId,
                FileName = fileName!.Trim(),
                ContentType = type,
                SizeBytes = content.Length,
                Sha256 = EvidenceRules.Sha256(content),
                Description = Normalize(description),
                Content = content,
                UploadedBy = storeContext.CurrentUserId,
            });
            // Teslimat kaydı sürüm denetimiyle dokunulur; kanıt eklendi bilgisi sipariş tarihçesine yazılır (dosya içeriği ya da adı günlüğe yazılmaz).
            using var _ = gate.Open([Permission], FulfillmentEventKinds.EvidenceAdded, null, deliveryEffect: (ctx, d, token) =>
            {
                d.Touch();
                OrderHistory.Add(ctx, order, FulfillmentEventKinds.EvidenceAdded, $"{d.Number}: teslim kanıtı eklendi");
                return Task.CompletedTask;
            });
            var touched = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Delivery, deliveryId, new JsonObject(), delivery.Version) { Reason = "delivery.EvidenceAdded" }, ct);
            return touched.IsSuccess ? RecordResult<DeliveryView>.Ok(await DeliveryViewAsync(deliveryId, ct)) : Fail<DeliveryView>(touched.Error!);
        }, cancellationToken);
    }

    public async Task<RecordResult<EvidenceFile>> GetEvidenceAsync(Guid deliveryId, Guid evidenceId, CancellationToken cancellationToken)
    {
        // Görünürlük (izin + veri kapsamı) teslimat kaydı üzerinden doğrulanır; görünmeyen kayıt "bulunamadı".
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Delivery, deliveryId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<EvidenceFile>(loaded.Error!);
        }

        return await reader.EvidenceAsync(deliveryId, evidenceId, cancellationToken) is { } file
            ? RecordResult<EvidenceFile>.Ok(new EvidenceFile(file.FileName, file.ContentType, file.Content, file.Sha256))
            : RecordResult<EvidenceFile>.Fail(RecordErrorCode.NotFound, "Teslim kanıtı bulunamadı.");
    }

    // ---- Hizmet başlangıcı

    public async Task<RecordResult<ServiceLineView>> PlanServiceAsync(Guid orderId, Guid orderLineId, DateOnly? plannedStartDate, CancellationToken cancellationToken)
    {
        var (order, error) = await RequireAsync<ServiceLineView>(orderId, cancellationToken);
        if (order is null)
        {
            return error!;
        }

        if (order.Status != OrderStatus.Approved)
        {
            return Invalid<ServiceLineView>("orderId", "Hizmet başlangıcı yalnızca onaylanmış sipariş için planlanır.");
        }

        var line = (await reader.LinesAsync(orderId, cancellationToken)).FirstOrDefault(l => l.Id == orderLineId && !l.IsCancelled && l.EffectiveQuantity > 0m);
        if (line is null || LineKinds.IsDeliverable(line.Kind))
        {
            return Invalid<ServiceLineView>("orderLineId", "Hizmet kalemi bu siparişte bulunamadı; ürün kalemleri teslimatla izlenir.");
        }

        if ((await reader.ServiceStartsOfOrderAsync(orderId, cancellationToken)).Any(s => s.OrderLineId == orderLineId))
        {
            return RecordResult<ServiceLineView>.Fail(RecordErrorCode.Conflict, "Bu hizmet kaleminin başlangıç kaydı zaten var.");
        }

        var values = new JsonObject
        {
            ["orderId"] = orderId.ToString(),
            ["orderLineId"] = orderLineId.ToString(),
            ["status"] = ServiceStatus.Planned,
            ["plannedStartDate"] = plannedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(orderId, ct);
            OrderHistory.Add(storeContext, order, FulfillmentEventKinds.ServicePlanned, $"{line.Name}" + (plannedStartDate is { } d ? $": {d:dd.MM.yyyy}" : string.Empty));
            using (gate.Open([Permission], FulfillmentEventKinds.ServicePlanned, null))
            {
                var created = await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.ServiceStart, null, values) { Reason = "service.Planned" }, ct);
                if (!created.IsSuccess)
                {
                    return Fail<ServiceLineView>(created.Error!);
                }
            }

            var synced = await SyncAsync(orderId, ct);
            return synced is not null ? Fail<ServiceLineView>(synced) : RecordResult<ServiceLineView>.Ok(await ServiceLineAsync(orderId, orderLineId, ct));
        }, cancellationToken);
    }

    public async Task<RecordResult<ServiceLineView>> RunServiceAsync(Guid serviceStartId, string action, long version, ServiceActionInput input, CancellationToken cancellationToken)
    {
        if (!ServiceAction.All.Contains(action))
        {
            return Invalid<ServiceLineView>("action", "Bilinmeyen eylem.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.ServiceStart, serviceStartId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<ServiceLineView>(loaded.Error!);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(Permission))
        {
            return RecordResult<ServiceLineView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{Permission}' izni gerekir.");
        }

        var start = (await reader.ServiceStartAsync(serviceStartId, cancellationToken))!;
        var order = (await reader.OrderAsync(start.OrderId, cancellationToken))!;
        var line = (await reader.LinesAsync(order.Id, cancellationToken)).First(l => l.Id == start.OrderLineId);
        var rule = FulfillmentTransitions.CanServiceAct(action, start.Status);
        if (!rule.Allowed)
        {
            return RecordResult<ServiceLineView>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        var now = time.GetUtcNow();
        var values = new JsonObject();
        string kind;
        switch (action)
        {
            case ServiceAction.Start:
                var blockers = await ServiceBlockersAsync(order, await reader.HandoverOfOrderAsync(order.Id, cancellationToken), line, cancellationToken);
                if (blockers.Count > 0)
                {
                    return RecordResult<ServiceLineView>.Fail(RecordErrorCode.Conflict, "Hizmet başlangıcı kaydedilemedi: " + string.Join(" ", blockers),
                        [.. blockers.Select(b => new RecordFieldError("blockers", b))]);
                }

                values["status"] = ServiceStatus.Started;
                values["actualStartAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                values["startedBy"] = storeContext.CurrentUserId?.ToString();
                kind = FulfillmentEventKinds.ServiceStarted;
                break;
            case ServiceAction.Confirm:
                var by = Normalize(input.ConfirmedBy);
                if (by is null)
                {
                    return Invalid<ServiceLineView>("confirmedBy", "Başlangıcı onaylayan müşteri tarafı kişi zorunludur.");
                }

                values["customerConfirmedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                values["customerConfirmedBy"] = by;
                values["confirmationNote"] = Normalize(input.Note);
                kind = FulfillmentEventKinds.ServiceConfirmed;
                break;
            default:
                values["status"] = ServiceStatus.Completed;
                values["completedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                values["completedBy"] = storeContext.CurrentUserId?.ToString();
                kind = FulfillmentEventKinds.ServiceCompleted;
                break;
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await LockOrderAsync(order.Id, ct);
            OrderHistory.Add(storeContext, order, kind, line.Name + (action == ServiceAction.Confirm ? $": {values["customerConfirmedBy"]}" : string.Empty));
            using (gate.Open([Permission], kind, null))
            {
                var updated = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.ServiceStart, serviceStartId, values, version) { Reason = $"service.{kind}" }, ct);
                if (!updated.IsSuccess)
                {
                    return Fail<ServiceLineView>(updated.Error!);
                }
            }

            var synced = await SyncAsync(order.Id, ct);
            return synced is not null ? Fail<ServiceLineView>(synced) : RecordResult<ServiceLineView>.Ok(await ServiceLineAsync(order.Id, line.Id, ct));
        }, cancellationToken);
    }

    /// <summary>Hizmet başlangıcını engelleyen eksikler (sipariş, devir, bağımlı iş, ön koşul görevleri).</summary>
    private async Task<IReadOnlyList<string>> ServiceBlockersAsync(SalesOrder order, OrderHandover? handover, SalesOrderLine line, CancellationToken cancellationToken)
    {
        var openDependencies = new List<string>();
        var undone = new List<string>();
        if (await reader.OpenJobOfLineAsync(line.Id, cancellationToken) is { } job)
        {
            foreach (var edge in (await reader.JobEdgesAsync(order.Id, cancellationToken)).Where(e => e.JobId == job.Id))
            {
                if (await reader.JobAsync(edge.DependsOnJobId, cancellationToken) is { Status: not JobStatus.Completed } dependency)
                {
                    openDependencies.Add($"{dependency.Number} ({dependency.Title})");
                }
            }

            undone.AddRange((await reader.JobTasksAsync(job.Id, cancellationToken)).Where(t => !t.IsDone && OperationTemplates.ServicePrerequisites.Contains(t.Title)).Select(t => t.Title));
        }

        return FulfillmentTransitions.ServiceStartBlockers(order.Status, handover?.Status ?? HandoverStatus.NotStarted, openDependencies, undone);
    }

    // ---- Ortak

    /// <summary>Siparişin gerçekleşme durumunu kalem teslimatları ve hizmet kayıtlarından türetip değiştiyse aynı işlemde siparişe yazar.</summary>
    private async Task<RecordError?> SyncAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = (await reader.OrderAsync(orderId, cancellationToken))!;
        var lines = (await reader.LinesAsync(orderId, cancellationToken)).Where(l => !l.IsCancelled).ToList();
        var starts = await reader.ServiceStartsOfOrderAsync(orderId, cancellationToken);
        var deliveries = await reader.DeliveriesOfOrderAsync(orderId, cancellationToken);
        var status = FulfillmentCalculator.Compute(
            [.. lines.Select(l => new FulfillmentLine(l.Id, l.Kind, l.EffectiveQuantity, l.DeliveredQuantity, starts.FirstOrDefault(s => s.OrderLineId == l.Id)?.Status))],
            deliveries.Any(d => d.Status == DeliveryStatus.Planned));
        if (status == order.FulfillmentStatus)
        {
            return null;
        }

        using var _ = gate.Open([Permission], FulfillmentEventKinds.FulfillmentChanged, $"{order.FulfillmentStatus} → {status}");
        var updated = await pipeline.UpdateAsync(
            new RecordCommand(OrdersModule.Entities.Order, orderId, new JsonObject { ["fulfillmentStatus"] = status }, order.Version) { Reason = "order.FulfillmentChanged" }, cancellationToken);
        return updated.IsSuccess ? null : updated.Error;
    }

    private async Task<(SalesOrder? Order, RecordResult<T>? Error)> RequireAsync<T>(Guid orderId, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return (null, Fail<T>(loaded.Error!));
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(Permission))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{Permission}' izni gerekir."));
        }

        return (await reader.OrderAsync(orderId, cancellationToken), null);
    }

    private async Task<DeliveryView> DeliveryViewAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = (await reader.DeliveryAsync(deliveryId, cancellationToken))!;
        return (await BuildAsync(delivery.OrderId, cancellationToken)).Deliveries.First(d => d.Id == deliveryId);
    }

    private async Task<ServiceLineView> ServiceLineAsync(Guid orderId, Guid lineId, CancellationToken cancellationToken) =>
        (await BuildAsync(orderId, cancellationToken)).Services.First(s => s.LineId == lineId);

    /// <summary>
    /// Siparişin teslimat/hizmet yazmalarını işlem süresince sıraya koyar (<c>sp_getapplock</c>, işlem kapsamlı) ve izleyiciyi temizler: kilidi bekleyen istek, öncekinin
    /// yazdığı güncel miktarları okuyup yeniden doğrular. Kilit işlemle birlikte bırakılır.
    /// </summary>
    private Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        transactionLock.AcquireAsync($"orders.fulfillment:{orderId:N}", cancellationToken);

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RecordResult<T> Fail<T>(RecordError error) => RecordResult<T>.Fail(error.Code, error.Message, error.FieldErrors);

    private static RecordResult<T> FailFrom<T>(IReadOnlyList<DeliveryError> errors) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, errors[0].Message, [.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);

    private static RecordResult<T> Invalid<T>(string field, string message) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
