using System.Globalization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>Bir alanın iki sürüm arasındaki farkı (metin olarak biçimlenmiş).</summary>
public sealed record QuoteFieldChange(string Field, string Label, string? Before, string? After);

/// <summary>Kalem farkı. <c>Change</c>: Added, Removed, Changed ya da Unchanged. Kalemler revizyonlar arasında kararlı anahtarla eşlenir.</summary>
public sealed record QuoteLineChange(string Change, Guid StableKey, string Name, IReadOnlyList<QuoteFieldChange> Fields);

public sealed record QuoteComparison(
    int LeftRevision, int RightRevision, string LeftStatus, string RightStatus, string Currency, string RightCurrency,
    IReadOnlyList<QuoteFieldChange> Header, IReadOnlyList<QuoteFieldChange> Totals, IReadOnlyList<QuoteLineChange> Lines);

/// <summary>İki teklif sürümünü karşılaştırır. Aynı ailede olmaları gerekir; maliyet ve iç not karşılaştırmaya girmez.</summary>
public interface IQuoteComparison
{
    Task<RecordResult<QuoteComparison>> CompareAsync(Guid leftId, Guid rightId, CancellationToken cancellationToken);
}

/// <summary>Saf karşılaştırma (veritabanı yok). Biçim Türkçe sayı gösterimidir; boş değerler "—" olur.</summary>
public static class QuoteComparer
{
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    internal static QuoteComparison Compare(Quote left, IReadOnlyList<QuoteLine> leftLines, Quote right, IReadOnlyList<QuoteLine> rightLines)
    {
        var header = new List<QuoteFieldChange>();
        Add(header, "title", "Başlık", left.Title, right.Title);
        Add(header, "description", "Açıklama", left.Description, right.Description);
        Add(header, "validUntil", "Geçerlilik bitişi", Time(left.ValidUntil), Time(right.ValidUntil));
        Add(header, "currency", "Para birimi", left.Currency, right.Currency);
        Add(header, "priceIncludesTax", "Fiyatlar vergi dahil", Bool(left.PriceIncludesTax), Bool(right.PriceIncludesTax));
        Add(header, "documentDiscount", "Genel indirim", Discount(left.DocumentDiscountKind, left.DocumentDiscountValue), Discount(right.DocumentDiscountKind, right.DocumentDiscountValue));
        Add(header, "paymentTerms", "Ödeme koşulları", left.PaymentTerms, right.PaymentTerms);
        Add(header, "deliveryTerms", "Teslimat koşulları", left.DeliveryTerms, right.DeliveryTerms);
        Add(header, "commercialTerms", "Ticari koşullar", left.CommercialTerms, right.CommercialTerms);

        var totals = new List<QuoteFieldChange>();
        Add(totals, "listTotal", "Liste toplamı", Money(left.ListTotal), Money(right.ListTotal));
        Add(totals, "discountTotal", "Toplam indirim", Money(left.DiscountTotal), Money(right.DiscountTotal));
        Add(totals, "netTotal", "Net toplam", Money(left.NetTotal), Money(right.NetTotal));
        Add(totals, "taxTotal", "Vergi", Money(left.TaxTotal), Money(right.TaxTotal));
        Add(totals, "grossTotal", "Genel toplam", Money(left.GrossTotal), Money(right.GrossTotal));
        Add(totals, "optionalNet", "Opsiyonel/alternatif (net)", Money(left.OptionalNet), Money(right.OptionalNet));

        var before = leftLines.ToDictionary(l => l.StableKey);
        var after = rightLines.ToDictionary(l => l.StableKey);
        var lines = new List<QuoteLineChange>();
        foreach (var line in rightLines.OrderBy(l => l.Position))
        {
            if (!before.TryGetValue(line.StableKey, out var old))
            {
                lines.Add(new QuoteLineChange("Added", line.StableKey, line.Name, [new QuoteFieldChange("net", "Net tutar", null, Money(line.Net))]));
                continue;
            }

            var changes = LineChanges(old, line);
            lines.Add(new QuoteLineChange(changes.Count == 0 ? "Unchanged" : "Changed", line.StableKey, line.Name, changes));
        }

        foreach (var line in leftLines.Where(l => !after.ContainsKey(l.StableKey)).OrderBy(l => l.Position))
        {
            lines.Add(new QuoteLineChange("Removed", line.StableKey, line.Name, [new QuoteFieldChange("net", "Net tutar", Money(line.Net), null)]));
        }

        return new QuoteComparison(left.RevisionNo, right.RevisionNo, left.Status, right.Status, left.Currency, right.Currency, header, totals, lines);
    }

    private static List<QuoteFieldChange> LineChanges(QuoteLine a, QuoteLine b)
    {
        var changes = new List<QuoteFieldChange>();
        Add(changes, "name", "Ad", a.Name, b.Name);
        Add(changes, "description", "Açıklama", a.Description, b.Description);
        Add(changes, "quantity", "Miktar", Number(a.Quantity), Number(b.Quantity));
        Add(changes, "unit", "Birim", a.UnitName, b.UnitName);
        Add(changes, "unitPrice", "Birim fiyat", Money(a.UnitPrice), Money(b.UnitPrice));
        Add(changes, "discount", "İndirim", Discount(a.DiscountKind, a.DiscountValue), Discount(b.DiscountKind, b.DiscountValue));
        Add(changes, "taxRatePercent", "Vergi oranı", $"%{Number(a.TaxRatePercent)}", $"%{Number(b.TaxRatePercent)}");
        Add(changes, "role", "Rol", a.Role, b.Role);
        Add(changes, "isSelected", "Seçili", Bool(a.IsSelected), Bool(b.IsSelected));
        Add(changes, "recurringPeriod", "Tekrarlayan dönem", a.RecurringPeriod, b.RecurringPeriod);
        Add(changes, "net", "Net tutar", Money(a.Net), Money(b.Net));
        return changes;
    }

    private static void Add(List<QuoteFieldChange> target, string field, string label, string? before, string? after)
    {
        var x = string.IsNullOrWhiteSpace(before) ? null : before.Trim();
        var y = string.IsNullOrWhiteSpace(after) ? null : after.Trim();
        if (!string.Equals(x, y, StringComparison.Ordinal))
        {
            target.Add(new QuoteFieldChange(field, label, x, y));
        }
    }

    private static string Money(decimal value) => value.ToString("N2", Numbers);

    private static string Number(decimal value) => value.ToString("0.####", Numbers);

    private static string Bool(bool value) => value ? "Evet" : "Hayır";

    private static string? Time(DateTimeOffset? value) => value?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string? Discount(string kind, decimal value) => kind switch
    {
        "Percent" when value > 0 => $"%{Number(value)}",
        "Amount" when value > 0 => Money(value),
        _ => null,
    };
}

internal sealed class QuoteComparisonService(IRecordPipeline pipeline, QuoteReader reader) : IQuoteComparison
{
    public async Task<RecordResult<QuoteComparison>> CompareAsync(Guid leftId, Guid rightId, CancellationToken cancellationToken)
    {
        // Görünürlük (izin ve veri kapsamı) iki sürüm için de boru hattında doğrulanır; kapsam dışı olan "bulunamadı" görünür.
        foreach (var id in new[] { leftId, rightId })
        {
            var record = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, id), cancellationToken);
            if (!record.IsSuccess)
            {
                return RecordResult<QuoteComparison>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
            }
        }

        var left = await reader.QuoteAsync(leftId, cancellationToken);
        var right = await reader.QuoteAsync(rightId, cancellationToken);
        if (left is null || right is null)
        {
            return RecordResult<QuoteComparison>.Fail(RecordErrorCode.NotFound, "Teklif bulunamadı.");
        }

        if (left.FamilyId != right.FamilyId || left.Id == right.Id)
        {
            const string message = "Yalnızca aynı teklif ailesinin iki farklı revizyonu karşılaştırılır.";
            return RecordResult<QuoteComparison>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError("with", message)]);
        }

        return RecordResult<QuoteComparison>.Ok(QuoteComparer.Compare(
            left, await reader.LinesAsync(leftId, cancellationToken), right, await reader.LinesAsync(rightId, cancellationToken)));
    }
}
