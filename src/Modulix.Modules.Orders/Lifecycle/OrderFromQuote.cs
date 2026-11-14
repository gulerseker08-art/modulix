using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Contracts.Sales;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Sipariş oluşturma seçenekleri. Boş alanlar tekliften ya da sunucu varsayılanından gelir.</summary>
/// <param name="Mode"><c>Full</c> (varsayılan; teklifin tamamı tek sipariş) ya da <c>Partial</c> (kalem bazında miktar).</param>
/// <param name="Lines">Yalnızca <c>Partial</c> kipinde: kalem anahtarı ve siparişe alınacak miktar.</param>
public sealed record OrderFromQuoteOptions(
    string Mode = ConversionMode.Full,
    IReadOnlyList<ConversionLineRequest>? Lines = null,
    string? Title = null,
    Guid? OwnerId = null,
    string? DeliveryAddress = null,
    string? Location = null,
    DateOnly? PlannedStartDate = null,
    DateOnly? PlannedDeliveryDate = null,
    string? Commitments = null,
    string? OperationsNotes = null);

/// <summary>Önizlemedeki teklif kalemi ve siparişe alınma durumu.</summary>
public sealed record OrderSourceLine(
    Guid StableKey, int Position, string Kind, string? ProductCode, string Name, string? Unit, decimal AcceptedQuantity, decimal ConvertedQuantity,
    decimal RemainingQuantity, decimal Net, decimal Gross, string? RecurringPeriod);

public sealed record ExistingOrder(Guid Id, string Number, string Status);

/// <summary>
/// Önizleme: kabul edilen revizyon, kalemler ve eksik bilgi uyarıları. <see cref="Warnings"/> engelleyici değildir (kullanıcı bilgilendirilir),
/// <see cref="Blockers"/> ise siparişin oluşmasını engeller.
/// </summary>
public sealed record OrderSourcePreview(
    Guid QuoteId, string QuoteNumber, int RevisionNo, string Title, string Currency, decimal NetTotal, decimal GrossTotal, DateTimeOffset AcceptedAt,
    string AcceptanceMethod, string? AcceptedBy, string? DocumentSha256, DateTimeOffset? ValidUntil, string? CustomerName, string? BillingAddress,
    string? DeliveryAddress, string? PaymentTerms, string? DeliveryTerms, string? CommercialTerms, bool CustomerLinked, bool FullyConverted,
    IReadOnlyList<OrderSourceLine> Lines, IReadOnlyList<string> Warnings, IReadOnlyList<string> Blockers, IReadOnlyList<ExistingOrder> ExistingOrders);

/// <summary>
/// Kabul edilmiş tekliften sipariş oluşturma (ADR-0023, dilim 1). Teklif <see cref="IAcceptedQuoteSource"/> ile (CRM'e bağlanmadan) okunur;
/// kabul edilmemiş, reddedilmiş, iptal edilmiş, yerine yenisi geçmiş ya da kapsam dışı teklif kaynak tarafından zaten verilmez ("bulunamadı").
/// Teklif geçerliliği <b>kabul anında</b> denetlenmiştir; geçerlilik süresi içinde kabul edilen teklif sonradan siparişe çevrilirken yeniden
/// denetlenmez. Fiyatlar ve koşullar siparişe kopyalanır; sonradan katalog, müşteri kartı ya da teklif değişse de sipariş değişmez.
/// Sipariş, kalemler ve tüketim defteri tek işlemde yazılır: çift tıklama, tekrar gönderim ve eşzamanlı istek tek sonuç üretir (kalan miktar
/// uygulamada, benzersiz anahtar/sürüm belirteci/CHECK kısıtı veritabanında denetlenir).
/// </summary>
public interface IOrderFromQuote
{
    Task<RecordResult<OrderSourcePreview>> PreviewAsync(Guid quoteId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CreateAsync(Guid quoteId, OrderFromQuoteOptions options, CancellationToken cancellationToken);
}

internal sealed class OrderFromQuote(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrdersStoreContext storeContext,
    OrderReader reader,
    IAcceptedQuoteSource? quotes = null) : IOrderFromQuote
{
    public async Task<RecordResult<OrderSourcePreview>> PreviewAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var (quote, error) = await LoadAsync<OrderSourcePreview>(quoteId, cancellationToken);
        if (quote is null)
        {
            return error!;
        }

        var ledger = await LedgerAsync(quoteId, cancellationToken);
        var lines = quote.Lines.Select(l =>
        {
            var state = ledger.TryGetValue(l.StableKey, out var s) ? s : ConsumptionState.For(l);
            return new OrderSourceLine(
                l.StableKey, l.Position, l.Kind, l.ProductCode, l.Name, l.Unit, state.AcceptedQuantity, state.ConvertedQuantity, state.RemainingQuantity,
                l.Net, l.Gross, l.RecurringPeriod);
        }).ToList();

        var existing = await ExistingOrdersAsync(quoteId, cancellationToken);
        var warnings = new List<string>();
        var blockers = new List<string>();
        if (quote.PartyId is null)
        {
            blockers.Add("Teklifin müşterisi ortak müşteri kaydıyla eşleşmedi; sipariş oluşturulamaz.");
        }

        if (lines.Count > 0 && lines.All(l => l.RemainingQuantity <= 0m))
        {
            blockers.Add("Bu teklifin bütün kalemleri zaten siparişe alınmış.");
        }

        if (string.IsNullOrWhiteSpace(quote.DeliveryAddress))
        {
            warnings.Add("Müşteri kartında teslimat adresi yok; sipariş oluşturulurken adresi girin.");
        }

        if (string.IsNullOrWhiteSpace(quote.BillingAddress))
        {
            warnings.Add("Müşteri kartında fatura adresi yok.");
        }

        if (string.IsNullOrWhiteSpace(quote.DeliveryTerms))
        {
            warnings.Add("Tekliste teslimat koşulu yok; planlanan teslim ya da başlangıç tarihini siz girin.");
        }

        if (quote.AcceptanceMethod == AcceptanceMethods.Manual)
        {
            warnings.Add("Kabul satış kullanıcısı tarafından elle kaydedilmiş; müşterinin imzalı belge kanıtı bu kayıtta yok.");
        }

        if (string.IsNullOrWhiteSpace(quote.DocumentSha256))
        {
            warnings.Add("Teklifin müşteriye sunulan sabit belgesi bulunamadı (belge özeti yok).");
        }

        return RecordResult<OrderSourcePreview>.Ok(new OrderSourcePreview(
            quote.QuoteId, quote.QuoteNumber, quote.RevisionNo, quote.Title, quote.Currency, quote.NetTotal, quote.GrossTotal, quote.AcceptedAt,
            quote.AcceptanceMethod, quote.AcceptedBy, quote.DocumentSha256, quote.ValidUntil, quote.CustomerName, quote.BillingAddress, quote.DeliveryAddress,
            quote.PaymentTerms, quote.DeliveryTerms, quote.CommercialTerms, quote.PartyId is not null, lines.Count > 0 && lines.All(l => l.RemainingQuantity <= 0m),
            lines, warnings, blockers, existing));
    }

    public async Task<RecordResult<RecordData>> CreateAsync(Guid quoteId, OrderFromQuoteOptions options, CancellationToken cancellationToken)
    {
        var (quote, error) = await LoadAsync<RecordData>(quoteId, cancellationToken);
        if (quote is null)
        {
            return error!;
        }

        if (quote.PartyId is not { } partyId)
        {
            return Invalid("quoteId", "Teklifin müşterisi ortak müşteri kaydıyla eşleşmedi; sipariş oluşturulamaz.");
        }

        var mode = string.IsNullOrWhiteSpace(options.Mode) ? ConversionMode.Full : options.Mode;
        var consumptions = await reader.ConsumptionsAsync(quoteId, cancellationToken);
        var states = consumptions.ToDictionary(
            c => c.LineKey, c => new ConsumptionState(c.AcceptedQuantity, c.AcceptedNet, c.AcceptedTax, c.ConvertedQuantity, c.ConvertedNet, c.ConvertedTax));
        var plan = OrderConversionPlanner.Plan(quote.Lines, mode, options.Lines, states);
        if (!plan.IsSuccess)
        {
            if (plan.Error == PlanErrorKind.Conflict)
            {
                var existing = await ExistingOrdersAsync(quoteId, cancellationToken);
                var hint = existing.Count > 0 ? $" Mevcut sipariş: {existing[0].Number}." : string.Empty;
                return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, plan.Message + hint, [new RecordFieldError(plan.Field!, plan.Message!)]);
            }

            return Invalid(plan.Field!, plan.Message!);
        }

        if (options.PlannedStartDate is { } start && options.PlannedDeliveryDate is { } delivery && delivery < start)
        {
            return Invalid("plannedDeliveryDate", "Planlanan teslim tarihi başlangıç tarihinden önce olamaz.");
        }

        var net = plan.Lines.Sum(l => l.Net);
        var tax = plan.Lines.Sum(l => l.Tax);
        var ownerId = options.OwnerId ?? (quote.OwnerId is { } quoteOwner && await storeContext.Members.IsActiveMemberAsync(quoteOwner, cancellationToken) ? quoteOwner : null);
        var values = new JsonObject
        {
            ["title"] = Trim(string.IsNullOrWhiteSpace(options.Title) ? quote.Title : options.Title.Trim(), SalesOrder.TitleMaxLength),
            ["partyId"] = partyId.ToString(),
            ["personId"] = quote.PersonId?.ToString(),
            ["sourceQuoteId"] = quote.QuoteId.ToString(),
            ["sourceQuoteNumber"] = quote.QuoteNumber,
            ["sourceQuoteRevisionNo"] = quote.RevisionNo,
            ["sourceQuoteSha256"] = quote.DocumentSha256,
            ["sourceOpportunityId"] = quote.OpportunityId?.ToString(),
            ["conversionMode"] = mode,
            ["acceptedAt"] = quote.AcceptedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["acceptanceMethod"] = quote.AcceptanceMethod,
            ["acceptedByUserId"] = quote.AcceptedByUserId?.ToString(),
            ["acceptedByName"] = quote.AcceptanceMethod == AcceptanceMethods.Portal ? Trim(quote.AcceptedBy, SalesOrder.NameMaxLength) : null,
            ["acceptanceNote"] = Trim(quote.AcceptanceNote, SalesOrder.TextMaxLength),
            ["quoteValidUntil"] = quote.ValidUntil?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["customerName"] = Trim(quote.CustomerName, SalesOrder.NameMaxLength),
            ["billingAddress"] = Trim(quote.BillingAddress, SalesOrder.AddressMaxLength),
            ["deliveryAddress"] = Trim(string.IsNullOrWhiteSpace(options.DeliveryAddress) ? quote.DeliveryAddress : options.DeliveryAddress.Trim(), SalesOrder.AddressMaxLength),
            ["currency"] = quote.Currency,
            ["priceIncludesTax"] = quote.PriceIncludesTax,
            ["reportingCurrency"] = quote.ReportingCurrency,
            ["exchangeRate"] = quote.ExchangeRate,
            ["exchangeRateDate"] = quote.ExchangeRateDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["paymentTerms"] = Trim(quote.PaymentTerms, SalesOrder.TextMaxLength),
            ["deliveryTerms"] = Trim(quote.DeliveryTerms, SalesOrder.TextMaxLength),
            ["commercialTerms"] = Trim(quote.CommercialTerms, SalesOrder.TextMaxLength),
            ["netTotal"] = net,
            ["taxTotal"] = tax,
            ["grossTotal"] = net + tax,
        };
        Set(values, "ownerId", ownerId?.ToString());
        Set(values, "location", Trim(options.Location, SalesOrder.AddressMaxLength));
        Set(values, "commitments", Trim(options.Commitments, SalesOrder.TextMaxLength));
        Set(values, "operationsNotes", Trim(options.OperationsNotes, SalesOrder.TextMaxLength));
        Set(values, "plannedStartDate", options.PlannedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Set(values, "plannedDeliveryDate", options.PlannedDeliveryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var reason = $"Teklif {quote.QuoteNumber} V{quote.RevisionNo}";
        return await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([OrdersModule.Permissions.OrdersCreate], OrderEventKinds.CreatedFromQuote, reason);

            // Tüketim defteri sipariş kaydıyla aynı kaydetmede yazılır: sürüm belirteci ve benzersiz anahtar eşzamanlı ikinci dönüşümü reddeder.
            foreach (var planned in plan.Lines)
            {
                var row = consumptions.FirstOrDefault(c => c.LineKey == planned.Source.StableKey);
                if (row is null)
                {
                    row = new QuoteLineConsumption
                    {
                        QuoteId = quote.QuoteId,
                        LineKey = planned.Source.StableKey,
                        AcceptedQuantity = planned.Source.Quantity,
                        AcceptedNet = planned.Source.Net,
                        AcceptedTax = planned.Source.Tax,
                    };
                    storeContext.Data.Set<QuoteLineConsumption>().Add(row);
                    consumptions.Add(row);
                }
                else
                {
                    row.Touch();
                }

                row.ConvertedQuantity += planned.Quantity;
                row.ConvertedNet += planned.Net;
                row.ConvertedTax += planned.Tax;
            }

            var created = await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.Order, null, values) { Reason = "order.CreatedFromQuote" }, ct);
            if (!created.IsSuccess)
            {
                return created;
            }

            var position = 0;
            foreach (var planned in plan.Lines.OrderBy(l => l.Source.Position))
            {
                var line = planned.Source;
                var lineValues = new JsonObject
                {
                    ["orderId"] = created.Value!.Id.ToString(),
                    ["position"] = ++position,
                    ["kind"] = line.Kind is { Length: > 0 } kind && LineKinds.All.Contains(kind) ? kind : line.ProductCode is null ? LineKinds.Service : LineKinds.Product,
                    ["parentSourceLineKey"] = line.ParentStableKey?.ToString(),
                    ["groupName"] = Trim(line.GroupName, SalesOrderLine.NameMaxLength),
                    ["productId"] = line.ProductId?.ToString(),
                    ["productCode"] = Trim(line.ProductCode, SalesOrderLine.CodeMaxLength),
                    ["name"] = Trim(line.Name, SalesOrderLine.NameMaxLength),
                    ["description"] = Trim(line.Description, SalesOrderLine.TextMaxLength),
                    ["quantity"] = planned.Quantity,
                    ["acceptedQuantity"] = line.Quantity,
                    ["unitName"] = Trim(line.Unit, SalesOrderLine.UnitMaxLength),
                    ["unitPrice"] = planned.Quantity > 0 ? decimal.Round(planned.Net / planned.Quantity, 6) : 0m,
                    ["listAmount"] = planned.ListAmount,
                    ["lineDiscount"] = planned.LineDiscount,
                    ["taxRatePercent"] = line.TaxRatePercent,
                    ["net"] = planned.Net,
                    ["tax"] = planned.Tax,
                    ["gross"] = planned.Gross,
                    ["recurringPeriod"] = line.RecurringPeriod,
                    ["sourceQuoteId"] = quote.QuoteId.ToString(),
                    ["sourceQuoteLineKey"] = line.StableKey.ToString(),
                };
                var added = await pipeline.CreateAsync(new RecordCommand(OrdersModule.Entities.OrderLine, null, lineValues), ct);
                if (!added.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(added.Error!.Code, added.Error.Message, added.Error.FieldErrors);
                }
            }

            return await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, created.Value!.Id), ct);
        }, cancellationToken);
    }

    private async Task<Dictionary<Guid, ConsumptionState>> LedgerAsync(Guid quoteId, CancellationToken cancellationToken) =>
        (await reader.ConsumptionsAsync(quoteId, cancellationToken)).ToDictionary(
            c => c.LineKey, c => new ConsumptionState(c.AcceptedQuantity, c.AcceptedNet, c.AcceptedTax, c.ConvertedQuantity, c.ConvertedNet, c.ConvertedTax));

    /// <summary>Teklifin mevcut siparişleri; yalnızca çağıranın görebildikleri (kapsam kayıt boru hattında uygulanır).</summary>
    private async Task<IReadOnlyList<ExistingOrder>> ExistingOrdersAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var result = await pipeline.QueryAsync(
            new RecordQuery(OrdersModule.Entities.Order, 1, 20, null, new Dictionary<string, string> { ["sourceQuoteId"] = quoteId.ToString() }), cancellationToken);
        return result.IsSuccess
            ? [.. result.Value!.Items.Select(o => new ExistingOrder(o.Id, o.Values["number"]?.GetValue<string>() ?? string.Empty, o.Values["status"]?.GetValue<string>() ?? string.Empty))]
            : [];
    }

    /// <summary>İzin, kaynak ve kabul denetimi; sorun varsa hazır hata sonucu döner.</summary>
    private async Task<(AcceptedQuote? Quote, RecordResult<T>? Error)> LoadAsync<T>(Guid quoteId, CancellationToken cancellationToken)
    {
        var granted = await access.GetPermissionsAsync(cancellationToken);
        // Fiyatlar siparişe sunucu tarafından yazılır; hassas alan yazımı finansal izin ister.
        if (!granted.Contains(OrdersModule.Permissions.OrdersCreate) || !granted.Contains(OrdersModule.Permissions.FinancialsRead))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied,
                $"Tekliften sipariş oluşturmak için '{OrdersModule.Permissions.OrdersCreate}' ve '{OrdersModule.Permissions.FinancialsRead}' izinleri gerekir."));
        }

        if (quotes is null)
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Teklif kaynağı kullanılamıyor; kabul edilmiş teklif bulunamadı."));
        }

        var quote = await quotes.GetAsync(quoteId, cancellationToken);
        return quote is null
            ? (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Kabul edilmiş teklif bulunamadı."))
            : (quote, null);
    }

    private static void Set(JsonObject values, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[field] = value;
        }
    }

    private static string? Trim(string? text, int max) => text is { Length: > 0 } && text.Length > max ? text[..max] : text;

    private static RecordResult<RecordData> Invalid(string field, string message) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
