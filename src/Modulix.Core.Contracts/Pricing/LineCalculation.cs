namespace Modulix.Core.Contracts.Pricing;

/// <summary>
/// İndirim kuralı. Yüzde ya da tutar (ikisinden yalnızca biri). <see cref="Stackable"/> yanlışsa başka indirimle birlikte uygulanamaz
/// (açık hata). Kurallar <see cref="Priority"/> sırasıyla (küçük önce) ardışık uygulanır: %10 sonra %10 = %19 (toplanmaz).
/// </summary>
public sealed record DiscountRule(Guid Id, string Name, int Priority, decimal? Percent, decimal? Amount, bool Stackable = true);

public sealed record DiscountResult(decimal Original, decimal Final, decimal EffectivePercent, bool CapApplied, IReadOnlyList<string> Explanation);

public static class DiscountEngine
{
    /// <param name="amount">İndirim öncesi tutar (para birimi çağıranda; tutar indirimleri aynı birimde olmalıdır).</param>
    /// <param name="maxTotalPercent">Azami toplam indirim yüzdesi; aşılırsa indirim bu sınıra çekilir.</param>
    public static DiscountResult Apply(decimal amount, IEnumerable<DiscountRule> rules, decimal? maxTotalPercent = null)
    {
        if (amount < 0)
        {
            throw new PricingException(PricingErrors.NegativePrice, "Tutar negatif olamaz.");
        }

        var ordered = rules.OrderBy(r => r.Priority).ThenBy(r => r.Id).ToList();
        if (ordered.Count > 1 && ordered.FirstOrDefault(r => !r.Stackable) is { } exclusive)
        {
            throw new PricingException(PricingErrors.PriceConflict, $"'{exclusive.Name}' indirimi başka indirimle birlikte uygulanamaz.");
        }

        decimal current = amount;
        var log = new List<string>();
        foreach (DiscountRule rule in ordered)
        {
            if ((rule.Percent is null) == (rule.Amount is null))
            {
                throw new PricingException(PricingErrors.InvalidRate, $"'{rule.Name}' için yüzde ya da tutardan yalnızca biri verilmeli.");
            }

            if (rule.Percent is { } p)
            {
                if (p < 0 || p > 100)
                {
                    throw new PricingException(PricingErrors.InvalidRate, $"'{rule.Name}' yüzdesi 0-100 arasında olmalıdır.");
                }

                decimal next = current * (1 - p / 100m);
                log.Add($"{rule.Name}: %{p} → {current} × {1 - p / 100m} = {next}");
                current = next;
            }
            else
            {
                decimal a = rule.Amount!.Value;
                if (a < 0 || a > current)
                {
                    throw new PricingException(PricingErrors.DiscountExceedsPrice, $"'{rule.Name}' tutar indirimi ({a}) kalan fiyatı ({current}) aşıyor.");
                }

                log.Add($"{rule.Name}: -{a} → {current - a}");
                current -= a;
            }
        }

        bool capped = false;
        decimal effective = amount == 0 ? 0 : (1 - current / amount) * 100m;
        if (maxTotalPercent is { } cap && effective > cap)
        {
            current = amount * (1 - cap / 100m);
            effective = cap;
            capped = true;
            log.Add($"Azami toplam indirim %{cap} sınırı uygulandı → {current}");
        }

        current = MoneyRounding.Round(current);
        return new DiscountResult(amount, current, MoneyRounding.Round(effective), capped, log);
    }

    /// <summary>Kullanıcının/rolün manuel indirim sınırı aşılıyorsa onay gerekir.</summary>
    public static bool RequiresApproval(decimal effectivePercent, decimal userLimitPercent) => effectivePercent > userLimitPercent;
}

public sealed record LineAmounts(decimal Net, decimal Tax, decimal Gross);

/// <summary>
/// Vergi ve yuvarlama. Satır düzeyinde yuvarlanır (2 hane, sıfırdan uzağa); belge toplamı yuvarlanmış satırların toplamıdır, böylece
/// satırlar ile belge her zaman tutar. Vergi oranı çağıran tarafından vergi tanımından verilir (ülkeye özel sabit yoktur).
/// </summary>
public static class LineTotals
{
    /// <param name="amountAfterDiscount">İndirim sonrası tutar (vergi dahil fiyatsa vergi dahil).</param>
    /// <param name="taxRatePercent">Vergi oranı yüzde olarak (0 geçerli: vergisiz).</param>
    public static LineAmounts Calculate(decimal amountAfterDiscount, decimal taxRatePercent, bool priceIncludesTax)
    {
        if (taxRatePercent < 0)
        {
            throw new PricingException(PricingErrors.InvalidRate, "Vergi oranı negatif olamaz.");
        }

        if (priceIncludesTax)
        {
            decimal gross = MoneyRounding.Round(amountAfterDiscount);
            decimal net = MoneyRounding.Round(gross / (1 + taxRatePercent / 100m));
            return new LineAmounts(net, gross - net, gross);
        }

        decimal netAmount = MoneyRounding.Round(amountAfterDiscount);
        decimal tax = MoneyRounding.Round(netAmount * taxRatePercent / 100m);
        return new LineAmounts(netAmount, tax, netAmount + tax);
    }

    /// <summary>Belge toplamı: yuvarlanmış satırların toplamı. Para birimleri karışık olamaz (çağıran para birimi başına gruplar).</summary>
    public static LineAmounts Sum(IEnumerable<LineAmounts> lines) =>
        lines.Aggregate(new LineAmounts(0m, 0m, 0m), (a, l) => new LineAmounts(a.Net + l.Net, a.Tax + l.Tax, a.Gross + l.Gross));
}

public enum PackagePricing
{
    /// <summary>Paketin sabit bedeli vardır; bileşen fiyatları yalnızca dağıtım içindir, toplama eklenmez.</summary>
    Fixed,

    /// <summary>Paket bedeli seçili bileşenlerin bedelleri toplamıdır.</summary>
    Calculated,
}

public static class PackageCalculator
{
    /// <summary>
    /// Paket toplamı. Sabit fiyatta bileşenler toplama girmez (mükerrer ücret oluşmaz); hesaplanan fiyatta yalnızca bileşenler girer.
    /// </summary>
    public static decimal Total(PackagePricing mode, decimal? fixedPrice, IReadOnlyList<decimal> componentAmounts)
    {
        if (mode == PackagePricing.Fixed)
        {
            return fixedPrice is { } f && f >= 0
                ? MoneyRounding.Round(f)
                : throw new PricingException(PricingErrors.NegativePrice, "Sabit fiyatlı paket için geçerli bir paket bedeli gerekir.");
        }

        return MoneyRounding.Round(componentAmounts.Sum());
    }

    /// <summary>
    /// Sabit paket bedelini bileşenlere liste bedelleriyle orantılı dağıtır (her bileşen kendi vergi kategorisiyle vergilenebilsin
    /// diye). Yuvarlama farkı deterministiktir: en büyük bileşene (eşitlikte sıradaki ilkine) eklenir; dağıtımın toplamı her zaman
    /// paket bedelidir.
    /// </summary>
    public static IReadOnlyList<decimal> Allocate(decimal fixedPrice, IReadOnlyList<decimal> componentListAmounts)
    {
        if (componentListAmounts.Count == 0 || componentListAmounts.Any(a => a < 0))
        {
            throw new PricingException(PricingErrors.NegativePrice, "Dağıtım için geçerli bileşen bedelleri gerekir.");
        }

        decimal sum = componentListAmounts.Sum();
        decimal target = MoneyRounding.Round(fixedPrice);
        if (sum == 0)
        {
            throw new PricingException(PricingErrors.PriceNotFound, "Bileşen liste bedelleri sıfır; paket bedeli dağıtılamaz.");
        }

        var shares = componentListAmounts.Select(a => MoneyRounding.Round(target * a / sum)).ToArray();
        decimal diff = target - shares.Sum();
        if (diff != 0)
        {
            int largest = 0;
            for (int i = 1; i < componentListAmounts.Count; i++)
            {
                if (componentListAmounts[i] > componentListAmounts[largest])
                {
                    largest = i;
                }
            }

            shares[largest] += diff;
        }

        return shares;
    }

    /// <summary>Paket doğrudan ya da dolaylı kendini içeremez. <paramref name="components"/>: paket → bileşen paketleri/ürünleri.</summary>
    public static void EnsureNoCycle(Guid packageId, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> components)
    {
        var visiting = new HashSet<Guid>();
        var done = new HashSet<Guid>();

        void Visit(Guid id)
        {
            if (done.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new PricingException(PricingErrors.PackageCycle, "Paket doğrudan ya da dolaylı olarak kendisini içeremez.");
            }

            if (components.TryGetValue(id, out IReadOnlyList<Guid>? children))
            {
                foreach (Guid child in children)
                {
                    Visit(child);
                }
            }

            visiting.Remove(id);
            done.Add(id);
        }

        Visit(packageId);
    }
}
