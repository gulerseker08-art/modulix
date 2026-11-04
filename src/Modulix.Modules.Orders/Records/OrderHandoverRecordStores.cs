using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>Devir kaydı ortak işlemleri: hazırlık denetimi, sipariş devir durumu aynası ve geçmiş satırı.</summary>
internal static class HandoverSync
{
    public static HandoverSnapshot Snapshot(OrderHandover h) => new(
        h.Scope, h.Deliverables, h.Location, h.ContactName, h.ContactPhone, h.ContactEmail, h.TargetStartDate, h.TargetEndDate, h.CompletionCriteria,
        h.AcceptanceCriteria, h.ResponsibleUserId, h.ResponsibleTeam);

    /// <summary>
    /// Zorunlu bilgi ve kontrol listesi eksikleri. <paramref name="changed"/>: henüz kaydedilmemiş (izlenen) madde; veritabanındaki halinin yerine geçer.
    /// </summary>
    public static async Task<IReadOnlyList<string>> MissingAsync(OrdersStoreContext context, OrderHandover handover, OrderHandoverItem? changed, bool removed, CancellationToken cancellationToken)
    {
        var items = await context.Data.Set<OrderHandoverItem>().AsNoTracking().Where(i => i.HandoverId == handover.Id).ToListAsync(cancellationToken);
        var states = items.Where(i => i.Id != changed?.Id).Select(i => new HandoverChecklistState(i.Text, i.IsRequired, i.IsDone)).ToList();
        if (changed is not null && !removed)
        {
            states.Add(new HandoverChecklistState(changed.Text, changed.IsRequired, changed.IsDone));
        }

        return HandoverRules.Missing(Snapshot(handover), states);
    }

    /// <summary>Siparişin devir durumunu devir kaydının durumuna eşitler (sürüm artar; sipariş tarihçesine satır yazılır).</summary>
    public static async Task MirrorAsync(OrdersStoreContext context, OrderHandover handover, CancellationToken cancellationToken)
    {
        var order = await context.Data.Set<SalesOrder>().SingleAsync(o => o.Id == handover.OrderId, cancellationToken);
        if (order.HandoverStatus == handover.Status)
        {
            return;
        }

        OrderHistory.Add(context, order, OrderEventKinds.HandoverChanged, null, order.HandoverStatus, handover.Status);
        order.HandoverStatus = handover.Status;
        order.Touch();
    }

    public static void AddEvent(OrdersStoreContext context, OrderHandover handover, string kind, string? note) =>
        context.Data.Set<OrderHandoverEvent>().Add(new OrderHandoverEvent
        {
            HandoverId = handover.Id,
            OrderId = handover.OrderId,
            Round = handover.Round,
            Kind = kind,
            ActorId = context.CurrentUserId,
            Note = note is { Length: > OrderHandover.TextMaxLength } ? note[..OrderHandover.TextMaxLength] : note,
        });

    /// <summary>Üst sipariş sonuçlanmışsa devir ve maddeleri değişmez.</summary>
    public static async Task EnsureEditableAsync(OrdersStoreContext context, OrderHandover handover, string field, CancellationToken cancellationToken)
    {
        if (context.Gate.IsOpen)
        {
            return;
        }

        if (!HandoverRules.CanEdit(handover.Status))
        {
            throw new RecordValidationException([new RecordFieldError(field, handover.Status == HandoverStatus.Accepted
                ? "Kabul edilen devir değiştirilemez; değişiklik için sipariş değişiklik talebi açın."
                : "Operasyona gönderilen devir karara bağlanana kadar değiştirilemez.")]);
        }

        var status = await context.Data.Set<SalesOrder>().AsNoTracking().Where(o => o.Id == handover.OrderId).Select(o => o.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null || OrderStatus.IsFinal(status))
        {
            throw new RecordValidationException([new RecordFieldError(field, "Sonuçlanmış siparişin devri değiştirilemez.")]);
        }
    }
}

/// <summary>
/// Satış devri kaydı. Devir yalnızca iş akışı servisiyle (varsayılan kontrol listesi, geçmiş ve sipariş aynasıyla birlikte) oluşur; içerik alanları
/// hazırlanırken ve bilgi istenince düzenlenir. Her kayıtta zorunlu bilgi denetimi yapılır ve durum Hazırlanıyor/Hazır olarak sunucuda belirlenir;
/// durum, tur ve karar alanları yalnızca eylemlerle yazılır. Görünürlük ve veri kapsamı üst siparişi izler.
/// </summary>
internal sealed class OrderHandoverRecordStore(OrdersStoreContext context) : OrdersRecordStore<OrderHandover>(context)
{
    public override string EntityKey => OrdersModule.Entities.Handover;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.HandoversWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => OrdersModule.Entities.Order;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OrderHandover>> Fields { get; } =
    [
        Field.Reference<OrderHandover, SalesOrder>("orderId", "Sipariş bulunamadı.", h => h.OrderId, (h, v) => h.OrderId = v!.Value, required: true, createOnly: true),
        Field.Text<OrderHandover>("status", SalesOrder.StatusMaxLength, h => h.Status, (h, v) => h.Status = v!).AsSystem(),
        Field.Integer<OrderHandover>("round", h => h.Round, (h, v) => h.Round = v ?? 0).AsSystem(),
        Field.Text<OrderHandover>("scope", OrderHandover.TextMaxLength, h => h.Scope, (h, v) => h.Scope = v),
        Field.Text<OrderHandover>("outOfScope", OrderHandover.TextMaxLength, h => h.OutOfScope, (h, v) => h.OutOfScope = v),
        Field.Text<OrderHandover>("deliverables", OrderHandover.TextMaxLength, h => h.Deliverables, (h, v) => h.Deliverables = v),
        Field.Text<OrderHandover>("commitments", OrderHandover.TextMaxLength, h => h.Commitments, (h, v) => h.Commitments = v),
        Field.Text<OrderHandover>("location", SalesOrder.AddressMaxLength, h => h.Location, (h, v) => h.Location = v),
        Field.Text<OrderHandover>("contactName", OrderHandover.ShortMaxLength, h => h.ContactName, (h, v) => h.ContactName = v),
        Field.Text<OrderHandover>("contactPhone", OrderHandover.ShortMaxLength, h => h.ContactPhone, (h, v) => h.ContactPhone = v),
        Field.Text<OrderHandover>("contactEmail", 254, h => h.ContactEmail, (h, v) => h.ContactEmail = v),
        Field.Date<OrderHandover>("targetStartDate", h => h.TargetStartDate, (h, v) => h.TargetStartDate = v),
        Field.Date<OrderHandover>("targetEndDate", h => h.TargetEndDate, (h, v) => h.TargetEndDate = v),
        Field.Choice<OrderHandover>("priority", HandoverPriority.All, h => h.Priority, (h, v) => h.Priority = v),
        Field.Text<OrderHandover>("requiredDocuments", OrderHandover.TextMaxLength, h => h.RequiredDocuments, (h, v) => h.RequiredDocuments = v),
        Field.Text<OrderHandover>("customerPrerequisites", OrderHandover.TextMaxLength, h => h.CustomerPrerequisites, (h, v) => h.CustomerPrerequisites = v),
        Field.Text<OrderHandover>("paymentConditions", OrderHandover.TextMaxLength, h => h.PaymentConditions, (h, v) => h.PaymentConditions = v),
        Field.Text<OrderHandover>("risks", OrderHandover.TextMaxLength, h => h.Risks, (h, v) => h.Risks = v),
        Field.Text<OrderHandover>("specialRequests", OrderHandover.TextMaxLength, h => h.SpecialRequests, (h, v) => h.SpecialRequests = v),
        Field.Text<OrderHandover>("dependencies", OrderHandover.TextMaxLength, h => h.Dependencies, (h, v) => h.Dependencies = v),
        Field.Owner<OrderHandover>("responsibleUserId", h => h.ResponsibleUserId, (h, v) => h.ResponsibleUserId = v),
        Field.Text<OrderHandover>("responsibleTeam", OrderHandover.ShortMaxLength, h => h.ResponsibleTeam, (h, v) => h.ResponsibleTeam = v),
        Field.Text<OrderHandover>("completionCriteria", OrderHandover.TextMaxLength, h => h.CompletionCriteria, (h, v) => h.CompletionCriteria = v),
        Field.Text<OrderHandover>("acceptanceCriteria", OrderHandover.TextMaxLength, h => h.AcceptanceCriteria, (h, v) => h.AcceptanceCriteria = v),
        Field.Text<OrderHandover>("infoRequest", OrderHandover.TextMaxLength, h => h.InfoRequest, (h, v) => h.InfoRequest = v).AsSystem(),
        Field.Time<OrderHandover>("sentAt", h => h.SentAt, (h, v) => h.SentAt = v).AsSystem(),
        Field.Id<OrderHandover>("sentBy", h => h.SentBy, (h, v) => h.SentBy = v).AsSystem(),
        Field.Time<OrderHandover>("acceptedAt", h => h.AcceptedAt, (h, v) => h.AcceptedAt = v).AsSystem(),
        Field.Id<OrderHandover>("acceptedBy", h => h.AcceptedBy, (h, v) => h.AcceptedBy = v).AsSystem(),
        Field.Id<OrderHandover>("operationsActivityId", h => h.OperationsActivityId, (h, v) => h.OperationsActivityId = v).AsSystem(),
        Field.Id<OrderHandover>("salesActivityId", h => h.SalesActivityId, (h, v) => h.SalesActivityId = v).AsSystem(),
        Field.Integer<OrderHandover>("escalatedRound", h => h.EscalatedRound, (h, v) => h.EscalatedRound = v ?? 0).AsSystem(),
        Field.Id<OrderHandover>("createdBy", h => h.CreatedBy, (h, v) => h.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OrderHandover>> Filters { get; } = new Dictionary<string, RecordFilter<OrderHandover>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(h => h.OrderId == id) : null,
        ["status"] = (q, v, _) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } statuses && statuses.All(HandoverStatus.All.Contains)
            ? q.Where(h => statuses.Contains(h.Status))
            : null,
        ["responsibleUserId"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } user ? q.Where(h => h.ResponsibleUserId == user) : null,
    };

    protected override IOrderedQueryable<OrderHandover> Order(IQueryable<OrderHandover> source) => source.OrderByDescending(h => h.CreatedAt);

    protected override IQueryable<OrderHandover> Search(IQueryable<OrderHandover> source, string term) =>
        source.Where(h => (h.Scope != null && h.Scope.Contains(term)) || (h.Location != null && h.Location.Contains(term)));

    protected override IQueryable<OrderHandover> ScopeToOwners(IQueryable<OrderHandover> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(h => orders.Any(o => o.Id == h.OrderId && ids.Contains(o.OwnerId)));
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

    protected override Task<JsonObject> PrepareNewAsync(OrderHandover entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Devir kaydı yalnızca sipariş ekranındaki devir hazırlama işlemiyle açılır.")]);
        }

        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(OrderHandover entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var workflow = Context.Gate.IsOpen;
        if (!isNew && !workflow)
        {
            await HandoverSync.EnsureEditableAsync(Context, entity, "status", cancellationToken);
        }

        var errors = new List<RecordFieldError>();
        if (entity.TargetStartDate is { } start && entity.TargetEndDate is { } end && end < start)
        {
            errors.Add(new RecordFieldError("targetEndDate", "Hedef bitiş tarihi başlangıçtan önce olamaz."));
        }

        if (!string.IsNullOrWhiteSpace(entity.ContactEmail) && !entity.ContactEmail.Contains('@'))
        {
            errors.Add(new RecordFieldError("contactEmail", "Geçerli bir e-posta adresi girin."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (!isNew && !workflow)
        {
            // Kullanıcı düzenlemesi: zorunlu bilgi denetimi sonucuna göre durum Hazırlanıyor/Hazır olur ve siparişe yansır.
            var missing = await HandoverSync.MissingAsync(Context, entity, null, false, cancellationToken);
            var next = HandoverRules.EditableStatus(entity.Status, missing);
            if (next != entity.Status)
            {
                entity.Status = next;
                await HandoverSync.MirrorAsync(Context, entity, cancellationToken);
            }
        }

        if (workflow && !isNew && Context.Gate.HandoverEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override async Task CreatedAsync(OrderHandover entity, CancellationToken cancellationToken)
    {
        var order = await Context.Data.Set<SalesOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == entity.OrderId, cancellationToken);
        if (order is null || OrderStatus.IsFinal(order.Status))
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Sonuçlanmış sipariş için devir hazırlanamaz.")]);
        }

        entity.Status = HandoverStatus.Preparing;
        HandoverSync.AddEvent(Context, entity, HandoverEventKinds.Created, null);
        // Varsayılan kontrol listesi devir kaydıyla aynı kaydetmede oluşur.
        var position = 0;
        foreach (var text in HandoverRules.DefaultChecklist)
        {
            Context.Data.Set<OrderHandoverItem>().Add(new OrderHandoverItem { HandoverId = entity.Id, Position = ++position, Text = text, IsRequired = true, IsDefault = true });
        }

        await HandoverSync.MirrorAsync(Context, entity, cancellationToken);
    }

    protected override Task BeforeDeleteAsync(OrderHandover entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Devir kaydı silinemez; geçmişi korunur.");
}

/// <summary>
/// Devir kontrol listesi maddeleri. Varsayılan maddeler yalnızca tamamlandı olarak işaretlenir (metin ve zorunluluk değişmez, silinmez); kullanıcı kendi
/// maddesini ekleyebilir, düzenleyebilir ve silebilir. Her değişiklikte devrin zorunlu bilgi denetimi yenilenir. Devir düzenlenemeyen durumdaysa
/// maddeler de değişmez.
/// </summary>
internal sealed class OrderHandoverItemRecordStore(OrdersStoreContext context) : OrdersRecordStore<OrderHandoverItem>(context)
{
    public override string EntityKey => OrdersModule.Entities.HandoverItem;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.HandoversWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => OrdersModule.Entities.Order;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OrderHandoverItem>> Fields { get; } =
    [
        Field.Reference<OrderHandoverItem, OrderHandover>("handoverId", "Devir kaydı bulunamadı.", i => i.HandoverId, (i, v) => i.HandoverId = v!.Value, required: true, createOnly: true),
        Field.Integer<OrderHandoverItem>("position", i => i.Position, (i, v) => i.Position = v ?? 0, max: 9999).AsSystem(),
        Field.Text<OrderHandoverItem>("text", OrderHandoverItem.TextMaxLength, i => i.Text, (i, v) => i.Text = v!, required: true),
        Field.Flag<OrderHandoverItem>("isRequired", i => i.IsRequired, (i, v) => i.IsRequired = v).AsSystem(),
        Field.Flag<OrderHandoverItem>("isDone", i => i.IsDone, (i, v) => i.IsDone = v),
        Field.Id<OrderHandoverItem>("doneBy", i => i.DoneBy, (i, v) => i.DoneBy = v).AsSystem(),
        Field.Time<OrderHandoverItem>("doneAt", i => i.DoneAt, (i, v) => i.DoneAt = v).AsSystem(),
        Field.Text<OrderHandoverItem>("note", SalesOrder.ReasonMaxLength, i => i.Note, (i, v) => i.Note = v),
        Field.Flag<OrderHandoverItem>("isDefault", i => i.IsDefault, (i, v) => i.IsDefault = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OrderHandoverItem>> Filters { get; } = new Dictionary<string, RecordFilter<OrderHandoverItem>>
    {
        ["handoverId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(i => i.HandoverId == id) : null,
    };

    protected override IOrderedQueryable<OrderHandoverItem> Order(IQueryable<OrderHandoverItem> source) => source.OrderBy(i => i.Position).ThenBy(i => i.CreatedAt);

    protected override IQueryable<OrderHandoverItem> Search(IQueryable<OrderHandoverItem> source, string term) => source.Where(i => i.Text.Contains(term));

    protected override IQueryable<OrderHandoverItem> ScopeToOwners(IQueryable<OrderHandoverItem> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var handovers = Context.Data.Set<OrderHandover>();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(i => handovers.Any(h => h.Id == i.HandoverId && orders.Any(o => o.Id == h.OrderId && ids.Contains(o.OwnerId))));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("handoverId", out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var handoverId))
        {
            return false;
        }

        var ids = owners.Select(id => (Guid?)id).ToList();
        return await Context.Data.Set<OrderHandover>().AsNoTracking().AnyAsync(
            h => h.Id == handoverId && Context.Data.Set<SalesOrder>().Any(o => o.Id == h.OrderId && ids.Contains(o.OwnerId)), cancellationToken);
    }

    protected override async Task<JsonObject> PrepareNewAsync(OrderHandoverItem entity, JsonObject values, CancellationToken cancellationToken)
    {
        // Kullanıcının eklediği madde zorunludur (eklenen iş yapılmadan devir gönderilmez); sıra sona eklenir.
        entity.IsRequired = true;
        if (values.TryGetPropertyValue("handoverId", out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var handoverId))
        {
            var count = await Context.Data.Set<OrderHandoverItem>().CountAsync(i => i.HandoverId == handoverId, cancellationToken);
            entity.Position = count + 1;
        }

        return values;
    }

    private string? textBefore;

    /// <summary>Güncellemede alanlar yazılmadan önceki metin (varsayılan maddenin metni değişmez).</summary>
    protected override void OnUpdating(OrderHandoverItem entity) => textBefore = entity.Text;

    protected override async Task AppliedAsync(OrderHandoverItem entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var parent = await Context.Data.Set<OrderHandover>().SingleOrDefaultAsync(h => h.Id == entity.HandoverId, cancellationToken)
            ?? throw new RecordValidationException([new RecordFieldError("handoverId", "Devir kaydı bulunamadı.")]);
        await HandoverSync.EnsureEditableAsync(Context, parent, "handoverId", cancellationToken);
        if (!isNew && entity.IsDefault && entity.Text != textBefore)
        {
            throw new RecordValidationException([new RecordFieldError("text", "Varsayılan kontrol listesi maddesinin metni değiştirilemez.")]);
        }

        if (values.ContainsKey("isDone"))
        {
            entity.DoneBy = entity.IsDone ? Context.CurrentUserId : null;
            entity.DoneAt = entity.IsDone ? DateTimeOffset.UtcNow : null;
        }

        await RefreshAsync(parent, entity, removed: false, cancellationToken);
    }

    protected override async Task BeforeDeleteAsync(OrderHandoverItem entity, CancellationToken cancellationToken)
    {
        if (entity.IsDefault)
        {
            throw new RecordConflictException("Varsayılan kontrol listesi maddesi silinemez; yalnızca tamamlandı olarak işaretlenir.");
        }

        var handover = await Context.Data.Set<OrderHandover>().SingleAsync(h => h.Id == entity.HandoverId, cancellationToken);
        await HandoverSync.EnsureEditableAsync(Context, handover, "handoverId", cancellationToken);
        await RefreshAsync(handover, entity, removed: true, cancellationToken);
    }

    /// <summary>Madde değişince devrin zorunlu bilgi denetimi yenilenir; durum değişirse siparişe yansır.</summary>
    private async Task RefreshAsync(OrderHandover handover, OrderHandoverItem changed, bool removed, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            return;
        }

        var missing = await HandoverSync.MissingAsync(Context, handover, changed, removed, cancellationToken);
        var next = HandoverRules.EditableStatus(handover.Status, missing);
        if (next != handover.Status)
        {
            handover.Status = next;
            handover.Touch();
            await HandoverSync.MirrorAsync(Context, handover, cancellationToken);
        }
    }
}
