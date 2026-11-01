using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Değişiklik talebi girişi. Başlık alanı → yeni değer (boş değer temizler); kalem değişikliği yeni etkin miktar ve/veya planlanan tarihtir.</summary>
public sealed record OrderChangeRequestInput(string? Reason, IReadOnlyDictionary<string, string?>? Header, IReadOnlyList<OrderLineChange>? Lines);

/// <summary>Talebin görünümü. Tutar farkı yalnızca tutar görme izni olana verilir.</summary>
public sealed record OrderChangeRequestView(
    Guid Id, long Version, Guid OrderId, int Sequence, string Status, string Reason, OrderChangeSet Changes, IReadOnlyList<string> Operations,
    IReadOnlyList<string> Deliveries, IReadOnlyList<string> Contracts, decimal? NetDelta, decimal? TaxDelta, Guid? RequestedBy, Guid? DecidedBy,
    DateTimeOffset? DecidedAt, string? DecisionNote, DateTimeOffset CreatedAt);

/// <summary>Kaydetmeden etki önizlemesi: etkili değişiklikler ve etki özeti.</summary>
public sealed record OrderChangePreview(
    OrderChangeSet Changes, IReadOnlyList<string> Operations, IReadOnlyList<string> Deliveries, IReadOnlyList<string> Contracts, decimal? NetDelta, decimal? TaxDelta);

/// <summary>
/// Onaylanmış siparişin gerekçeli değişiklik talebi (ADR-0023, dilim 2). Onaylanmış siparişin ticari alanları yerinde değişmez; talep açılır, etkisi
/// (operasyon, teslimat, sözleşme, tutar) gösterilir ve yetkili kişi onaylayınca tek işlemde uygulanır. Kopyalanmış fiyat ve miktar bilgisi
/// değişmez: miktar azaltma iptal edilen miktar olarak ayrı tutulur, teslim edilen miktarın altına inilemez, miktar artırılamaz (ek kapsam yeni siparişle).
/// Sipariş başına aynı anda en fazla bir bekleyen talep vardır. Dört göz: talebi açan kişi onaylayamaz (ayar açıksa serbest).
/// </summary>
public interface IOrderChangeRequests
{
    Task<RecordResult<IReadOnlyList<OrderChangeRequestView>>> ListAsync(Guid orderId, CancellationToken cancellationToken);

    Task<RecordResult<OrderChangePreview>> PreviewAsync(Guid orderId, OrderChangeRequestInput input, CancellationToken cancellationToken);

    Task<RecordResult<OrderChangeRequestView>> CreateAsync(Guid orderId, OrderChangeRequestInput input, CancellationToken cancellationToken);

    /// <summary>Talebi onaylar (uygular) ya da reddeder (gerekçe zorunlu).</summary>
    Task<RecordResult<OrderChangeRequestView>> DecideAsync(Guid requestId, bool approve, long version, string? note, CancellationToken cancellationToken);

    Task<RecordResult<OrderChangeRequestView>> WithdrawAsync(Guid requestId, long version, CancellationToken cancellationToken);
}

internal sealed class OrderChangeRequests(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrderReader reader,
    OrdersStoreContext storeContext,
    IOrderPolicyProvider? policy = null) : IOrderChangeRequests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecordResult<IReadOnlyList<OrderChangeRequestView>>> ListAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!order.IsSuccess)
        {
            return RecordResult<IReadOnlyList<OrderChangeRequestView>>.Fail(order.Error!.Code, order.Error.Message, order.Error.FieldErrors);
        }

        var financials = (await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.FinancialsRead);
        var requests = await reader.ChangeRequestsAsync(orderId, cancellationToken);
        return RecordResult<IReadOnlyList<OrderChangeRequestView>>.Ok([.. requests.Select(r => ToView(r, financials))]);
    }

    public async Task<RecordResult<OrderChangePreview>> PreviewAsync(Guid orderId, OrderChangeRequestInput input, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync<OrderChangePreview>(orderId, input, requirePermission: OrdersModule.Permissions.OrdersWrite, cancellationToken);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var financials = (await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.FinancialsRead);
        var p = prepared.Value!;
        return RecordResult<OrderChangePreview>.Ok(new OrderChangePreview(
            p.Effective, p.Impact.Operations, p.Impact.Deliveries, p.Impact.Contracts, financials ? p.Impact.NetDelta : null, financials ? p.Impact.TaxDelta : null));
    }

    public async Task<RecordResult<OrderChangeRequestView>> CreateAsync(Guid orderId, OrderChangeRequestInput input, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (reason is null)
        {
            return Invalid<OrderChangeRequestView>("reason", "Gerekçe zorunludur.");
        }

        if (reason.Length > SalesOrder.ReasonMaxLength)
        {
            return Invalid<OrderChangeRequestView>("reason", $"Gerekçe en fazla {SalesOrder.ReasonMaxLength} karakter olabilir.");
        }

        var prepared = await PrepareAsync<OrderChangeRequestView>(orderId, input, OrdersModule.Permissions.OrdersWrite, cancellationToken);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        if (await reader.PendingChangeRequestAsync(orderId, cancellationToken) is { } pending)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.Conflict,
                $"Bu siparişin karar bekleyen #{pending.Sequence} numaralı değişiklik talebi var; önce karara bağlanmalı ya da geri çekilmeli.");
        }

        var p = prepared.Value!;
        var sequence = (await reader.ChangeRequestsAsync(orderId, cancellationToken)).Select(c => c.Sequence).DefaultIfEmpty(0).Max() + 1;
        var values = new JsonObject
        {
            ["orderId"] = orderId.ToString(),
            ["sequence"] = sequence,
            ["status"] = ChangeRequestStatus.Pending,
            ["reason"] = reason,
            ["changes"] = JsonSerializer.Serialize(Stored(p.Effective), Json),
            ["impact"] = JsonSerializer.Serialize(new StoredImpact(p.Impact.Operations, p.Impact.Deliveries, p.Impact.Contracts), Json),
            ["netDelta"] = p.Impact.NetDelta,
            ["taxDelta"] = p.Impact.TaxDelta,
        };
        var financials = (await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.FinancialsRead);
        return await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([OrdersModule.Permissions.OrdersWrite], OrderEventKinds.ChangeRequested, reason);
            // Tarihçe satırı talep kaydıyla aynı kaydetmede yazılır.
            OrderHistory.Add(storeContext, p.Order, OrderEventKinds.ChangeRequested, $"Talep #{sequence}: {reason}");
            var created = await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.ChangeRequest, null, values) { Reason = "order.ChangeRequested" }, ct);
            if (!created.IsSuccess)
            {
                return RecordResult<OrderChangeRequestView>.Fail(created.Error!.Code, created.Error.Message, created.Error.FieldErrors);
            }

            var stored = await reader.ChangeRequestAsync(created.Value!.Id, ct);
            return RecordResult<OrderChangeRequestView>.Ok(ToView(stored!, financials));
        }, cancellationToken);
    }

    public async Task<RecordResult<OrderChangeRequestView>> DecideAsync(Guid requestId, bool approve, long version, string? note, CancellationToken cancellationToken)
    {
        var request = await reader.ChangeRequestAsync(requestId, cancellationToken);
        if (request is null || !(await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, request.OrderId), cancellationToken)).IsSuccess)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.NotFound, "Değişiklik talebi bulunamadı.");
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        if (!granted.Contains(OrdersModule.Permissions.OrdersApprove))
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{OrdersModule.Permissions.OrdersApprove}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (!approve && text is null)
        {
            return Invalid<OrderChangeRequestView>("note", "Reddetmek için gerekçe zorunludur.");
        }

        if (request.Status != ChangeRequestStatus.Pending)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.Conflict, "Bu talep zaten karara bağlanmış ya da geri çekilmiş.");
        }

        if (request.RequestedBy is { } requester && requester == storeContext.CurrentUserId
            && !(policy is null ? OrderPolicySettings.Default : await policy.GetAsync(cancellationToken)).AllowSelfApproval)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.ValidationFailed, "Kendi açtığınız değişiklik talebini karara bağlayamazsınız.",
                [new RecordFieldError("status", "Kendi açtığınız değişiklik talebini karara bağlayamazsınız.")]);
        }

        var financials = granted.Contains(OrdersModule.Permissions.FinancialsRead);
        var order = (await reader.OrderAsync(request.OrderId, cancellationToken))!;
        return await pipeline.RunAtomicAsync(async ct =>
        {
            var kind = approve ? OrderEventKinds.ChangeApplied : OrderEventKinds.ChangeRejected;
            using var _ = gate.Open([OrdersModule.Permissions.OrdersApprove], kind, $"Talep #{request.Sequence}" + (text is null ? string.Empty : $": {text}"));
            if (approve)
            {
                var applied = await ApplyAsync(request, order, ct);
                if (applied is not null)
                {
                    return applied;
                }
            }
            else
            {
                OrderHistory.Add(storeContext, order, kind, $"Talep #{request.Sequence}: {text}");
            }

            var update = new JsonObject
            {
                ["status"] = approve ? ChangeRequestStatus.Approved : ChangeRequestStatus.Rejected,
                ["decidedBy"] = storeContext.CurrentUserId?.ToString(),
                ["decidedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["decisionNote"] = text,
            };
            var decided = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.ChangeRequest, requestId, update, version) { Reason = $"order.{kind}" }, ct);
            if (!decided.IsSuccess)
            {
                return RecordResult<OrderChangeRequestView>.Fail(decided.Error!.Code, decided.Error.Message, decided.Error.FieldErrors);
            }

            return RecordResult<OrderChangeRequestView>.Ok(ToView((await reader.ChangeRequestAsync(requestId, ct))!, financials));
        }, cancellationToken);
    }

    public async Task<RecordResult<OrderChangeRequestView>> WithdrawAsync(Guid requestId, long version, CancellationToken cancellationToken)
    {
        var request = await reader.ChangeRequestAsync(requestId, cancellationToken);
        if (request is null || !(await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, request.OrderId), cancellationToken)).IsSuccess)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.NotFound, "Değişiklik talebi bulunamadı.");
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        if (!granted.Contains(OrdersModule.Permissions.OrdersWrite))
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{OrdersModule.Permissions.OrdersWrite}' izni gerekir.");
        }

        if (request.Status != ChangeRequestStatus.Pending)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.Conflict, "Bu talep zaten karara bağlanmış ya da geri çekilmiş.");
        }

        var financials = granted.Contains(OrdersModule.Permissions.FinancialsRead);
        var order = (await reader.OrderAsync(request.OrderId, cancellationToken))!;
        return await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([OrdersModule.Permissions.OrdersWrite], OrderEventKinds.ChangeWithdrawn, $"Talep #{request.Sequence}");
            OrderHistory.Add(storeContext, order, OrderEventKinds.ChangeWithdrawn, $"Talep #{request.Sequence} geri çekildi.");
            var update = new JsonObject
            {
                ["status"] = ChangeRequestStatus.Withdrawn,
                ["decidedBy"] = storeContext.CurrentUserId?.ToString(),
                ["decidedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            var result = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.ChangeRequest, requestId, update, version) { Reason = "order.ChangeWithdrawn" }, ct);
            return result.IsSuccess
                ? RecordResult<OrderChangeRequestView>.Ok(ToView((await reader.ChangeRequestAsync(requestId, ct))!, financials))
                : RecordResult<OrderChangeRequestView>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
        }, cancellationToken);
    }

    /// <summary>
    /// Onaylanan talebi uygular: kalem miktarı azaltma ve planlanan tarih (kalem kayıtları boru hattından), tüketim defterinden serbest bırakma, sipariş
    /// başlığı ve toplamları. Talep o günden bu yana uygulanamaz hale geldiyse (sipariş durumu, teslim edilen miktar) hata döner ve hiçbir şey yazılmaz.
    /// </summary>
    private async Task<RecordResult<OrderChangeRequestView>?> ApplyAsync(OrderChangeRequest request, SalesOrder order, CancellationToken cancellationToken)
    {
        var lines = await reader.LinesAsync(order.Id, cancellationToken);
        var amounts = lines.Select(OrderWorkflow.ToAmounts).ToList();
        var set = Parse(request.Changes);
        var state = HeaderState(order);
        var errors = OrderChangeRules.Validate(set, state, amounts);
        if (errors.Count > 0)
        {
            return RecordResult<OrderChangeRequestView>.Fail(RecordErrorCode.Conflict, "Talep artık uygulanamaz: " + errors[0].Message,
                [.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);
        }

        var ledger = await reader.ConsumptionsAsync(order.SourceQuoteId, cancellationToken);
        var after = amounts.ToDictionary(a => a.LineId);
        foreach (var change in set.Lines)
        {
            var line = lines.First(l => l.Id == change.LineId);
            var values = new JsonObject();
            if (change.Quantity is { } quantity)
            {
                var reduction = OrderAmounts.Reduce(after[line.Id], quantity);
                values["cancelledQuantity"] = reduction.NewCancelled;
                values["isCancelled"] = reduction.NewCancelled >= line.Quantity;
                OrderWorkflow.Release(ledger, line.SourceQuoteLineKey, reduction);
                after[line.Id] = after[line.Id] with { Cancelled = reduction.NewCancelled };
            }

            if (change.PlannedDate is { } planned)
            {
                values["plannedDate"] = planned.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            var updated = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.OrderLine, line.Id, values, line.Version) { Reason = "order.ChangeApplied" }, cancellationToken);
            if (!updated.IsSuccess)
            {
                return RecordResult<OrderChangeRequestView>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
            }
        }

        var header = new JsonObject();
        foreach (var (field, value) in set.Header)
        {
            header[field] = value;
        }

        if (set.Lines.Any(l => l.Quantity is not null))
        {
            var (net, tax) = OrderAmounts.Totals(after.Values);
            header["netTotal"] = net;
            header["taxTotal"] = tax;
            header["grossTotal"] = net + tax;
        }

        var result = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Order, order.Id, header, order.Version) { Reason = "order.ChangeApplied" }, cancellationToken);
        return result.IsSuccess
            ? null
            : RecordResult<OrderChangeRequestView>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    private sealed record Prepared(SalesOrder Order, OrderChangeSet Effective, OrderChangeImpact Impact);

    private sealed record PreparedResult<T>(Prepared? Value, RecordResult<T>? Error);

    /// <summary>Sipariş görünürlüğü ve izin, etkisiz değişikliklerin ayıklanması, doğrulama ve etki hesabı (yazmaz).</summary>
    private async Task<PreparedResult<T>> PrepareAsync<T>(Guid orderId, OrderChangeRequestInput input, string requirePermission, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return new(null, RecordResult<T>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors));
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(requirePermission))
        {
            return new(null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{requirePermission}' izni gerekir."));
        }

        var order = (await reader.OrderAsync(orderId, cancellationToken))!;
        var lines = await reader.LinesAsync(orderId, cancellationToken);
        var amounts = lines.Select(OrderWorkflow.ToAmounts).ToList();
        var state = HeaderState(order);
        var requested = new OrderChangeSet(input.Header ?? new Dictionary<string, string?>(), input.Lines ?? []);
        var effective = OrderChangeRules.Effective(requested, state, amounts, lines.ToDictionary(l => l.Id, l => l.PlannedDate));
        var errors = OrderChangeRules.Validate(effective, state, amounts);
        if (errors.Count > 0)
        {
            return new(null, RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, errors[0].Message, [.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]));
        }

        return new(new Prepared(order, effective, OrderChangeRules.Impact(effective, state, amounts)), null);
    }

    private static OrderHeaderState HeaderState(SalesOrder o) => new(
        o.Status, o.HandoverStatus, o.FulfillmentStatus, o.ContractNumber,
        new Dictionary<string, string?>
        {
            ["deliveryAddress"] = o.DeliveryAddress,
            ["location"] = o.Location,
            ["plannedStartDate"] = o.PlannedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["plannedDeliveryDate"] = o.PlannedDeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["paymentTerms"] = o.PaymentTerms,
            ["deliveryTerms"] = o.DeliveryTerms,
            ["commercialTerms"] = o.CommercialTerms,
            ["commitments"] = o.Commitments,
            ["operationsNotes"] = o.OperationsNotes,
        });

    private sealed record StoredChanges(Dictionary<string, string?> Header, List<StoredLineChange> Lines);

    private sealed record StoredLineChange(Guid LineId, decimal? Quantity, string? PlannedDate);

    private sealed record StoredImpact(IReadOnlyList<string> Operations, IReadOnlyList<string> Deliveries, IReadOnlyList<string> Contracts);

    private static StoredChanges Stored(OrderChangeSet set) => new(
        new Dictionary<string, string?>(set.Header),
        [.. set.Lines.Select(l => new StoredLineChange(l.LineId, l.Quantity, l.PlannedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))]);

    private static OrderChangeSet Parse(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredChanges>(json, Json) ?? new StoredChanges([], []);
        return new OrderChangeSet(
            stored.Header,
            [.. stored.Lines.Select(l => new OrderLineChange(l.LineId, l.Quantity, l.PlannedDate is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null))]);
    }

    private static OrderChangeRequestView ToView(OrderChangeRequest r, bool financials)
    {
        var impact = JsonSerializer.Deserialize<StoredImpact>(r.Impact, Json) ?? new StoredImpact([], [], []);
        return new OrderChangeRequestView(
            r.Id, r.Version, r.OrderId, r.Sequence, r.Status, r.Reason, Parse(r.Changes), impact.Operations, impact.Deliveries, impact.Contracts,
            financials ? r.NetDelta : null, financials ? r.TaxDelta : null, r.RequestedBy, r.DecidedBy, r.DecidedAt, r.DecisionNote, r.CreatedAt);
    }

    private static RecordResult<T> Invalid<T>(string field, string message) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
