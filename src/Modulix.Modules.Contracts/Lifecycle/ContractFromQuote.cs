using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Contracts.Sales;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Sözleşme oluşturma seçenekleri (kabul edilmiş tekliften). Boş alanlar sunucu varsayılanını alır.</summary>
/// <param name="TermMonths">Süre (ay): verilirse <paramref name="StartDate"/> zorunludur ve bitiş takvim ayıyla hesaplanır (365 gün değil).</param>
/// <param name="LineKeys">Sözleşmeye alınacak teklif kalemlerinin kararlı anahtarları; boşsa teklifin tüm kalemleri (kısmi kapsam için alt küme).</param>
public sealed record ContractFromQuoteOptions(
    string? Type = null,
    DateOnly? StartDate = null,
    int? TermMonths = null,
    string? RenewalMode = null,
    int? RenewalPeriodMonths = null,
    int? NoticePeriodDays = null,
    IReadOnlyList<Guid>? LineKeys = null,
    Guid? OwnerId = null);

/// <summary>Teklif kalemi ve sözleşmedeki durumu: aynı kalem aynı anda ikinci bir sözleşmeye alınamaz.</summary>
public sealed record QuoteSourceLine(
    Guid StableKey, int Position, string Name, decimal Quantity, string? Unit, decimal Net, decimal Gross, string? RecurringPeriod,
    bool Taken, Guid? ContractId, string? ContractNumber);

public sealed record QuoteSourcePreview(
    Guid QuoteId, string QuoteNumber, int RevisionNo, string Title, string Currency, decimal NetTotal, decimal GrossTotal, DateTimeOffset AcceptedAt,
    string? AcceptedBy, string? DocumentSha256, bool CustomerLinked, IReadOnlyList<QuoteSourceLine> Lines);

/// <summary>
/// Kabul edilmiş tekliften sözleşme taslağı oluşturma (ADR-0022, dilim 3). Teklif <see cref="IAcceptedQuoteSource"/> ile (CRM'e bağlanmadan)
/// okunur; CRM kapalıysa ya da teklif görünmüyorsa "bulunamadı". Teklif sözleşmeyle bire bir kabul edilmez: kalem alt kümesi seçilerek
/// bir tekliften birden çok sözleşme açılabilir, ancak aynı teklif kalemi aynı anda yalnızca bir sözleşmede olur (mükerrer devir 409;
/// yarışta veritabanı ikincisini reddeder, sözleşme iptalinde kalem serbest kalır). Fiyatlar teklifin imza anındaki halidir (indirim sonrası
/// net birim fiyat); teklif ya da katalog sonradan değişse de sözleşme değişmez. Tek işlemdir: kalemlerden biri reddedilirse hiçbir şey yazılmaz.
/// </summary>
public interface IContractFromQuote
{
    Task<RecordResult<QuoteSourcePreview>> PreviewAsync(Guid quoteId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> CreateAsync(Guid quoteId, ContractFromQuoteOptions options, CancellationToken cancellationToken);
}

internal sealed class ContractFromQuote(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    IAcceptedQuoteSource? quotes = null) : IContractFromQuote
{
    public async Task<RecordResult<QuoteSourcePreview>> PreviewAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var (quote, error) = await LoadAsync<QuoteSourcePreview>(quoteId, cancellationToken);
        if (quote is null)
        {
            return error!;
        }

        var used = await reader.UsedQuoteLinesAsync(quoteId, cancellationToken);
        var visible = new Dictionary<Guid, string?>();
        foreach (var contractId in used.Values.Distinct())
        {
            // Sözleşme numarası yalnızca çağıranın görebildiği sözleşme için gösterilir; aksi halde yalnızca "alınmış" bilgisi verilir.
            var contract = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, contractId), cancellationToken);
            visible[contractId] = contract.IsSuccess ? contract.Value!.Values["number"]?.GetValue<string>() : null;
        }

        var lines = quote.Lines.Select(l => new QuoteSourceLine(
            l.StableKey, l.Position, l.Name, l.Quantity, l.Unit, l.Net, l.Gross, l.RecurringPeriod,
            used.ContainsKey(l.StableKey),
            used.TryGetValue(l.StableKey, out var cid) && visible.TryGetValue(cid, out var number) && number is not null ? cid : null,
            used.TryGetValue(l.StableKey, out var cid2) && visible.TryGetValue(cid2, out var number2) ? number2 : null)).ToList();
        return RecordResult<QuoteSourcePreview>.Ok(new QuoteSourcePreview(
            quote.QuoteId, quote.QuoteNumber, quote.RevisionNo, quote.Title, quote.Currency, quote.NetTotal, quote.GrossTotal, quote.AcceptedAt,
            quote.AcceptedBy, quote.DocumentSha256, quote.PartyId is not null, lines));
    }

    public async Task<RecordResult<RecordData>> CreateAsync(Guid quoteId, ContractFromQuoteOptions options, CancellationToken cancellationToken)
    {
        var (quote, error) = await LoadAsync<RecordData>(quoteId, cancellationToken);
        if (quote is null)
        {
            return error!;
        }

        if (quote.PartyId is not { } partyId)
        {
            return Invalid("partyId", "Teklifin müşterisi ortak kayıtla eşleşmedi; sözleşme boş taslaktan açılmalıdır.");
        }

        var keys = options.LineKeys is { Count: > 0 } requested ? requested.Distinct().ToList() : [.. quote.Lines.Select(l => l.StableKey)];
        var selected = quote.Lines.Where(l => keys.Contains(l.StableKey)).OrderBy(l => l.Position).ToList();
        if (selected.Count == 0 || selected.Count != keys.Count)
        {
            return Invalid("lineKeys", selected.Count == 0 ? "Sözleşmeye alınacak en az bir teklif kalemi seçin." : "Seçilen kalemlerden biri bu teklifte yok.");
        }

        if (options.TermMonths is { } months && options.StartDate is null)
        {
            return Invalid("startDate", "Süre (ay) verildiğinde başlangıç tarihi de girilmelidir.");
        }

        if (options.TermMonths is < 1 or > 120)
        {
            return Invalid("termMonths", "Süre 1 ile 120 ay arasında olmalıdır.");
        }

        var used = await reader.UsedQuoteLinesAsync(quoteId, cancellationToken);
        if (selected.Where(l => used.ContainsKey(l.StableKey)).Select(l => l.Name).ToList() is { Count: > 0 } taken)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Seçilen teklif kalemleri zaten bir sözleşmede: " + string.Join(", ", taken) + ".",
                [new RecordFieldError("lineKeys", "Kalem başka bir sözleşmede kullanılıyor.")]);
        }

        var endDate = options.TermMonths is { } term && options.StartDate is { } start ? ContractPeriod.TermEnd(start, term) : (DateOnly?)null;
        var values = new JsonObject
        {
            ["title"] = Trim(quote.Title, 200),
            ["partyId"] = partyId.ToString(),
            ["personId"] = quote.PersonId?.ToString(),
            ["currency"] = quote.Currency,
            // Satır fiyatı indirim sonrası net birim fiyattır; vergi satır başına ayrıca hesaplanır. Bu yüzden sözleşme vergi hariç fiyatlanır.
            ["priceIncludesTax"] = false,
            ["paymentTerms"] = Trim(quote.PaymentTerms, 4000),
            ["description"] = Description(quote),
            ["sourceQuoteId"] = quote.QuoteId.ToString(),
            ["sourceQuoteNumber"] = quote.QuoteNumber,
            ["sourceQuoteRevisionNo"] = quote.RevisionNo,
            ["sourceQuoteSha256"] = quote.DocumentSha256,
            ["sourceOpportunityId"] = quote.OpportunityId?.ToString(),
        };
        Set(values, "type", options.Type);
        Set(values, "ownerId", options.OwnerId?.ToString());
        Set(values, "startDate", options.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Set(values, "endDate", endDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Set(values, "renewalMode", options.RenewalMode);
        if (options.RenewalPeriodMonths is { } renewal)
        {
            values["renewalPeriodMonths"] = renewal;
        }

        if (options.NoticePeriodDays is { } notice)
        {
            values["noticePeriodDays"] = notice;
        }

        var reason = $"Teklif {quote.QuoteNumber} V{quote.RevisionNo}";
        return await pipeline.RunAtomicAsync(async ct =>
        {
            using var _ = gate.Open([ContractsModule.Permissions.ContractsWrite], ContractEventKinds.CreatedFromQuote, reason);
            var created = await pipeline.CreateAsync(new RecordCommand(ContractsModule.Entities.Contract, null, values) { Reason = "contract.CreatedFromQuote" }, ct);
            if (!created.IsSuccess)
            {
                return created;
            }

            foreach (var line in selected)
            {
                var price = line.Quantity > 0 ? decimal.Round(line.Net / line.Quantity, 6) : 0m;
                var lineValues = new JsonObject
                {
                    ["contractId"] = created.Value!.Id.ToString(),
                    ["kind"] = line.ProductCode is null ? LineKinds.Service : LineKinds.Product,
                    ["productCode"] = line.ProductCode,
                    ["name"] = Trim(line.Name, 200),
                    ["description"] = Trim(line.Description, 2000),
                    ["quantity"] = line.Quantity,
                    ["unitName"] = Trim(line.Unit, 50),
                    ["unitPrice"] = price,
                    ["taxRatePercent"] = line.TaxRatePercent,
                    ["billingPeriod"] = line.RecurringPeriod,
                    ["sourceQuoteLineKey"] = line.StableKey.ToString(),
                    ["sourceQuoteId"] = quote.QuoteId.ToString(),
                };
                var added = await pipeline.CreateAsync(new RecordCommand(ContractsModule.Entities.ContractLine, null, lineValues), ct);
                if (!added.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(added.Error!.Code, added.Error.Message, added.Error.FieldErrors);
                }
            }

            return await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, created.Value!.Id), ct);
        }, cancellationToken);
    }

    /// <summary>İzin, kaynak ve kabul denetimi; sorun varsa hazır hata sonucu döner.</summary>
    private async Task<(AcceptedQuote? Quote, RecordResult<T>? Error)> LoadAsync<T>(Guid quoteId, CancellationToken cancellationToken)
    {
        var granted = await access.GetPermissionsAsync(cancellationToken);
        // Fiyatlar sözleşmeye sunucu tarafından yazılır; hassas alan yazımı finansal izin ister.
        if (!granted.Contains(ContractsModule.Permissions.ContractsWrite) || !granted.Contains(ContractsModule.Permissions.FinancialsRead))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied,
                $"Tekliften sözleşme oluşturmak için '{ContractsModule.Permissions.ContractsWrite}' ve '{ContractsModule.Permissions.FinancialsRead}' izinleri gerekir."));
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

    private static string Description(AcceptedQuote quote)
    {
        var text = $"Kabul edilen teklif {quote.QuoteNumber} (V{quote.RevisionNo}) üzerinden oluşturuldu. Kabul: {quote.AcceptedBy ?? "—"}, {quote.AcceptedAt:yyyy-MM-dd}. "
            + "Kalem fiyatları teklifin indirim sonrası net birim fiyatıdır.";
        if (!string.IsNullOrWhiteSpace(quote.DeliveryTerms))
        {
            text += $"\nTeslimat koşulları: {quote.DeliveryTerms}";
        }

        if (!string.IsNullOrWhiteSpace(quote.CommercialTerms))
        {
            text += $"\nTicari koşullar: {quote.CommercialTerms}";
        }

        return Trim(text, 4000)!;
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
