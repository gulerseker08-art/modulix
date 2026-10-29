namespace Modulix.Core.Pricing;

/// <summary>Teklif satırının toplama katılma biçimi.</summary>
public enum QuoteLineRole
{
    /// <summary>Ana toplama girer.</summary>
    Standard,

    /// <summary>Müşterinin seçebileceği ek kalem; seçilmedikçe ana toplamdan ayrıdır.</summary>
    Optional,

    /// <summary>Başka bir kalemin alternatifi; seçilmedikçe ana toplamdan ayrıdır.</summary>
    Alternative,

    /// <summary>Paket bileşeni: yalnızca bilgi içindir, paketin bedeli ana kalemdedir (çift hesaplama yok).</summary>
    PackageChild,
}

public enum QuoteDiscountKind
{
    None,
    Percent,
    Amount,
}

/// <summary>Hesaplama politikası; teklifte sabitlenir, tenant ayarı sonradan değişse de tarihsel teklif aynı politikayla okunur.</summary>
/// <param name="PriceIncludesTax">Birim fiyatlar vergi dahilse true.</param>
/// <param name="PolicyVersion">Kuralın sürümü; tarihsel teklifin hangi kuralla hesaplandığını belirtir.</param>
public sealed record QuotePolicy(bool PriceIncludesTax, int PolicyVersion = 1);

/// <param name="Id">Kararlı satır kimliği (revizyonlar arası izleme).</param>
/// <param name="ParentLineId">Yalnızca <see cref="QuoteLineRole.PackageChild"/> için paket ana satırı.</param>
/// <param name="IsSelected">Opsiyonel/alternatif satırın seçili olup olmadığı; standart satırda yok sayılır.</param>
/// <param name="RecurringPeriod">Boşsa tek seferlik; doluysa dönem anahtarı (ör. "Monthly", "Yearly").</param>
public sealed record QuoteLineInput(
    Guid Id,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRatePercent,
    QuoteLineRole Role = QuoteLineRole.Standard,
    QuoteDiscountKind DiscountKind = QuoteDiscountKind.None,
    decimal DiscountValue = 0m,
    bool IsSelected = false,
    Guid? ParentLineId = null,
    string? RecurringPeriod = null);

public sealed record QuoteLineResult(
    Guid Id,
    bool Included,
    decimal ListAmount,
    decimal LineDiscount,
    decimal DocumentDiscountShare,
    decimal Net,
    decimal Tax,
    decimal Gross,
    string? RecurringPeriod);

public sealed record QuoteTaxGroup(decimal RatePercent, decimal Net, decimal Tax, decimal Gross);

/// <summary>Bir faturalama dönemi (ya da tek seferlik) için toplam.</summary>
public sealed record QuotePeriodTotal(string? Period, decimal Net, decimal Tax, decimal Gross);

/// <summary>
/// Hesap sonucu. <see cref="OneTime"/> ve <see cref="Recurring"/> ayrı tutulur; sözleşme süresi bilinmeden tekrarlayan tutarlardan
/// tek bir "genel toplam" üretilmez. Seçilmeyen opsiyonel/alternatif satırlar <see cref="OptionalNet"/> ile bilgi olarak raporlanır.
/// </summary>
public sealed record QuoteCalculation(
    IReadOnlyList<QuoteLineResult> Lines,
    IReadOnlyList<QuoteTaxGroup> TaxGroups,
    QuotePeriodTotal OneTime,
    IReadOnlyList<QuotePeriodTotal> Recurring,
    decimal ListTotal,
    decimal LineDiscountTotal,
    decimal DocumentDiscountTotal,
    decimal OptionalNet);

public static class QuoteErrors
{
    public const string InvalidQuantity = "invalid-quantity";
    public const string NegativePrice = "negative-price";
    public const string InvalidDiscount = "invalid-discount";
    public const string DiscountExceedsAmount = "discount-exceeds-amount";
    public const string InvalidParent = "invalid-parent";
    public const string DuplicateLine = "duplicate-line";
}

/// <summary>
/// Teklif hesabının tek kaynağı (istemci toplamları doğru kabul edilmez). Sıra: satır tutarı (miktar × birim fiyat, yuvarlanır) →
/// satır indirimi → teklif geneli indirimi (satırlara indirim sonrası tutarla orantılı dağıtılır; yuvarlama farkı en büyük satıra,
/// eşitte ilkine eklenir) → vergi (satır başına, oran gruplarına göre özetlenir). Vergi dahil modda tutar brüttür; net, brütten
/// çıkarılır. Dahil edilenler: standart satırlar ve seçili opsiyonel/alternatifler; paket bileşenleri hiçbir zaman toplanmaz.
/// Oranlar çağıran tarafından verilir; mevzuat sabiti yoktur.
/// </summary>
public static class QuoteCalculator
{
    public static QuoteCalculation Calculate(
        IReadOnlyList<QuoteLineInput> lines, QuotePolicy policy, QuoteDiscountKind documentDiscountKind = QuoteDiscountKind.None,
        decimal documentDiscountValue = 0m)
    {
        Validate(lines);

        var afterLine = new Dictionary<Guid, (decimal List, decimal Discount, decimal After)>();
        foreach (QuoteLineInput line in lines)
        {
            decimal list = MoneyRounding.Round(line.Quantity * line.UnitPrice);
            decimal discount = LineDiscountAmount(line, list);
            afterLine[line.Id] = (list, discount, list - discount);
        }

        // Ana toplama giren satırlar.
        var included = lines.Where(IsIncluded).ToList();
        decimal eligible = included.Sum(l => afterLine[l.Id].After);
        decimal docDiscount = DocumentDiscountAmount(documentDiscountKind, documentDiscountValue, eligible);
        var shares = Allocate(included, afterLine, eligible, docDiscount);

        var results = new List<QuoteLineResult>();
        foreach (QuoteLineInput line in lines)
        {
            var (list, discount, after) = afterLine[line.Id];
            if (!IsIncluded(line))
            {
                // Dahil edilmeyen satır (seçilmemiş opsiyonel/alternatif ya da paket bileşeni) bilgi amaçlı hesaplanır, toplanmaz.
                LineAmounts info = line.Role == QuoteLineRole.PackageChild
                    ? new LineAmounts(0m, 0m, 0m)
                    : LineTotals.Calculate(after, line.TaxRatePercent, policy.PriceIncludesTax);
                results.Add(new QuoteLineResult(line.Id, false, list, discount, 0m, info.Net, info.Tax, info.Gross, line.RecurringPeriod));
                continue;
            }

            decimal share = shares[line.Id];
            LineAmounts amounts = LineTotals.Calculate(after - share, line.TaxRatePercent, policy.PriceIncludesTax);
            results.Add(new QuoteLineResult(line.Id, true, list, discount, share, amounts.Net, amounts.Tax, amounts.Gross, line.RecurringPeriod));
        }

        var includedResults = results.Where(r => r.Included).ToList();
        var taxGroups = includedResults
            .Join(lines, r => r.Id, l => l.Id, (r, l) => (Rate: l.TaxRatePercent, r.Net, r.Tax, r.Gross))
            .GroupBy(x => x.Rate)
            .OrderBy(g => g.Key)
            .Select(g => new QuoteTaxGroup(g.Key, g.Sum(x => x.Net), g.Sum(x => x.Tax), g.Sum(x => x.Gross)))
            .ToList();

        QuotePeriodTotal Total(string? period) => new(period,
            includedResults.Where(r => r.RecurringPeriod == period).Sum(r => r.Net),
            includedResults.Where(r => r.RecurringPeriod == period).Sum(r => r.Tax),
            includedResults.Where(r => r.RecurringPeriod == period).Sum(r => r.Gross));

        var recurring = includedResults.Where(r => r.RecurringPeriod is not null).Select(r => r.RecurringPeriod).Distinct()
            .OrderBy(p => p, StringComparer.Ordinal).Select(Total).ToList();

        decimal optionalNet = results.Where(r => !r.Included).Join(lines, r => r.Id, l => l.Id, (r, l) => (r, l))
            .Where(x => x.l.Role is QuoteLineRole.Optional or QuoteLineRole.Alternative).Sum(x => x.r.Net);

        return new QuoteCalculation(
            results, taxGroups, Total(null), recurring,
            included.Sum(l => afterLine[l.Id].List), included.Sum(l => afterLine[l.Id].Discount), docDiscount, optionalNet);
    }

    private static bool IsIncluded(QuoteLineInput line) => line.Role switch
    {
        QuoteLineRole.Standard => true,
        QuoteLineRole.Optional or QuoteLineRole.Alternative => line.IsSelected,
        _ => false,
    };

    private static decimal LineDiscountAmount(QuoteLineInput line, decimal list)
    {
        switch (line.DiscountKind)
        {
            case QuoteDiscountKind.None:
                return 0m;
            case QuoteDiscountKind.Percent:
                if (line.DiscountValue is < 0 or > 100)
                {
                    throw new PricingException(QuoteErrors.InvalidDiscount, "İndirim yüzdesi 0-100 arasında olmalıdır.");
                }

                return MoneyRounding.Round(list * line.DiscountValue / 100m);
            default:
                if (line.DiscountValue < 0 || line.DiscountValue > list)
                {
                    throw new PricingException(QuoteErrors.DiscountExceedsAmount, "Satır indirimi satır tutarını aşamaz.");
                }

                return MoneyRounding.Round(line.DiscountValue);
        }
    }

    private static decimal DocumentDiscountAmount(QuoteDiscountKind kind, decimal value, decimal eligible)
    {
        switch (kind)
        {
            case QuoteDiscountKind.None:
                return 0m;
            case QuoteDiscountKind.Percent:
                if (value is < 0 or > 100)
                {
                    throw new PricingException(QuoteErrors.InvalidDiscount, "Genel indirim yüzdesi 0-100 arasında olmalıdır.");
                }

                return MoneyRounding.Round(eligible * value / 100m);
            default:
                if (value < 0 || value > eligible)
                {
                    throw new PricingException(QuoteErrors.DiscountExceedsAmount, "Genel indirim toplam tutarı aşamaz.");
                }

                return MoneyRounding.Round(value);
        }
    }

    /// <summary>Genel indirimi satırlara orantılı dağıtır; paylaşımların toplamı her zaman indirimdir.</summary>
    private static Dictionary<Guid, decimal> Allocate(
        List<QuoteLineInput> included, Dictionary<Guid, (decimal List, decimal Discount, decimal After)> amounts, decimal eligible, decimal total)
    {
        var shares = included.ToDictionary(l => l.Id, _ => 0m);
        if (total == 0m || eligible == 0m)
        {
            return shares;
        }

        foreach (QuoteLineInput line in included)
        {
            shares[line.Id] = MoneyRounding.Round(total * amounts[line.Id].After / eligible);
        }

        decimal diff = total - shares.Values.Sum();
        if (diff != 0m)
        {
            // En büyük (indirim sonrası) satır; eşitte listedeki ilk.
            QuoteLineInput target = included.Aggregate((best, next) => amounts[next.Id].After > amounts[best.Id].After ? next : best);
            shares[target.Id] += diff;
        }

        return shares;
    }

    private static void Validate(IReadOnlyList<QuoteLineInput> lines)
    {
        var ids = new HashSet<Guid>();
        foreach (QuoteLineInput line in lines)
        {
            if (!ids.Add(line.Id))
            {
                throw new PricingException(QuoteErrors.DuplicateLine, "Satır kimlikleri benzersiz olmalıdır.");
            }

            if (line.Quantity <= 0)
            {
                throw new PricingException(QuoteErrors.InvalidQuantity, "Miktar sıfırdan büyük olmalıdır.");
            }

            if (line.UnitPrice < 0)
            {
                throw new PricingException(QuoteErrors.NegativePrice, "Birim fiyat negatif olamaz.");
            }

            if (line.TaxRatePercent is < 0 or > 100)
            {
                throw new PricingException(PricingErrors.InvalidRate, "Vergi oranı 0-100 arasında olmalıdır.");
            }
        }

        foreach (QuoteLineInput child in lines.Where(l => l.Role == QuoteLineRole.PackageChild))
        {
            if (child.ParentLineId is not { } parent || parent == child.Id || !ids.Contains(parent))
            {
                throw new PricingException(QuoteErrors.InvalidParent, "Paket bileşeninin geçerli bir paket ana satırı olmalıdır.");
            }
        }
    }
}
