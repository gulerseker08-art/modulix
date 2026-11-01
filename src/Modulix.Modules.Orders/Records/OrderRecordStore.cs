using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Sipariş başlığı kayıt deposu (ADR-0023). Sipariş genel uçtan oluşturulamaz ve silinemez: yalnızca kabul edilmiş tekliften, iş akışı
/// kapısı açıkken oluşur ve iptal edilir. Durum, kaynak teklif, kabul kanıtı, ticari snapshot ve toplamlar sistem alanıdır; genel uçlardan
/// yazılamaz. Ticari alanlar yalnızca taslakta düzenlenir (<see cref="OrderRules.CanEditContent"/>).
/// </summary>
internal sealed class OrderRecordStore(OrdersStoreContext context) : OrdersRecordStore<SalesOrder>(context)
{
    /// <summary>Düzenlenmesi "ticari içerik değişikliği" sayılan alanlar; yalnızca taslakta yazılır.</summary>
    private static readonly string[] ContentFields =
    [
        "title", "personId", "contractId", "contractNumber", "deliveryAddress", "location", "plannedStartDate", "plannedDeliveryDate",
        "paymentTerms", "deliveryTerms", "commercialTerms", "commitments",
    ];

    public override string EntityKey => OrdersModule.Entities.Order;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.OrdersWrite;

    protected override string CreatePermission => OrdersModule.Permissions.OrdersCreate;

    protected override IReadOnlyList<RecordField<SalesOrder>> Fields { get; } =
    [
        Field.Text<SalesOrder>("number", SalesOrder.NumberMaxLength, o => o.Number, (o, v) => o.Number = v!).AsSystem(),
        Field.Text<SalesOrder>("title", SalesOrder.TitleMaxLength, o => o.Title, (o, v) => o.Title = v!, required: true),
        Field.Shared<SalesOrder, Party>("partyId", "Müşteri bulunamadı.", o => o.PartyId, (o, v) => o.PartyId = v!.Value, required: true).AsSystem(),
        Field.Shared<SalesOrder, Person>("personId", "Kişi bulunamadı.", o => o.PersonId, (o, v) => o.PersonId = v),
        Field.Owner<SalesOrder>(OwnerField, o => o.OwnerId, (o, v) => o.OwnerId = v),
        Field.Id<SalesOrder>("sourceQuoteId", o => o.SourceQuoteId == Guid.Empty ? null : o.SourceQuoteId, (o, v) => o.SourceQuoteId = v ?? Guid.Empty).AsSystem(),
        Field.Text<SalesOrder>("sourceQuoteNumber", SalesOrder.NumberMaxLength, o => o.SourceQuoteNumber, (o, v) => o.SourceQuoteNumber = v ?? string.Empty).AsSystem(),
        Field.Integer<SalesOrder>("sourceQuoteRevisionNo", o => o.SourceQuoteRevisionNo, (o, v) => o.SourceQuoteRevisionNo = v ?? 1).AsSystem(),
        Field.Text<SalesOrder>("sourceQuoteSha256", 64, o => o.SourceQuoteSha256, (o, v) => o.SourceQuoteSha256 = v).AsSystem(),
        Field.Id<SalesOrder>("sourceOpportunityId", o => o.SourceOpportunityId, (o, v) => o.SourceOpportunityId = v).AsSystem(),
        Field.Id<SalesOrder>("contractId", o => o.ContractId, (o, v) => o.ContractId = v),
        Field.Text<SalesOrder>("contractNumber", SalesOrder.NumberMaxLength, o => o.ContractNumber, (o, v) => o.ContractNumber = v),
        Field.Choice<SalesOrder>("conversionMode", ConversionMode.All, o => o.ConversionMode, (o, v) => o.ConversionMode = v).AsSystem(),
        Field.Time<SalesOrder>("acceptedAt", o => o.AcceptedAt, (o, v) => o.AcceptedAt = v ?? default).AsSystem(),
        Field.Text<SalesOrder>("acceptanceMethod", SalesOrder.StatusMaxLength, o => o.AcceptanceMethod, (o, v) => o.AcceptanceMethod = v!).AsSystem(),
        Field.Id<SalesOrder>("acceptedByUserId", o => o.AcceptedByUserId, (o, v) => o.AcceptedByUserId = v).AsSystem(),
        Field.Text<SalesOrder>("acceptedByName", SalesOrder.NameMaxLength, o => o.AcceptedByName, (o, v) => o.AcceptedByName = v).AsSystem(),
        Field.Text<SalesOrder>("acceptanceNote", SalesOrder.TextMaxLength, o => o.AcceptanceNote, (o, v) => o.AcceptanceNote = v).AsSystem(),
        Field.Time<SalesOrder>("quoteValidUntil", o => o.QuoteValidUntil, (o, v) => o.QuoteValidUntil = v).AsSystem(),
        Field.Text<SalesOrder>("status", SalesOrder.StatusMaxLength, o => o.Status, (o, v) => o.Status = v!).AsSystem(),
        Field.Text<SalesOrder>("handoverStatus", SalesOrder.StatusMaxLength, o => o.HandoverStatus, (o, v) => o.HandoverStatus = v!).AsSystem(),
        Field.Text<SalesOrder>("fulfillmentStatus", SalesOrder.StatusMaxLength, o => o.FulfillmentStatus, (o, v) => o.FulfillmentStatus = v!).AsSystem(),
        Field.Text<SalesOrder>("statusReason", SalesOrder.ReasonMaxLength, o => o.StatusReason, (o, v) => o.StatusReason = v).AsSystem(),
        Field.Text<SalesOrder>("customerName", SalesOrder.NameMaxLength, o => o.CustomerName, (o, v) => o.CustomerName = v).AsSystem(),
        Field.Text<SalesOrder>("billingAddress", SalesOrder.AddressMaxLength, o => o.BillingAddress, (o, v) => o.BillingAddress = v).AsSystem(),
        Field.Text<SalesOrder>("deliveryAddress", SalesOrder.AddressMaxLength, o => o.DeliveryAddress, (o, v) => o.DeliveryAddress = v),
        Field.Currency<SalesOrder>("currency", o => o.Currency, (o, v) => o.Currency = v).AsSystem(),
        Field.Flag<SalesOrder>("priceIncludesTax", o => o.PriceIncludesTax, (o, v) => o.PriceIncludesTax = v).AsSystem(),
        Field.Text<SalesOrder>("reportingCurrency", SalesOrder.CurrencyLength, o => o.ReportingCurrency, (o, v) => o.ReportingCurrency = v).AsSystem(),
        Field.Quantity<SalesOrder>("exchangeRate", o => o.ExchangeRate, (o, v) => o.ExchangeRate = v).AsSystem(),
        Field.Date<SalesOrder>("exchangeRateDate", o => o.ExchangeRateDate, (o, v) => o.ExchangeRateDate = v).AsSystem(),
        Field.Text<SalesOrder>("paymentTerms", SalesOrder.TextMaxLength, o => o.PaymentTerms, (o, v) => o.PaymentTerms = v),
        Field.Text<SalesOrder>("deliveryTerms", SalesOrder.TextMaxLength, o => o.DeliveryTerms, (o, v) => o.DeliveryTerms = v),
        Field.Text<SalesOrder>("commercialTerms", SalesOrder.TextMaxLength, o => o.CommercialTerms, (o, v) => o.CommercialTerms = v),
        Field.Text<SalesOrder>("commitments", SalesOrder.TextMaxLength, o => o.Commitments, (o, v) => o.Commitments = v),
        Field.Text<SalesOrder>("operationsNotes", SalesOrder.TextMaxLength, o => o.OperationsNotes, (o, v) => o.OperationsNotes = v),
        Field.Text<SalesOrder>("location", SalesOrder.AddressMaxLength, o => o.Location, (o, v) => o.Location = v),
        Field.Date<SalesOrder>("plannedStartDate", o => o.PlannedStartDate, (o, v) => o.PlannedStartDate = v),
        Field.Date<SalesOrder>("plannedDeliveryDate", o => o.PlannedDeliveryDate, (o, v) => o.PlannedDeliveryDate = v),
        Field.Money<SalesOrder>("netTotal", o => o.NetTotal, (o, v) => o.NetTotal = v ?? 0m).AsSystem(),
        Field.Money<SalesOrder>("taxTotal", o => o.TaxTotal, (o, v) => o.TaxTotal = v ?? 0m).AsSystem(),
        Field.Money<SalesOrder>("grossTotal", o => o.GrossTotal, (o, v) => o.GrossTotal = v ?? 0m).AsSystem(),
        Field.Id<SalesOrder>("createdBy", o => o.CreatedBy, (o, v) => o.CreatedBy = v).AsSystem(),
        Field.Id<SalesOrder>("updatedBy", o => o.UpdatedBy, (o, v) => o.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SalesOrder>> Filters { get; } = new Dictionary<string, RecordFilter<SalesOrder>>
    {
        ["partyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.PartyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.PersonId == id) : null,
        ["sourceQuoteId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.SourceQuoteId == id) : null,
        ["sourceOpportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.SourceOpportunityId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(o => o.OwnerId == owner) : null,
        ["number"] = (q, v, _) => v.Trim() is { Length: > 0 } number ? q.Where(o => o.Number == number) : null,
        // Virgülle ayrılmış birden çok durum kabul edilir (ör. status=Draft,PendingApproval).
        ["status"] = (q, v, _) => Split(v, OrderStatus.All) is { } statuses ? q.Where(o => statuses.Contains(o.Status)) : null,
        ["handoverStatus"] = (q, v, _) => Split(v, HandoverStatus.All) is { } statuses ? q.Where(o => statuses.Contains(o.HandoverStatus)) : null,
        ["fulfillmentStatus"] = (q, v, _) => Split(v, FulfillmentStatus.All) is { } statuses ? q.Where(o => statuses.Contains(o.FulfillmentStatus)) : null,
        ["currency"] = (q, v, _) => v.Trim().ToUpperInvariant() is { Length: SalesOrder.CurrencyLength } code ? q.Where(o => o.Currency == code) : null,
        ["deliveryFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.PlannedDeliveryDate >= d) : null,
        ["deliveryTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.PlannedDeliveryDate <= d) : null,
        // Gecikenler: planlanan teslim/başlangıç tarihi geçmiş, sonuçlanmamış ve gerçekleşmesi tamamlanmamış siparişler.
        ["overdue"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(o => (o.Status == OrderStatus.Approved && o.FulfillmentStatus != FulfillmentStatus.Completed
                && o.PlannedDeliveryDate != null && o.PlannedDeliveryDate < DateOnly.FromDateTime(DateTime.UtcNow)) == flag)
            : null,
        // Devir bekleyenler: onaylı ama devri operasyonca kabul edilmemiş siparişler.
        ["handoverPending"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(o => (o.Status == OrderStatus.Approved && o.HandoverStatus != HandoverStatus.Accepted) == flag)
            : null,
    };

    private static List<string>? Split(string value, IReadOnlyCollection<string> allowed) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts && parts.All(allowed.Contains)
            ? [.. parts]
            : null;

    protected override IOrderedQueryable<SalesOrder> Order(IQueryable<SalesOrder> source) => source.OrderByDescending(o => o.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys => OrderSort.All;

    protected override IOrderedQueryable<SalesOrder> Order(IQueryable<SalesOrder> source, string? sort) => sort switch
    {
        "number" => source.OrderBy(o => o.Number),
        "-number" => source.OrderByDescending(o => o.Number),
        "title" => source.OrderBy(o => o.Title),
        "-title" => source.OrderByDescending(o => o.Title),
        "deliveryDate" => source.OrderBy(o => o.PlannedDeliveryDate == null).ThenBy(o => o.PlannedDeliveryDate),
        "-deliveryDate" => source.OrderBy(o => o.PlannedDeliveryDate == null).ThenByDescending(o => o.PlannedDeliveryDate),
        "createdAt" => source.OrderBy(o => o.CreatedAt),
        _ => Order(source),
    };

    protected override IQueryable<SalesOrder> Search(IQueryable<SalesOrder> source, string term) =>
        source.Where(o => o.Title.Contains(term) || o.Number.Contains(term) || o.SourceQuoteNumber.Contains(term) || (o.CustomerName != null && o.CustomerName.Contains(term)));

    private string? before;

    private static string StatusSummary(SalesOrder o) => $"{o.Status}/{o.HandoverStatus}/{o.FulfillmentStatus}";

    protected override void OnUpdating(SalesOrder entity) => before = StatusSummary(entity);

    protected override Task<JsonObject> PrepareNewAsync(SalesOrder entity, JsonObject values, CancellationToken cancellationToken)
    {
        // Sipariş yalnızca kabul edilmiş tekliften (iş akışı servisi, kapı açık) oluşur; genel uçtan boş sipariş açılamaz.
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("title", "Sipariş yalnızca kabul edilmiş tekliften oluşturulur.")]);
        }

        entity.Number = OrderRules.NewNumber(DateTimeOffset.UtcNow);
        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(SalesOrder entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var workflow = Context.Gate.IsOpen;

        if (!isNew && !workflow && !IsTransfer)
        {
            if (OrderStatus.IsFinal(entity.Status))
            {
                errors.Add(new RecordFieldError("title", "Sonuçlanmış sipariş değiştirilemez."));
            }
            else if (ContentFields.Any(values.ContainsKey) && OrderRules.CanEditContent(entity.Status) is { Allowed: false } rule)
            {
                errors.Add(new RecordFieldError("title", rule.Message!));
            }
        }

        // Bağımlı lookup: kişi, müşteri tarafına bağlı olmalıdır (ADR-0016).
        if (entity.PersonId is { } personId && (values.ContainsKey("personId") || values.ContainsKey("partyId"))
            && !await Context.Data.Set<Person>().AsNoTracking().AnyAsync(p => p.Id == personId && p.PartyId == entity.PartyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Kişi seçilen müşteriyle ilişkili değil."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (workflow && !isNew && Context.Gate.EventKind is { } kind)
        {
            OrderHistory.Add(Context, entity, kind, Context.Gate.Reason, before, StatusSummary(entity));
            if (Context.Gate.Effect is { } effect)
            {
                await effect(Context, entity, cancellationToken);
            }
        }
    }

    protected override async Task CreatedAsync(SalesOrder entity, CancellationToken cancellationToken)
    {
        OrderHistory.Add(Context, entity, Context.Gate.EventKind ?? OrderEventKinds.CreatedFromQuote, Context.Gate.Reason, null, StatusSummary(entity));
        if (Context.Gate.Effect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override Task BeforeDeleteAsync(SalesOrder entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Sipariş silinemez; geçmişi korunur. Vazgeçildiyse siparişi iptal edin.");
}

/// <summary>Liste sıralama anahtarları.</summary>
public static class OrderSort
{
    public static readonly IReadOnlyList<string> All = ["number", "-number", "title", "-title", "deliveryDate", "-deliveryDate", "createdAt"];
}

/// <summary>Tarihçe satırı yazımı; sipariş güncellemesiyle aynı işlemde kaydedilir.</summary>
internal static class OrderHistory
{
    public static void Add(OrdersStoreContext context, SalesOrder order, string kind, string? reason, string? from = null, string? to = null) =>
        context.Data.Set<SalesOrderEvent>().Add(new SalesOrderEvent
        {
            OrderId = order.Id,
            Kind = kind,
            ActorId = context.CurrentUserId,
            FromStatus = from,
            ToStatus = to,
            Reason = reason is { Length: > SalesOrder.ReasonMaxLength } ? reason[..SalesOrder.ReasonMaxLength] : reason,
        });
}
