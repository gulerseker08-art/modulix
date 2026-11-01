using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Sipariş kalemleri. Kalemler yalnızca tekliften sipariş oluşturulurken sunucu tarafından yazılır; ürün, miktar ve tutarlar teklif
/// kaleminin kopyasıdır ve genel uçlardan değiştirilemez ya da silinemez. Yalnızca planlanan tarih ve açıklama düzenlenir. Görünürlük ve
/// veri kapsamı üst siparişi izler. Fiyat ve tutarlar hassas alandır (<c>orders.financials.read</c>).
/// </summary>
internal sealed class OrderLineRecordStore(OrdersStoreContext context) : OrdersRecordStore<SalesOrderLine>(context)
{
    public override string EntityKey => OrdersModule.Entities.OrderLine;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.OrdersWrite;

    protected override string CreatePermission => OrdersModule.Permissions.OrdersCreate;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<SalesOrderLine>> Fields { get; } =
    [
        Field.Reference<SalesOrderLine, SalesOrder>("orderId", "Sipariş bulunamadı.", l => l.OrderId, (l, v) => l.OrderId = v!.Value, required: true, createOnly: true),
        Field.Integer<SalesOrderLine>("position", l => l.Position, (l, v) => l.Position = v ?? 0, max: 9999).AsSystem(),
        Field.Choice<SalesOrderLine>("kind", LineKinds.All, l => l.Kind, (l, v) => l.Kind = v).AsSystem(),
        Field.Id<SalesOrderLine>("parentSourceLineKey", l => l.ParentSourceLineKey, (l, v) => l.ParentSourceLineKey = v).AsSystem(),
        Field.Text<SalesOrderLine>("groupName", SalesOrderLine.NameMaxLength, l => l.GroupName, (l, v) => l.GroupName = v).AsSystem(),
        Field.Id<SalesOrderLine>("productId", l => l.ProductId, (l, v) => l.ProductId = v).AsSystem(),
        Field.Text<SalesOrderLine>("productCode", SalesOrderLine.CodeMaxLength, l => l.ProductCode, (l, v) => l.ProductCode = v).AsSystem(),
        Field.Text<SalesOrderLine>("name", SalesOrderLine.NameMaxLength, l => l.Name, (l, v) => l.Name = v!, required: true).AsSystem(),
        Field.Text<SalesOrderLine>("description", SalesOrderLine.TextMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Quantity<SalesOrderLine>("quantity", l => l.Quantity, (l, v) => l.Quantity = v ?? 1m).AsSystem(),
        Field.Quantity<SalesOrderLine>("acceptedQuantity", l => l.AcceptedQuantity, (l, v) => l.AcceptedQuantity = v ?? 1m).AsSystem(),
        Field.Text<SalesOrderLine>("unitName", SalesOrderLine.UnitMaxLength, l => l.UnitName, (l, v) => l.UnitName = v).AsSystem(),
        Field.Quantity<SalesOrderLine>("unitPrice", l => l.UnitPrice, (l, v) => l.UnitPrice = v ?? 0m).AsSystem(),
        Field.Money<SalesOrderLine>("listAmount", l => l.ListAmount, (l, v) => l.ListAmount = v ?? 0m).AsSystem(),
        Field.Money<SalesOrderLine>("lineDiscount", l => l.LineDiscount, (l, v) => l.LineDiscount = v ?? 0m).AsSystem(),
        Field.Rate<SalesOrderLine>("taxRatePercent", l => l.TaxRatePercent, (l, v) => l.TaxRatePercent = v ?? 0m).AsSystem(),
        Field.Money<SalesOrderLine>("net", l => l.Net, (l, v) => l.Net = v ?? 0m).AsSystem(),
        Field.Money<SalesOrderLine>("tax", l => l.Tax, (l, v) => l.Tax = v ?? 0m).AsSystem(),
        Field.Money<SalesOrderLine>("gross", l => l.Gross, (l, v) => l.Gross = v ?? 0m).AsSystem(),
        Field.Text<SalesOrderLine>("recurringPeriod", SalesOrderLine.KindMaxLength, l => l.RecurringPeriod, (l, v) => l.RecurringPeriod = v).AsSystem(),
        Field.Id<SalesOrderLine>("sourceQuoteId", l => l.SourceQuoteId == Guid.Empty ? null : l.SourceQuoteId, (l, v) => l.SourceQuoteId = v ?? Guid.Empty).AsSystem(),
        Field.Id<SalesOrderLine>("sourceQuoteLineKey", l => l.SourceQuoteLineKey == Guid.Empty ? null : l.SourceQuoteLineKey, (l, v) => l.SourceQuoteLineKey = v ?? Guid.Empty).AsSystem(),
        Field.Quantity<SalesOrderLine>("deliveredQuantity", l => l.DeliveredQuantity, (l, v) => l.DeliveredQuantity = v ?? 0m).AsSystem(),
        Field.Quantity<SalesOrderLine>("cancelledQuantity", l => l.CancelledQuantity, (l, v) => l.CancelledQuantity = v ?? 0m).AsSystem(),
        Field.Flag<SalesOrderLine>("isCancelled", l => l.IsCancelled, (l, v) => l.IsCancelled = v).AsSystem(),
        Field.Date<SalesOrderLine>("plannedDate", l => l.PlannedDate, (l, v) => l.PlannedDate = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SalesOrderLine>> Filters { get; } = new Dictionary<string, RecordFilter<SalesOrderLine>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.OrderId == id) : null,
        ["kind"] = (q, v, _) => LineKinds.All.Contains(v) ? q.Where(l => l.Kind == v) : null,
        ["sourceQuoteId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.SourceQuoteId == id) : null,
    };

    protected override IOrderedQueryable<SalesOrderLine> Order(IQueryable<SalesOrderLine> source) => source.OrderBy(l => l.Position).ThenBy(l => l.CreatedAt);

    protected override IQueryable<SalesOrderLine> Search(IQueryable<SalesOrderLine> source, string term) =>
        source.Where(l => l.Name.Contains(term) || (l.ProductCode != null && l.ProductCode.Contains(term)));

    protected override IQueryable<SalesOrderLine> ScopeToOwners(IQueryable<SalesOrderLine> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(l => orders.Any(o => o.Id == l.OrderId && ids.Contains(o.OwnerId)));
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

    protected override Task<JsonObject> PrepareNewAsync(SalesOrderLine entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Sipariş kalemi yalnızca tekliften sipariş oluşturulurken eklenir.")]);
        }

        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(SalesOrderLine entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew || Context.Gate.IsOpen)
        {
            return;
        }

        var order = await Context.Data.Set<SalesOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == entity.OrderId, cancellationToken);
        if (order is null || OrderStatus.IsFinal(order.Status))
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Sonuçlanmış siparişin kalemi değiştirilemez.")]);
        }
    }

    protected override Task BeforeDeleteAsync(SalesOrderLine entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Sipariş kalemi silinemez; siparişi iptal edin.");
}
