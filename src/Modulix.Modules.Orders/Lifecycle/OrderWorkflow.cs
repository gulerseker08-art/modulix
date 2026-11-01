using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Bir eylemin kullanıcı için durumu: yapılabilir mi, yapılamıyorsa nedeni. Yetkisi olmayan eylem listede hiç yer almaz.</summary>
public sealed record OrderActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <summary>Tenant ayarlarından gelen sipariş politikası; sağlayıcı yoksa varsayılan.</summary>
public sealed record OrderPolicySettings(bool AllowSelfApproval, int HandoverAcceptDueDays = 2, int HandoverWaitHours = 48)
{
    public static OrderPolicySettings Default { get; } = new(false);
}

/// <summary>Ayarları okur (API host'u tenant ayar servisiyle uygular; modül Core'un ayar servisini doğrudan görmez).</summary>
public interface IOrderPolicyProvider
{
    Task<OrderPolicySettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sipariş ticari durum iş akışı (ADR-0023, dilim 2). Tek yazma yolu: iş akışı kapısı eylemin izniyle açılır ve kayıt boru hattı üzerinden
/// güncelleme yapılır; kapsam, hassas alan, audit, olay ve sürüm denetimi (409) depoda uygulanır. Her eylem kaydın sürümünü ister. Ön koşullar
/// <see cref="OrderTransitions"/> ile arayüzle ortaktır. İptal, kalemlerin teslim edilmemiş kısmını iptal eder ve tüketim defterinden serbest
/// bırakır (teslim edilen miktar ve kayıtları korunur); iptal iade değildir. Bekleyen değişiklik talebi iptalde geri çekilir.
/// </summary>
public interface IOrderWorkflow
{
    Task<RecordResult<IReadOnlyList<OrderActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Eylemi uygular; bilinmeyen eylem 422. Gerekçe isteyen eylemde gerekçe zorunludur.</summary>
    Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken);
}

internal sealed class OrderWorkflow(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrderReader reader,
    OrdersStoreContext storeContext,
    IOrderPolicyProvider? policy = null) : IOrderWorkflow
{
    private const string Key = OrdersModule.Entities.Order;

    private static readonly IReadOnlyDictionary<string, string> PermissionOf = new Dictionary<string, string>
    {
        [OrderAction.SubmitForApproval] = OrdersModule.Permissions.OrdersWrite,
        [OrderAction.WithdrawApproval] = OrdersModule.Permissions.OrdersWrite,
        [OrderAction.Approve] = OrdersModule.Permissions.OrdersApprove,
        [OrderAction.ReturnToDraft] = OrdersModule.Permissions.OrdersApprove,
        [OrderAction.Cancel] = OrdersModule.Permissions.OrdersCancel,
        [OrderAction.Close] = OrdersModule.Permissions.OrdersClose,
        [OrderAction.ForceClose] = OrdersModule.Permissions.OrdersForceClose,
    };

    public async Task<RecordResult<IReadOnlyList<OrderActionState>>> ActionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<OrderActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        var state = await StateOfAsync(record.Value!, id, cancellationToken);
        var list = new List<OrderActionState>();
        foreach (var action in OrderAction.All.Where(a => granted.Contains(PermissionOf[a])))
        {
            var rule = OrderTransitions.Can(action, state);
            if (rule.Allowed && OrderAction.IsDecision(action) && await SelfApprovalBlockedAsync(id, cancellationToken))
            {
                rule = OrderRuleResult.Deny("self-approval", "Kendi onaya gönderdiğiniz siparişi onaylayamazsınız.");
            }

            list.Add(new OrderActionState(action, rule.Allowed, rule.Code, rule.Message, OrderTransitions.RequiresReason(action)));
        }

        return RecordResult<IReadOnlyList<OrderActionState>>.Ok(list);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid id, string action, long version, string? reason, CancellationToken cancellationToken)
    {
        if (!PermissionOf.TryGetValue(action, out var permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (OrderTransitions.RequiresReason(action) && text is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        var rule = OrderTransitions.Can(action, await StateOfAsync(loaded.Value!, id, cancellationToken));
        if (!rule.Allowed)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        if (OrderAction.IsDecision(action) && await SelfApprovalBlockedAsync(id, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Kendi onaya gönderdiğiniz siparişi onaylayamazsınız.",
                [new RecordFieldError("status", "Kendi onaya gönderdiğiniz siparişi onaylayamazsınız.")]);
        }

        var target = OrderTransitions.TargetStatus(action);
        var values = new JsonObject { ["status"] = target, ["statusReason"] = text };
        var kind = action switch
        {
            OrderAction.SubmitForApproval => OrderEventKinds.SubmittedForApproval,
            OrderAction.WithdrawApproval => OrderEventKinds.ApprovalWithdrawn,
            OrderAction.Approve => OrderEventKinds.Approved,
            OrderAction.ReturnToDraft => OrderEventKinds.ReturnedToDraft,
            OrderAction.Cancel => OrderEventKinds.Cancelled,
            OrderAction.Close => OrderEventKinds.Closed,
            _ => OrderEventKinds.ForceClosed,
        };
        Func<OrdersStoreContext, SalesOrder, CancellationToken, Task>? effect = action == OrderAction.Cancel ? CancelEffectAsync : null;

        // Geçiş, kalem iptali ve tüketim defteri serbest bırakma tek kaydetmede yazılır; sürüm çakışmasında hiçbiri yazılmaz.
        using (gate.Open([permission], kind, text, effect))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, values, version) { Reason = $"order.{kind}" }, cancellationToken);
        }
    }

    /// <summary>
    /// İptal etkisi: her kalemin teslim edilmemiş kısmı iptal edilir (teslim edilen miktar ve kayıtları korunur), iptal edilen miktar ve tutar teklif
    /// kalemi tüketim defterinden serbest bırakılır (aynı kalem yeniden siparişe alınabilir), bekleyen değişiklik talebi geri çekilir.
    /// </summary>
    private async Task CancelEffectAsync(OrdersStoreContext context, SalesOrder order, CancellationToken cancellationToken)
    {
        var lines = await reader.LinesAsync(order.Id, cancellationToken);
        var reductions = OrderAmounts.PlanCancellation([.. lines.Select(ToAmounts)]);
        var ledger = await reader.ConsumptionsAsync(order.SourceQuoteId, cancellationToken);
        foreach (var reduction in reductions)
        {
            var line = lines.First(l => l.Id == reduction.LineId);
            line.CancelledQuantity = reduction.NewCancelled;
            line.IsCancelled = reduction.NewCancelled >= line.Quantity;
            line.Touch();
            Release(ledger, line.SourceQuoteLineKey, reduction);
        }

        if (await reader.PendingChangeRequestAsync(order.Id, cancellationToken) is { } pending)
        {
            pending.Status = ChangeRequestStatus.Withdrawn;
            pending.DecidedBy = context.CurrentUserId;
            pending.DecidedAt = DateTimeOffset.UtcNow;
            pending.DecisionNote = "Sipariş iptal edildi.";
            pending.Touch();
        }
    }

    /// <summary>Kalemin tüketim defteri satırından iptal edilen miktar ve tutarı düşer (sürüm belirteci eşzamanlı çakışmayı yakalar).</summary>
    internal static void Release(IEnumerable<QuoteLineConsumption> ledger, Guid sourceLineKey, LineReduction reduction)
    {
        if (ledger.FirstOrDefault(c => c.LineKey == sourceLineKey) is not { } row)
        {
            return;
        }

        row.Touch();
        row.ConvertedQuantity = Math.Max(0m, row.ConvertedQuantity - reduction.ReleasedQuantity);
        row.ConvertedNet = Math.Max(0m, row.ConvertedNet - reduction.ReleasedNet);
        row.ConvertedTax = Math.Max(0m, row.ConvertedTax - reduction.ReleasedTax);
    }

    internal static LineAmounts ToAmounts(SalesOrderLine l) =>
        new(l.Id, l.Name, l.Quantity, l.DeliveredQuantity, l.CancelledQuantity, l.Net, l.Tax, LineKinds.IsService(l.Kind));

    private async Task<OrderState> StateOfAsync(RecordData order, Guid id, CancellationToken cancellationToken) =>
        new(Text(order, "status") ?? OrderStatus.Draft, Text(order, "handoverStatus") ?? HandoverStatus.NotStarted,
            Text(order, "fulfillmentStatus") ?? FulfillmentStatus.NotStarted, await reader.ActiveLineCountAsync(id, cancellationToken));

    /// <summary>Dört göz: onaya gönderen aynı kişi kararı veremez (ayar açıksa serbest).</summary>
    private async Task<bool> SelfApprovalBlockedAsync(Guid id, CancellationToken cancellationToken)
    {
        if ((policy is null ? OrderPolicySettings.Default : await policy.GetAsync(cancellationToken)).AllowSelfApproval)
        {
            return false;
        }

        var submitter = await reader.LastActorAsync(id, OrderEventKinds.SubmittedForApproval, cancellationToken);
        return submitter is not null && submitter == storeContext.CurrentUserId;
    }

    private static string? Text(RecordData data, string field) => data.Values[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
