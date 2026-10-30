using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Pricing;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Quote iş akışı kapısı (istek ömürlü): durum, onay, gönderim ve toplam alanları genel kayıt uçlarından yazılamaz; yalnızca
/// <see cref="IQuoteWorkflow"/> kapıyı açarak boru hattı üzerinden yazar.
/// </summary>
internal sealed class QuoteWorkflowGate
{
    public IReadOnlyList<string>? Permissions { get; private set; }

    public bool IsOpen => Permissions is not null;

    /// <summary>Tarihçe satırının türü ve gerekçesi; depo bunları geçişle aynı işlemde yazar.</summary>
    public string? EventKind { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>
    /// Geçişle aynı işlemde çalışan ek yazma (belge, paylaşım bağlantısı, müşteri kararı). Teklif toplamının parçalarıdır; bu yüzden
    /// teklifin sürüm denetimine ve audit'ine bağlı yazılırlar (eşzamanlı çakışmada hiçbiri yazılmaz).
    /// </summary>
    public Func<CrmStoreContext, Quote, CancellationToken, Task>? Effect { get; private set; }

    public IDisposable Open(
        IReadOnlyList<string> permissions, string eventKind, string? reason = null, Func<CrmStoreContext, Quote, CancellationToken, Task>? effect = null)
    {
        Permissions = permissions;
        EventKind = eventKind;
        Reason = reason;
        Effect = effect;
        return new Closer(this);
    }

    private sealed class Closer(QuoteWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permissions = null;
            gate.EventKind = null;
            gate.Reason = null;
            gate.Effect = null;
        }
    }
}

/// <summary>
/// Teklif toplamlarının sunucu tarafı yeniden hesabı: kalemleri okur, <see cref="QuoteCalculator"/> ile hesaplar, kalem ve başlık
/// toplamlarını yazar. İstemciden gelen tutarlar hiçbir zaman kaynak değildir. Kayıt boru hattı değişiklikleri tek işlemde kaydeder.
/// </summary>
internal static class QuoteTotals
{
    public static async Task RecalculateAsync(
        IModuleData data, Quote quote, IReadOnlyList<QuoteLine>? pending, IReadOnlyCollection<Guid>? removedIds, string errorField, CancellationToken cancellationToken)
    {
        var lines = await data.Set<QuoteLine>().Where(l => l.QuoteId == quote.Id).ToListAsync(cancellationToken);
        if (removedIds is { } removed)
        {
            lines.RemoveAll(l => removed.Contains(l.Id));
        }

        foreach (QuoteLine add in pending ?? [])
        {
            if (lines.All(l => l.Id != add.Id))
            {
                lines.Add(add);
            }
        }

        lines = [.. lines.OrderBy(l => l.Position).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)];

        var kind = Enum.TryParse<QuoteDiscountKind>(quote.DocumentDiscountKind, out var parsed) ? parsed : QuoteDiscountKind.None;
        QuoteCalculation calc;
        try
        {
            calc = QuoteCalculator.Calculate(
                [.. lines.Select(ToInput)], new QuotePolicy(quote.PriceIncludesTax, quote.CalculationPolicyVersion), kind, quote.DocumentDiscountValue);
        }
        catch (PricingException ex)
        {
            throw new RecordValidationException([new RecordFieldError(FieldFor(ex.Code, errorField), ex.Message)]);
        }

        var results = calc.Lines.ToDictionary(r => r.Id);
        foreach (QuoteLine line in lines)
        {
            var r = results[line.StableKey];
            line.Included = r.Included;
            line.ListAmount = r.ListAmount;
            line.LineDiscount = r.LineDiscount;
            line.DocumentDiscountShare = r.DocumentDiscountShare;
            line.Net = r.Net;
            line.Tax = r.Tax;
            line.Gross = r.Gross;
        }

        quote.ListTotal = calc.ListTotal;
        quote.DiscountTotal = calc.LineDiscountTotal + calc.DocumentDiscountTotal;
        quote.NetTotal = calc.OneTime.Net;
        quote.TaxTotal = calc.OneTime.Tax;
        quote.GrossTotal = calc.OneTime.Gross;
        quote.OptionalNet = calc.OptionalNet;
        quote.RecurringSummary = calc.Recurring.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(calc.Recurring.Select(p => new { period = p.Period, net = p.Net, tax = p.Tax, gross = p.Gross }));
        quote.CalculatedAt = DateTimeOffset.UtcNow;
    }

    private static QuoteLineInput ToInput(QuoteLine l) => new(
        l.StableKey, l.Quantity, l.UnitPrice, l.TaxRatePercent,
        Enum.TryParse<QuoteLineRole>(l.Role, out var role) ? role : QuoteLineRole.Standard,
        Enum.TryParse<QuoteDiscountKind>(l.DiscountKind, out var kind) ? kind : QuoteDiscountKind.None,
        l.DiscountValue, l.IsSelected, l.ParentStableKey, l.RecurringPeriod);

    private static string FieldFor(string code, string fallback) => code switch
    {
        QuoteErrors.InvalidQuantity => "quantity",
        QuoteErrors.NegativePrice => "unitPrice",
        QuoteErrors.InvalidParent => "parentStableKey",
        PricingErrors.InvalidRate => "taxRatePercent",
        _ => fallback,
    };
}
