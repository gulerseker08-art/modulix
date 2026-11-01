using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Teslimat kayıt deposu (ADR-0023, dilim 5). Teslimatlar yalnızca iş akışı servisiyle (planlama, yeniden planlama) oluşur; durum, gerçekleşen tarih, teslim alan,
/// başarısızlık nedeni ve teslim bilgileri sistem alanıdır (genel uçtan yazılamaz). Yalnızca planlı teslimatın planlanan tarihi ve notu düzenlenir. Kalem satırları ve
/// teslim edilen miktar toplamı kapı etkisiyle aynı kaydetmede yazılır. Görünürlük ve veri kapsamı üst siparişi izler.
/// </summary>
internal sealed class OrderDeliveryRecordStore(OrdersStoreContext context) : OrdersRecordStore<OrderDelivery>(context)
{
    public override string EntityKey => OrdersModule.Entities.Delivery;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.FulfillmentRecord;

    protected override bool UsesScope => true;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OrderDelivery>> Fields { get; } =
    [
        Field.Reference<OrderDelivery, SalesOrder>("orderId", "Sipariş bulunamadı.", d => d.OrderId, (d, v) => d.OrderId = v!.Value, required: true, createOnly: true),
        Field.Id<OrderDelivery>("jobId", d => d.JobId, (d, v) => d.JobId = v).AsSystem(),
        Field.Text<OrderDelivery>("number", OrderDelivery.NumberMaxLength, d => d.Number, (d, v) => d.Number = v!).AsSystem(),
        Field.Text<OrderDelivery>("status", SalesOrder.StatusMaxLength, d => d.Status, (d, v) => d.Status = v!).AsSystem(),
        Field.Date<OrderDelivery>("plannedDate", d => d.PlannedDate == default ? null : d.PlannedDate, (d, v) => d.PlannedDate = v ?? default),
        Field.Date<OrderDelivery>("actualDate", d => d.ActualDate, (d, v) => d.ActualDate = v).AsSystem(),
        Field.Text<OrderDelivery>("receiverName", SalesOrder.NameMaxLength, d => d.ReceiverName, (d, v) => d.ReceiverName = v).AsSystem(),
        Field.Text<OrderDelivery>("receiverNote", OrderDelivery.TextMaxLength, d => d.ReceiverNote, (d, v) => d.ReceiverNote = v).AsSystem(),
        Field.Text<OrderDelivery>("failureReason", SalesOrder.ReasonMaxLength, d => d.FailureReason, (d, v) => d.FailureReason = v).AsSystem(),
        Field.Id<OrderDelivery>("rescheduledFromId", d => d.RescheduledFromId, (d, v) => d.RescheduledFromId = v).AsSystem(),
        Field.Text<OrderDelivery>("rescheduleReason", SalesOrder.ReasonMaxLength, d => d.RescheduleReason, (d, v) => d.RescheduleReason = v).AsSystem(),
        Field.Text<OrderDelivery>("notes", OrderDelivery.TextMaxLength, d => d.Notes, (d, v) => d.Notes = v),
        Field.Time<OrderDelivery>("deliveredAt", d => d.DeliveredAt, (d, v) => d.DeliveredAt = v).AsSystem(),
        Field.Id<OrderDelivery>("deliveredBy", d => d.DeliveredBy, (d, v) => d.DeliveredBy = v).AsSystem(),
        Field.Id<OrderDelivery>("createdBy", d => d.CreatedBy, (d, v) => d.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OrderDelivery>> Filters { get; } = new Dictionary<string, RecordFilter<OrderDelivery>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(d => d.OrderId == id) : null,
        ["status"] = (q, v, _) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } statuses && statuses.All(DeliveryStatus.All.Contains)
            ? q.Where(d => statuses.Contains(d.Status))
            : null,
        ["plannedFrom"] = (q, v, _) => FilterValue.Date(v) is { } date ? q.Where(d => d.PlannedDate >= date) : null,
        ["plannedTo"] = (q, v, _) => FilterValue.Date(v) is { } date ? q.Where(d => d.PlannedDate <= date) : null,
        // Gecikenler: planlı ve planlanan tarihi geçmiş teslimatlar.
        ["overdue"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(d => (d.Status == DeliveryStatus.Planned && d.PlannedDate < DateOnly.FromDateTime(DateTime.UtcNow)) == flag)
            : null,
    };

    protected override IOrderedQueryable<OrderDelivery> Order(IQueryable<OrderDelivery> source) => source.OrderBy(d => d.PlannedDate).ThenBy(d => d.CreatedAt);

    protected override IQueryable<OrderDelivery> Search(IQueryable<OrderDelivery> source, string term) =>
        source.Where(d => d.Number.Contains(term) || (d.ReceiverName != null && d.ReceiverName.Contains(term)));

    protected override IQueryable<OrderDelivery> ScopeToOwners(IQueryable<OrderDelivery> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(d => orders.Any(o => o.Id == d.OrderId && ids.Contains(o.OwnerId)));
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

    protected override Task<JsonObject> PrepareNewAsync(OrderDelivery entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Teslimat yalnızca sipariş ekranındaki teslimat planlama işlemiyle oluşur.")]);
        }

        entity.Number = FulfillmentNumbers.NewDeliveryNumber(DateTimeOffset.UtcNow);
        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task CreatedAsync(OrderDelivery entity, CancellationToken cancellationToken)
    {
        if (Context.Gate.DeliveryEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override async Task AppliedAsync(OrderDelivery entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (entity.PlannedDate == default)
        {
            throw new RecordValidationException([new RecordFieldError("plannedDate", "Planlanan teslim tarihi zorunludur.")]);
        }

        if (!isNew && !Context.Gate.IsOpen && entity.Status != DeliveryStatus.Planned)
        {
            throw new RecordValidationException([new RecordFieldError("status", "Yalnızca planlı teslimatın planı ve notu düzenlenir; teslim edilen ya da sonuçlanan teslimat değişmez.")]);
        }

        if (!isNew && Context.Gate.IsOpen && Context.Gate.DeliveryEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override Task BeforeDeleteAsync(OrderDelivery entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Teslimat silinemez; geçmişi korunur. Planlı teslimat iptal edilir, başarısız teslimat yeniden planlanır.");
}

/// <summary>Teslimat ve hizmet başlangıcı numaraları.</summary>
internal static class FulfillmentNumbers
{
    public static string NewDeliveryNumber(DateTimeOffset createdAt)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"TS-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }
}

/// <summary>
/// Hizmet başlangıç kaydı. Kayıt yalnızca iş akışı servisiyle oluşur; durum, gerçekleşen başlangıç, başlatan, müşteri onayı ve tamamlama bilgisi sistem alanıdır.
/// Planlanan başlangıç tarihi ve gecikme nedeni tamamlanmamış hizmette düzenlenir. Görünürlük ve veri kapsamı üst siparişi izler.
/// </summary>
internal sealed class OrderServiceStartRecordStore(OrdersStoreContext context) : OrdersRecordStore<OrderServiceStart>(context)
{
    public override string EntityKey => OrdersModule.Entities.ServiceStart;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.FulfillmentRecord;

    protected override bool UsesScope => true;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OrderServiceStart>> Fields { get; } =
    [
        Field.Reference<OrderServiceStart, SalesOrder>("orderId", "Sipariş bulunamadı.", s => s.OrderId, (s, v) => s.OrderId = v!.Value, required: true, createOnly: true),
        Field.Reference<OrderServiceStart, SalesOrderLine>("orderLineId", "Sipariş kalemi bulunamadı.", s => s.OrderLineId, (s, v) => s.OrderLineId = v!.Value, required: true, createOnly: true),
        Field.Text<OrderServiceStart>("status", SalesOrder.StatusMaxLength, s => s.Status, (s, v) => s.Status = v!).AsSystem(),
        Field.Date<OrderServiceStart>("plannedStartDate", s => s.PlannedStartDate, (s, v) => s.PlannedStartDate = v),
        Field.Time<OrderServiceStart>("actualStartAt", s => s.ActualStartAt, (s, v) => s.ActualStartAt = v).AsSystem(),
        Field.Id<OrderServiceStart>("startedBy", s => s.StartedBy, (s, v) => s.StartedBy = v).AsSystem(),
        Field.Time<OrderServiceStart>("customerConfirmedAt", s => s.CustomerConfirmedAt, (s, v) => s.CustomerConfirmedAt = v).AsSystem(),
        Field.Text<OrderServiceStart>("customerConfirmedBy", SalesOrder.NameMaxLength, s => s.CustomerConfirmedBy, (s, v) => s.CustomerConfirmedBy = v).AsSystem(),
        Field.Text<OrderServiceStart>("confirmationNote", SalesOrder.ReasonMaxLength, s => s.ConfirmationNote, (s, v) => s.ConfirmationNote = v).AsSystem(),
        Field.Text<OrderServiceStart>("delayReason", SalesOrder.ReasonMaxLength, s => s.DelayReason, (s, v) => s.DelayReason = v),
        Field.Time<OrderServiceStart>("completedAt", s => s.CompletedAt, (s, v) => s.CompletedAt = v).AsSystem(),
        Field.Id<OrderServiceStart>("completedBy", s => s.CompletedBy, (s, v) => s.CompletedBy = v).AsSystem(),
        Field.Id<OrderServiceStart>("createdBy", s => s.CreatedBy, (s, v) => s.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OrderServiceStart>> Filters { get; } = new Dictionary<string, RecordFilter<OrderServiceStart>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(s => s.OrderId == id) : null,
        ["orderLineId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(s => s.OrderLineId == id) : null,
        ["status"] = (q, v, _) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } statuses && statuses.All(ServiceStatus.All.Contains)
            ? q.Where(s => statuses.Contains(s.Status))
            : null,
        // Gecikenler: başlamamış ve planlanan başlangıcı geçmiş hizmetler.
        ["overdue"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(s => (s.Status == ServiceStatus.Planned && s.PlannedStartDate != null && s.PlannedStartDate < DateOnly.FromDateTime(DateTime.UtcNow)) == flag)
            : null,
    };

    protected override IOrderedQueryable<OrderServiceStart> Order(IQueryable<OrderServiceStart> source) => source.OrderBy(s => s.PlannedStartDate == null).ThenBy(s => s.PlannedStartDate).ThenBy(s => s.CreatedAt);

    protected override IQueryable<OrderServiceStart> Search(IQueryable<OrderServiceStart> source, string term) =>
        source.Where(s => s.DelayReason != null && s.DelayReason.Contains(term));

    protected override IQueryable<OrderServiceStart> ScopeToOwners(IQueryable<OrderServiceStart> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(s => orders.Any(o => o.Id == s.OrderId && ids.Contains(o.OwnerId)));
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

    protected override Task<JsonObject> PrepareNewAsync(OrderServiceStart entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Hizmet başlangıç kaydı yalnızca sipariş ekranındaki planlama işlemiyle oluşur.")]);
        }

        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override Task AppliedAsync(OrderServiceStart entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew && !Context.Gate.IsOpen && entity.Status == ServiceStatus.Completed)
        {
            throw new RecordValidationException([new RecordFieldError("status", "Tamamlanmış hizmetin başlangıç kaydı değiştirilemez.")]);
        }

        return Task.CompletedTask;
    }

    protected override Task BeforeDeleteAsync(OrderServiceStart entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Hizmet başlangıç kaydı silinemez; geçmişi korunur.");
}
