using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Sipariş değişiklik talepleri. Talepler yalnızca iş akışı servisiyle (kapı açık) oluşur ve karara bağlanır; genel uçtan yazılamaz ya da
/// silinemez. Görünürlük ve veri kapsamı üst siparişi izler. Tutar etkisi hassas alandır (<c>orders.financials.read</c>).
/// </summary>
internal sealed class OrderChangeRequestRecordStore(OrdersStoreContext context) : OrdersRecordStore<OrderChangeRequest>(context)
{
    public override string EntityKey => OrdersModule.Entities.ChangeRequest;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.OrdersWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<OrderChangeRequest>> Fields { get; } =
    [
        Field.Reference<OrderChangeRequest, SalesOrder>("orderId", "Sipariş bulunamadı.", c => c.OrderId, (c, v) => c.OrderId = v!.Value, required: true, createOnly: true),
        Field.Integer<OrderChangeRequest>("sequence", c => c.Sequence, (c, v) => c.Sequence = v ?? 1).AsSystem(),
        Field.Choice<OrderChangeRequest>("status", ChangeRequestStatus.All, c => c.Status, (c, v) => c.Status = v).AsSystem(),
        Field.Text<OrderChangeRequest>("reason", SalesOrder.ReasonMaxLength, c => c.Reason, (c, v) => c.Reason = v ?? string.Empty, required: true).AsSystem(),
        Field.Text<OrderChangeRequest>("changes", OrderChangeRequest.JsonMaxLength, c => c.Changes, (c, v) => c.Changes = v ?? "{}").AsSystem(),
        Field.Text<OrderChangeRequest>("impact", OrderChangeRequest.JsonMaxLength, c => c.Impact, (c, v) => c.Impact = v ?? "{}").AsSystem(),
        Field.SignedMoney<OrderChangeRequest>("netDelta", c => c.NetDelta, (c, v) => c.NetDelta = v ?? 0m).AsSystem(),
        Field.SignedMoney<OrderChangeRequest>("taxDelta", c => c.TaxDelta, (c, v) => c.TaxDelta = v ?? 0m).AsSystem(),
        Field.Id<OrderChangeRequest>("requestedBy", c => c.RequestedBy, (c, v) => c.RequestedBy = v).AsSystem(),
        Field.Id<OrderChangeRequest>("decidedBy", c => c.DecidedBy, (c, v) => c.DecidedBy = v).AsSystem(),
        Field.Time<OrderChangeRequest>("decidedAt", c => c.DecidedAt, (c, v) => c.DecidedAt = v).AsSystem(),
        Field.Text<OrderChangeRequest>("decisionNote", SalesOrder.ReasonMaxLength, c => c.DecisionNote, (c, v) => c.DecisionNote = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OrderChangeRequest>> Filters { get; } = new Dictionary<string, RecordFilter<OrderChangeRequest>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.OrderId == id) : null,
        ["status"] = (q, v, _) => ChangeRequestStatus.All.Contains(v) ? q.Where(c => c.Status == v) : null,
    };

    protected override IOrderedQueryable<OrderChangeRequest> Order(IQueryable<OrderChangeRequest> source) => source.OrderByDescending(c => c.Sequence);

    protected override IQueryable<OrderChangeRequest> Search(IQueryable<OrderChangeRequest> source, string term) =>
        source.Where(c => c.Reason.Contains(term));

    protected override IQueryable<OrderChangeRequest> ScopeToOwners(IQueryable<OrderChangeRequest> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(c => orders.Any(o => o.Id == c.OrderId && ids.Contains(o.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("orderId", out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var orderId))
        {
            return false;
        }

        var ids = owners.Select(id => (Guid?)id).ToList();
        return await Context.Data.Set<SalesOrder>().AsNoTracking().AnyAsync(o => o.Id == orderId && ids.Contains(o.OwnerId), cancellationToken);
    }

    protected override Task<JsonObject> PrepareNewAsync(OrderChangeRequest entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Değişiklik talebi yalnızca sipariş ekranındaki talep işlemiyle açılır.")]);
        }

        entity.RequestedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override Task AppliedAsync(OrderChangeRequest entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew && !Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("status", "Değişiklik talebi yalnızca karar işlemleriyle değişir.")]);
        }

        return Task.CompletedTask;
    }

    protected override Task BeforeDeleteAsync(OrderChangeRequest entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Değişiklik talebi silinemez; geçmişi korunur. Bekleyen talep geri çekilebilir.");
}
