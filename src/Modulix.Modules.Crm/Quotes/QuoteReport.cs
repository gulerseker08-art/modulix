using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Crm.Quotes;

public sealed record QuoteReportQuery(DateOnly From, DateOnly To, Guid? OwnerId);

// Rapor sonucu. Alan adları Shared'daki QuoteReportDto ile birebir aynıdır (uç nokta JSON üzerinden eşler).
public sealed record QuoteCurrencyTotal(string Currency, int Count, decimal Gross, decimal Net, decimal Discount);

public sealed record QuoteReportOwner(Guid? OwnerId, int Presented, int Accepted, int Rejected, IReadOnlyList<QuoteCurrencyTotal> AcceptedTotals);

public sealed record QuoteReportItem(Guid Id, string QuoteNumber, int RevisionNo, string Title, Guid? OwnerId, string Currency, decimal Gross, DateOnly? Date, string? Note);

public sealed record QuoteReportResult(
    DateOnly From,
    DateOnly To,
    bool Truncated,
    int DraftCount,
    int PendingApprovalCount,
    int WaitingCount,
    IReadOnlyList<QuoteCurrencyTotal> WaitingTotals,
    int PresentedCount,
    IReadOnlyList<QuoteCurrencyTotal> PresentedTotals,
    int AcceptedCount,
    IReadOnlyList<QuoteCurrencyTotal> AcceptedTotals,
    int RejectedCount,
    int CancelledCount,
    int ExpiredCount,
    double? AcceptanceRate,
    double? AverageDaysToDecision,
    double? AverageDiscountPercent,
    double? AverageRevisionsToAccept,
    IReadOnlyList<QuoteReportOwner> ByOwner,
    IReadOnlyList<QuoteReportItem> ExpiringSoon,
    IReadOnlyList<QuoteReportItem> Overdue,
    IReadOnlyList<QuoteReportItem> AwaitingApproval,
    IReadOnlyList<QuoteReportItem> NotDelivered,
    IReadOnlyList<string> Definitions);

public interface IQuoteReport
{
    Task<RecordResult<QuoteReportResult>> BuildAsync(QuoteReportQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Teklif raporu (ADR-0019, dilim 6). Veriyi kayıt boru hattından okur: izin ve veri kapsamı liste ekranıyla aynıdır; kapsam dışı teklif hiçbir
/// toplamda yer almaz. Rapor yalnızca ailelerin <b>son revizyonunu</b> sayar. Para birimleri toplanmaz (her toplam para birimi bazındadır).
/// Kalem maliyeti ve iç not rapora hiç girmez (marj raporu yoktur). Hesap bugünkü kayıt değerlerindendir; geçmiş dönem anlık görüntüsü yoktur.
/// </summary>
internal sealed class QuoteReport(IRecordPipeline pipeline, TimeProvider time) : IQuoteReport
{
    /// <summary>Bir okumada en fazla teklif (her okuma için ayrı).</summary>
    public const int MaxRows = 5000;

    private const int ListLimit = 50;
    private const int ExpiringDays = 7;

    public static IReadOnlyList<string> Definitions { get; } =
    [
        "Yalnızca her teklif ailesinin son revizyonu sayılır; eski revizyonlar ayrı satış gibi görünmez. Revizyonlu teklif ailesi tek kez sayılır.",
        "Bekleyen (müşteride): durumu \"Müşteriye sunuldu\" olan ve geçerliliği dolmamış teklifler. Taslak ve onay bekleyenler ayrı sayılır.",
        "Sunulan: sunum tarihi [Başlangıç, Bitiş] içinde olan teklifler. Karar: karar tarihi bu aralıkta olan kabul, ret, iptal ve süresi dolanlar.",
        "Kabul oranı = Kabul / (Kabul + Ret). İptal ve süresi dolanlar paydaya girmez. Payda sıfırsa oran tanımsızdır.",
        "Karar süresi = sunum tarihinden karar tarihine gün (yalnızca her iki tarihi olan kabul ve retler).",
        "Ortalama indirim = sunulan tekliflerin liste toplamına göre indirim yüzdelerinin ortalaması (teklif başına; para birimlerini karıştırmaz).",
        "Kabul için ortalama revizyon = kabul edilen tekliflerin revizyon numarasının ortalaması.",
        "Para birimleri toplanmaz: her toplam para birimi bazındadır. Kur dönüşümü yapılmaz. Tutarlar vergi dahil genel toplamdır; net ve indirim ayrıca verilir.",
        "Süresi yaklaşan: müşteride bekleyen ve geçerliliği önümüzdeki 7 gün içinde bitenler. Süresi geçmiş: hâlâ \"sunuldu\" ama geçerliliği dolmuş olanlar (zamanlanmış süre dolumu işlediğinde \"süresi doldu\" olur).",
        "Gönderilmemiş: sunulmuş ancak e-posta gönderimi yapılmamış ya da başarısız olmuş teklifler. \"Sağlayıcı kabul etti\" teslim anlamına gelmez; teslim bilgisi bu raporda yoktur.",
        "Maliyet, marj ve iç notlar rapora girmez.",
    ];

    public async Task<RecordResult<QuoteReportResult>> BuildAsync(QuoteReportQuery query, CancellationToken cancellationToken)
    {
        if (query.To < query.From)
        {
            return RecordResult<QuoteReportResult>.Fail(RecordErrorCode.ValidationFailed, "Bitiş tarihi başlangıçtan önce olamaz.",
                [new RecordFieldError("to", "Bitiş tarihi başlangıçtan önce olamaz.")]);
        }

        var owner = query.OwnerId is { } id ? new Dictionary<string, string> { ["ownerId"] = id.ToString() } : [];
        var drafts = await ReadAsync(Merge(owner, "latestOnly", "true", "status", "Draft"), cancellationToken);
        var presentedNow = await ReadAsync(Merge(owner, "latestOnly", "true", "status", "Presented"), cancellationToken);
        var presented = await ReadAsync(Merge(owner, "latestOnly", "true", "presentedFrom", Iso(query.From), "presentedTo", Iso(query.To)), cancellationToken);
        var decided = await ReadAsync(Merge(owner, "latestOnly", "true", "decidedFrom", Iso(query.From), "decidedTo", Iso(query.To)), cancellationToken);
        var failed = new[] { drafts, presentedNow, presented, decided }.FirstOrDefault(p => p.Error is not null);
        if (failed is not null)
        {
            return RecordResult<QuoteReportResult>.Fail(failed.Error!.Code, failed.Error.Message, failed.Error.FieldErrors);
        }

        var now = time.GetUtcNow();
        var waiting = presentedNow.Rows.Where(r => r.ValidUntil is null || r.ValidUntil > now).ToList();
        var accepted = decided.Rows.Where(r => r.Status == QuoteStatus.Accepted).ToList();
        var rejected = decided.Rows.Where(r => r.Status == QuoteStatus.Rejected).ToList();
        var cancelled = decided.Rows.Count(r => r.Status == QuoteStatus.Cancelled);
        var expired = decided.Rows.Count(r => r.Status == QuoteStatus.Expired);
        var decisive = accepted.Count + rejected.Count;

        var days = decided.Rows.Where(r => r.Status is QuoteStatus.Accepted or QuoteStatus.Rejected && r.PresentedAt is not null && r.DecidedAt is not null)
            .Select(r => (r.DecidedAt!.Value - r.PresentedAt!.Value).TotalDays).ToList();
        var discounts = presented.Rows.Where(r => r.ListTotal > 0).Select(r => (double)(r.DiscountTotal * 100m / r.ListTotal)).ToList();

        var owners = presented.Rows.Select(r => r.OwnerId).Concat(decided.Rows.Select(r => r.OwnerId)).Distinct()
            .Select(o => new QuoteReportOwner(
                o, presented.Rows.Count(r => r.OwnerId == o), accepted.Count(r => r.OwnerId == o), rejected.Count(r => r.OwnerId == o),
                Totals(accepted.Where(r => r.OwnerId == o))))
            .OrderByDescending(o => o.Presented).ThenBy(o => o.OwnerId).ToList();

        QuoteReportItem Item(Row r, DateOnly? date, string? note = null) => new(r.Id, r.Number, r.RevisionNo, r.Title, r.OwnerId, r.Currency, r.GrossTotal, date, note);
        DateOnly? Day(DateTimeOffset? at) => at is { } t ? DateOnly.FromDateTime(t.UtcDateTime) : null;

        var soon = now.AddDays(ExpiringDays);
        var result = new QuoteReportResult(
            query.From, query.To, new[] { drafts, presentedNow, presented, decided }.Any(p => p.Truncated),
            drafts.Rows.Count(r => r.ApprovalStatus != QuoteApprovalStatus.Pending), drafts.Rows.Count(r => r.ApprovalStatus == QuoteApprovalStatus.Pending),
            waiting.Count, Totals(waiting), presented.Rows.Count, Totals(presented.Rows),
            accepted.Count, Totals(accepted), rejected.Count, cancelled, expired,
            decisive == 0 ? null : Math.Round((double)accepted.Count / decisive, 4),
            days.Count == 0 ? null : Math.Round(days.Average(), 1),
            discounts.Count == 0 ? null : Math.Round(discounts.Average(), 2),
            accepted.Count == 0 ? null : Math.Round(accepted.Average(r => (double)r.RevisionNo), 2),
            owners,
            [.. waiting.Where(r => r.ValidUntil is { } v && v <= soon).OrderBy(r => r.ValidUntil).Take(ListLimit).Select(r => Item(r, Day(r.ValidUntil)))],
            [.. presentedNow.Rows.Where(r => r.ValidUntil is { } v && v <= now).OrderBy(r => r.ValidUntil).Take(ListLimit).Select(r => Item(r, Day(r.ValidUntil)))],
            [.. drafts.Rows.Where(r => r.ApprovalStatus == QuoteApprovalStatus.Pending).OrderBy(r => r.Number).Take(ListLimit).Select(r => Item(r, null))],
            [.. presentedNow.Rows.Where(r => r.DeliveryStatus is QuoteDeliveryStatus.NotSent or QuoteDeliveryStatus.Failed)
                .OrderBy(r => r.PresentedAt).Take(ListLimit).Select(r => Item(r, Day(r.PresentedAt), r.DeliveryStatus))],
            Definitions);
        return RecordResult<QuoteReportResult>.Ok(result);
    }

    // ---- Okuma

    private sealed record Row(
        Guid Id, string Number, int RevisionNo, string Title, Guid? OwnerId, string Status, string ApprovalStatus, string DeliveryStatus, string Currency,
        decimal ListTotal, decimal DiscountTotal, decimal NetTotal, decimal GrossTotal, DateTimeOffset? ValidUntil, DateTimeOffset? PresentedAt, DateTimeOffset? DecidedAt);

    private sealed record Page(List<Row> Rows, bool Truncated, RecordError? Error);

    private async Task<Page> ReadAsync(Dictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        var pageNumber = 1;
        while (rows.Count < MaxRows)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Quote, pageNumber, RecordQuery.MaxPageSize, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return new Page([], false, result.Error);
            }

            rows.AddRange(result.Value!.Items.Select(ToRow));
            if (result.Value.Items.Count == 0 || rows.Count >= result.Value.TotalCount)
            {
                return new Page(rows, false, null);
            }

            pageNumber++;
        }

        return new Page(rows, true, null);
    }

    private static Row ToRow(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        decimal Money(string name) => v[name] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : 0m;
        DateTimeOffset? Time(string name) => DateTimeOffset.TryParse(Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var guid) ? guid : null;

        return new Row(
            record.Id, Text("quoteNumber") ?? string.Empty, v["revisionNo"] is JsonValue rev && rev.TryGetValue<int>(out var n) ? n : 1, Text("title") ?? string.Empty,
            Id("ownerId"), Text("status") ?? QuoteStatus.Draft, Text("approvalStatus") ?? QuoteApprovalStatus.NotRequired,
            Text("deliveryStatus") ?? QuoteDeliveryStatus.NotSent, Text("currency") ?? "TRY", Money("listTotal"), Money("discountTotal"), Money("netTotal"), Money("grossTotal"),
            Time("validUntil"), Time("presentedAt"), Time("decidedAt"));
    }

    // ---- Hesap

    private static IReadOnlyList<QuoteCurrencyTotal> Totals(IEnumerable<Row> rows) =>
        [.. rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new QuoteCurrencyTotal(g.Key, g.Count(), g.Sum(r => r.GrossTotal), g.Sum(r => r.NetTotal), g.Sum(r => r.DiscountTotal)))];

    private static Dictionary<string, string> Merge(Dictionary<string, string> owner, params string[] pairs)
    {
        var filters = new Dictionary<string, string>(owner);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            filters[pairs[i]] = pairs[i + 1];
        }

        return filters;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
