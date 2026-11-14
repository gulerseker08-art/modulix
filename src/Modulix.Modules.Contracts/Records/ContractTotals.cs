using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Pricing;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme toplamlarının sunucu tarafı hesabı: kalemleri okur, Core'un tek hesap motoru <see cref="QuoteCalculator"/> ile hesaplar
/// (satır indirimi → vergi; aynı yuvarlama), kalem ve başlık toplamlarını yazar. Tek seferlik ve dönemsel kalemler ayrı toplanır;
/// dönemsel tutar sözleşme süresiyle çarpılarak genel toplama katılmaz. İstemciden gelen tutarlar hiçbir zaman kaynak değildir.
/// </summary>
internal static class ContractTotals
{
    public static async Task RecalculateAsync(
        IModuleData data, Contract contract, ContractLine? pending, Guid? removedId, CancellationToken cancellationToken)
    {
        var lines = await data.Set<ContractLine>().Where(l => l.ContractId == contract.Id).ToListAsync(cancellationToken);
        if (removedId is { } removed)
        {
            lines.RemoveAll(l => l.Id == removed);
        }

        if (pending is not null && lines.All(l => l.Id != pending.Id))
        {
            lines.Add(pending);
        }

        lines = [.. lines.OrderBy(l => l.Position).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)];

        QuoteCalculation calc;
        try
        {
            calc = QuoteCalculator.Calculate(
                [.. lines.Where(l => l.Kind != Lifecycle.LineKinds.Text).Select(ToInput)], new QuotePolicy(contract.PriceIncludesTax));
        }
        catch (PricingException ex)
        {
            throw new RecordValidationException([new RecordFieldError("unitPrice", ex.Message)]);
        }

        var results = calc.Lines.ToDictionary(r => r.Id);
        foreach (ContractLine line in lines)
        {
            if (!results.TryGetValue(line.StableKey, out var r))
            {
                line.ListAmount = line.LineDiscount = line.Net = line.Tax = line.Gross = 0m;
                continue;
            }

            line.ListAmount = r.ListAmount;
            line.LineDiscount = r.LineDiscount;
            line.Net = r.Net;
            line.Tax = r.Tax;
            line.Gross = r.Gross;
        }

        contract.NetTotal = calc.OneTime.Net;
        contract.TaxTotal = calc.OneTime.Tax;
        contract.GrossTotal = calc.OneTime.Gross;
        contract.RecurringSummary = calc.Recurring.Count == 0
            ? null
            : JsonSerializer.Serialize(calc.Recurring.Select(p => new { period = p.Period, net = p.Net, tax = p.Tax, gross = p.Gross }));
        contract.CalculatedAt = DateTimeOffset.UtcNow;
    }

    private static QuoteLineInput ToInput(ContractLine l) => new(
        l.StableKey, l.Quantity, l.UnitPrice, l.TaxRatePercent, QuoteLineRole.Standard,
        l.DiscountPercent > 0 ? QuoteDiscountKind.Percent : QuoteDiscountKind.None, l.DiscountPercent, IsSelected: true, null, l.BillingPeriod);
}
