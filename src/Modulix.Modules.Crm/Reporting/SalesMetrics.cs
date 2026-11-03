using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Reporting;

/// <summary>Metrik hesabının tek fırsat girdisi: yalnızca hesap için gereken alanlar (ek kalem, aktivite, iş ortağı birleşimi yoktur; fırsat tek satırdır).</summary>
public sealed record OpportunityFact(
    Guid Id, string Status, decimal? Amount, string Currency, int? Probability,
    DateOnly? ExpectedCloseDate, DateOnly? ActualCloseDate, decimal? WonAmount, string? WonCurrency);

/// <summary>
/// Raporlama para biriminde toplam. <see cref="MissingAmountCount"/>: tutarı bilinmeyen (toplama girmez), <see cref="MissingRateCount"/>: kuru bulunamayan
/// (toplama girmez, sessizce 1 kabul edilmez). İkisi de sıfır değilse toplam <b>kısmidir</b>.
/// </summary>
public sealed record MoneyTotal(decimal Amount, int Count, int MissingAmountCount, int MissingRateCount)
{
    public static MoneyTotal Zero { get; } = new(0m, 0, 0, 0);

    public bool IsPartial => MissingAmountCount > 0 || MissingRateCount > 0;
}

/// <summary>Panel kartları. Her kartın kayıt seçimi (<see cref="SalesMetrics.Select"/>) ve tutarı (<see cref="SalesMetrics.Amount"/>) tek yerde tanımlıdır; kart, grafik ve detay listesi aynı kesiti kullanır.</summary>
public enum SalesCard
{
    /// <summary>Durumu Açık olan tüm fırsatlar (kapanış tarihinden bağımsız).</summary>
    OpenPipeline,

    /// <summary>Açık ve beklenen kapanışı dönemde olan fırsatlar.</summary>
    ExpectedOpen,

    /// <summary>Beklenen kapanışı dönemde olan, olasılığı bilinen açık fırsatlar (tutar × olasılık / 100).</summary>
    WeightedOpen,

    /// <summary>Gerçek kapanışı dönemde olan kazanılmış fırsatlar.</summary>
    Realized,

    /// <summary>Gerçek kapanışı dönemde olan kaybedilmiş fırsatlar (tutar toplamı raporlanmaz; yalnızca adet).</summary>
    Lost,
}

/// <summary>Bir fırsatın karttaki tutarı: <see cref="Value"/> raporlama para biriminde; null ise <see cref="Issue"/> nedenini söyler.</summary>
public sealed record ConvertedAmount(decimal? Value, string? Issue, RateLookup? Rate)
{
    public const string MissingAmount = "MissingAmount";
    public const string MissingRate = "MissingRate";
}

/// <summary>Bir dönemin satış metrikleri (ADR-0026 metrik sözlüğü). Tüm tutarlar raporlama para birimindedir, vergi hariç net tutardır.</summary>
public sealed record SalesPeriodMetrics(
    ReportPeriod Period,
    string Currency,
    MoneyTotal OpenPipeline,
    MoneyTotal ExpectedOpen,
    MoneyTotal WeightedOpen,
    int WeightedMissingProbabilityCount,
    MoneyTotal Realized,
    decimal Outlook,
    int WonCount,
    int LostCount,
    decimal? WinRatePercent,
    int OpenWithoutCloseDateCount,
    int OverdueOpenCount)
{
    /// <summary>Bu dönemin sayıları eksik tutar/kur yüzünden kısmi mi?</summary>
    public bool IsPartial => OpenPipeline.IsPartial || ExpectedOpen.IsPartial || WeightedOpen.IsPartial || Realized.IsPartial;
}

/// <summary>Hedefe göre ilerleme; hedef yok ya da sıfırsa oranlar hesaplanmaz (null).</summary>
public sealed record TargetProgress(
    decimal Target, decimal Realized, decimal Remaining, decimal? AttainmentPercent, decimal? Coverage, bool TargetMet);

/// <summary>Kilitli tahminin gerçekleşenle karşılaştırması: hata = tahmin − gerçekleşen; gerçekleşen sıfırsa yüzde hata yok.</summary>
public sealed record ForecastVariance(decimal Forecast, decimal Actual, decimal Error, decimal AbsoluteError, decimal? ErrorPercent);

/// <summary>
/// Satış metrik hesabı (saf işlevler; veritabanı ve izin gerektirmez). Panel, API, detay listesi ve dışa aktarma aynı hesabı kullanır.
/// Kur politikası: açık fırsat (pipeline, beklenen, ağırlıklı) hesap gününe göre; gerçekleşen satış kazanım (gerçek kapanış) gününe göre çevrilir.
/// </summary>
public static class SalesMetrics
{
    public static IReadOnlyList<OpportunityFact> Select(IEnumerable<OpportunityFact> facts, SalesCard card, ReportPeriod period) => card switch
    {
        SalesCard.OpenPipeline => [.. facts.Where(f => f.Status == OpportunityStatus.Open)],
        SalesCard.ExpectedOpen => [.. facts.Where(f => f.Status == OpportunityStatus.Open && f.ExpectedCloseDate is { } d && period.Contains(d))],
        SalesCard.WeightedOpen => [.. facts.Where(f => f.Status == OpportunityStatus.Open && f.ExpectedCloseDate is { } d && period.Contains(d) && f.Probability is not null)],
        SalesCard.Realized => [.. facts.Where(f => f.Status == OpportunityStatus.Won && f.ActualCloseDate is { } d && period.Contains(d))],
        _ => [.. facts.Where(f => f.Status == OpportunityStatus.Lost && f.ActualCloseDate is { } d && period.Contains(d))],
    };

    /// <summary>Fırsatın kartta sayılan tutarı (raporlama para biriminde). Tutar yoksa ya da kur bulunamazsa değer boştur ve neden bildirilir.</summary>
    public static ConvertedAmount Amount(OpportunityFact fact, SalesCard card, string currency, RateBook rates, DateOnly today)
    {
        var realized = card == SalesCard.Realized;
        var amount = realized ? fact.WonAmount ?? fact.Amount : fact.Amount;
        if (amount is not { } value)
        {
            return new ConvertedAmount(null, ConvertedAmount.MissingAmount, null);
        }

        var source = realized ? fact.WonCurrency ?? fact.Currency : fact.Currency;
        var day = realized ? fact.ActualCloseDate ?? today : today;
        var (converted, rate) = rates.Convert(value, source, currency, day);
        if (converted is not { } inTarget)
        {
            return new ConvertedAmount(null, ConvertedAmount.MissingRate, null);
        }

        return new ConvertedAmount(card == SalesCard.WeightedOpen ? inTarget * (fact.Probability ?? 0) / 100m : inTarget, null, rate);
    }

    public static MoneyTotal Total(IEnumerable<OpportunityFact> facts, SalesCard card, ReportPeriod period, string currency, RateBook rates, DateOnly today)
    {
        decimal total = 0;
        var count = 0;
        var missingAmount = 0;
        var missingRate = 0;
        foreach (var fact in Select(facts, card, period))
        {
            count++;
            var converted = Amount(fact, card, currency, rates, today);
            switch (converted.Issue)
            {
                case ConvertedAmount.MissingAmount:
                    missingAmount++;
                    break;
                case ConvertedAmount.MissingRate:
                    missingRate++;
                    break;
                default:
                    total += converted.Value!.Value;
                    break;
            }
        }

        return new MoneyTotal(total, count, missingAmount, missingRate);
    }

    public static SalesPeriodMetrics Compute(
        IReadOnlyList<OpportunityFact> facts, ReportPeriod period, string currency, RateBook rates, DateOnly today)
    {
        var weighted = Total(facts, SalesCard.WeightedOpen, period, currency, rates, today);
        var realized = Total(facts, SalesCard.Realized, period, currency, rates, today);
        var won = Select(facts, SalesCard.Realized, period).Count;
        var lost = Select(facts, SalesCard.Lost, period).Count;
        var decided = won + lost;
        var open = Select(facts, SalesCard.OpenPipeline, period);

        return new SalesPeriodMetrics(
            period, currency,
            Total(facts, SalesCard.OpenPipeline, period, currency, rates, today),
            Total(facts, SalesCard.ExpectedOpen, period, currency, rates, today),
            weighted,
            Select(facts, SalesCard.ExpectedOpen, period).Count(f => f.Probability is null),
            realized,
            realized.Amount + weighted.Amount,
            won, lost,
            decided == 0 ? null : Math.Round(100m * won / decided, 2),
            open.Count(f => f.ExpectedCloseDate is null),
            open.Count(f => f.ExpectedCloseDate is { } d && d < today));
    }

    /// <summary>
    /// Hedefe ilerleme. Kalan hedef = hedef − gerçekleşen. Kapsama = dönemde kapanması beklenen açık tutar / kalan hedef; kalan hedef ≤ 0 ise
    /// "hedef karşılandı" (oran yok, sonsuz gösterilmez). Hedef ≤ 0 ise hiçbir oran hesaplanmaz.
    /// </summary>
    public static TargetProgress? Progress(decimal? target, decimal realized, decimal expectedOpen)
    {
        if (target is not { } value || value <= 0)
        {
            return null;
        }

        var remaining = value - realized;
        var met = remaining <= 0;
        return new TargetProgress(
            value, realized, remaining,
            Math.Round(100m * realized / value, 2),
            met ? null : Math.Round(expectedOpen / remaining, 3),
            met);
    }

    public static ForecastVariance Variance(decimal forecast, decimal actual) =>
        new(forecast, actual, forecast - actual, Math.Abs(forecast - actual), actual == 0 ? null : Math.Round(100m * (forecast - actual) / actual, 2));
}
