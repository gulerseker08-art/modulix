using Modulix.Modules.Crm.Contracts.Sales;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Kısmi dönüşümde bir teklif kaleminden siparişe alınacak miktar.</summary>
public sealed record ConversionLineRequest(Guid LineKey, decimal Quantity);

/// <summary>Bir teklif kaleminin tüketim defterindeki durumu (kabul edilen ve dönüştürülen miktar/tutar).</summary>
public sealed record ConsumptionState(
    decimal AcceptedQuantity, decimal AcceptedNet, decimal AcceptedTax, decimal ConvertedQuantity, decimal ConvertedNet, decimal ConvertedTax)
{
    public decimal RemainingQuantity => AcceptedQuantity - ConvertedQuantity;

    public static ConsumptionState For(AcceptedQuoteLine line) => new(line.Quantity, line.Net, line.Tax, 0m, 0m, 0m);
}

/// <summary>Siparişe alınacak kalem: kaynak kalem, miktar ve o miktara düşen tutarlar.</summary>
public sealed record PlannedOrderLine(
    AcceptedQuoteLine Source, decimal Quantity, decimal ListAmount, decimal LineDiscount, decimal Net, decimal Tax)
{
    public decimal Gross => Net + Tax;
}

public enum PlanErrorKind
{
    None,
    Invalid,
    Conflict,
}

public sealed record ConversionPlan(IReadOnlyList<PlannedOrderLine> Lines, PlanErrorKind Error, string? Field, string? Message)
{
    public bool IsSuccess => Error == PlanErrorKind.None;

    public static ConversionPlan Ok(IReadOnlyList<PlannedOrderLine> lines) => new(lines, PlanErrorKind.None, null, null);

    public static ConversionPlan Invalid(string field, string message) => new([], PlanErrorKind.Invalid, field, message);

    public static ConversionPlan Conflict(string field, string message) => new([], PlanErrorKind.Conflict, field, message);
}

/// <summary>
/// Kabul edilmiş tekliften sipariş kalemlerinin hesabı (saf kural; veritabanına dokunmaz). Varsayılan dönüşüm teklifin tamamıdır: her kalem
/// tüm miktarıyla, tek siparişte. Kısmi dönüşüm ayrı ve açık bir iş kuralıdır: kalem bazında miktar verilir, toplam dönüştürülen miktar kabul
/// edilen miktarı aşamaz. Kalemin son kalan miktarı alınırken tutarlar kuruş farkı kalmayacak şekilde kalan tutardır.
/// </summary>
public static class OrderConversionPlanner
{
    public static ConversionPlan Plan(
        IReadOnlyList<AcceptedQuoteLine> lines, string mode, IReadOnlyList<ConversionLineRequest>? requested, IReadOnlyDictionary<Guid, ConsumptionState> ledger)
    {
        if (lines.Count == 0)
        {
            return ConversionPlan.Invalid("lines", "Kabul edilmiş teklifte sipariş kalemi yok.");
        }

        ConsumptionState StateOf(AcceptedQuoteLine line) => ledger.TryGetValue(line.StableKey, out var state) ? state : ConsumptionState.For(line);

        if (mode == ConversionMode.Full)
        {
            if (requested is { Count: > 0 })
            {
                return ConversionPlan.Invalid("lines", "Tam dönüşümde kalem miktarı verilmez; kısmi dönüşüm için kipi değiştirin.");
            }

            if (lines.Any(l => StateOf(l).ConvertedQuantity > 0m))
            {
                return ConversionPlan.Conflict("lines", "Bu teklifin kalemlerinden bir kısmı ya da tamamı zaten siparişe alınmış.");
            }

            var all = new List<PlannedOrderLine>();
            foreach (var line in lines)
            {
                all.Add(Take(line, StateOf(line), StateOf(line).RemainingQuantity));
            }

            return ConversionPlan.Ok(all);
        }

        if (mode != ConversionMode.Partial)
        {
            return ConversionPlan.Invalid("mode", "Dönüşüm kipi Full ya da Partial olmalıdır.");
        }

        if (requested is not { Count: > 0 })
        {
            return ConversionPlan.Invalid("lines", "Kısmi dönüşümde en az bir kalem ve miktar verilmelidir.");
        }

        if (requested.Select(r => r.LineKey).Distinct().Count() != requested.Count)
        {
            return ConversionPlan.Invalid("lines", "Aynı kalem birden çok kez verilemez.");
        }

        var planned = new List<PlannedOrderLine>();
        foreach (var request in requested)
        {
            var line = lines.FirstOrDefault(l => l.StableKey == request.LineKey);
            if (line is null)
            {
                return ConversionPlan.Invalid("lines", "Seçilen kalemlerden biri bu teklifte yok.");
            }

            if (request.Quantity <= 0m)
            {
                return ConversionPlan.Invalid("lines", $"'{line.Name}' için miktar sıfırdan büyük olmalıdır.");
            }

            var state = StateOf(line);
            if (request.Quantity > state.RemainingQuantity)
            {
                return ConversionPlan.Conflict("lines",
                    $"'{line.Name}' için en fazla {state.RemainingQuantity:0.####} {line.Unit} kalan; {request.Quantity:0.####} istendi (kabul edilen {state.AcceptedQuantity:0.####}).");
            }

            planned.Add(Take(line, state, request.Quantity));
        }

        return ConversionPlan.Ok(planned);
    }

    private static PlannedOrderLine Take(AcceptedQuoteLine line, ConsumptionState state, decimal quantity)
    {
        var ratio = state.AcceptedQuantity <= 0m ? 1m : quantity / state.AcceptedQuantity;

        // Son kalan miktar: tutar, kabul edilen tutardan önceden dönüştürülenin farkıdır (kuruş farkı kalmaz).
        var closes = state.AcceptedQuantity <= 0m || quantity == state.RemainingQuantity;
        var net = closes ? state.AcceptedNet - state.ConvertedNet : Round(state.AcceptedNet * ratio);
        var tax = closes ? state.AcceptedTax - state.ConvertedTax : Round(state.AcceptedTax * ratio);
        return new PlannedOrderLine(line, quantity, Round(line.ListAmount * ratio), Round(line.LineDiscount * ratio), net, tax);
    }

    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
