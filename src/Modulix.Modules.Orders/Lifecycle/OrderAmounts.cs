namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Kalem durumu (iptal ve değişiklik hesapları için saf giriş).</summary>
public sealed record LineAmounts(Guid LineId, string Name, decimal Quantity, decimal Delivered, decimal Cancelled, decimal Net, decimal Tax, bool IsService)
{
    public decimal Effective => Quantity - Cancelled;

    public decimal EffectiveNet => OrderAmounts.Effective(Net, Quantity, Cancelled);

    public decimal EffectiveTax => OrderAmounts.Effective(Tax, Quantity, Cancelled);
}

/// <summary>Kalemin yeni iptal edilmiş miktarı ve etkin tutardaki azalma (tüketim defterinden serbest bırakılacak tutar).</summary>
public sealed record LineReduction(Guid LineId, decimal NewCancelled, decimal ReleasedQuantity, decimal ReleasedNet, decimal ReleasedTax)
{
    public bool IsFullyCancelled(decimal quantity) => NewCancelled >= quantity;
}

/// <summary>
/// Kalem tutarlarının etkin miktara göre hesabı. Kalemin kopyalanmış tutarları (snapshot) hiç değişmez; iptal ya da değişiklik talebiyle
/// azalan miktar <c>CancelledQuantity</c> olarak ayrı tutulur ve etkin tutar orantılı hesaplanır. Teslim edilen miktar azaltılamaz.
/// </summary>
public static class OrderAmounts
{
    public static decimal Effective(decimal amount, decimal quantity, decimal cancelled)
    {
        if (cancelled <= 0m)
        {
            return amount;
        }

        return cancelled >= quantity ? 0m : OrderConversionPlanner.Round(amount * (quantity - cancelled) / quantity);
    }

    /// <summary>
    /// Siparişin iptali: her kalemin teslim edilmemiş kalan miktarı iptal edilir; teslim edilen miktar ve kayıtları korunur. Dönen azalmalar
    /// kalem iptal miktarını ve tüketim defterinden serbest bırakılacak miktar/tutarı verir.
    /// </summary>
    public static IReadOnlyList<LineReduction> PlanCancellation(IReadOnlyList<LineAmounts> lines) =>
        [.. lines.Select(l => Reduce(l, l.Delivered)).Where(r => r.ReleasedQuantity > 0m)];

    /// <summary>Kalemi etkin miktar <paramref name="newEffective"/> olacak şekilde azaltır (teslim edilenin altına inilemez, artırılamaz).</summary>
    public static LineReduction Reduce(LineAmounts line, decimal newEffective)
    {
        var newCancelled = line.Quantity - newEffective;
        var afterNet = Effective(line.Net, line.Quantity, newCancelled);
        var afterTax = Effective(line.Tax, line.Quantity, newCancelled);
        return new LineReduction(line.LineId, newCancelled, newCancelled - line.Cancelled, line.EffectiveNet - afterNet, line.EffectiveTax - afterTax);
    }

    public static (decimal Net, decimal Tax) Totals(IEnumerable<LineAmounts> lines)
    {
        var all = lines.ToList();
        return (all.Sum(l => l.EffectiveNet), all.Sum(l => l.EffectiveTax));
    }
}
