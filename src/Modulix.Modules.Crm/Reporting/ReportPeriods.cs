using System.Globalization;

namespace Modulix.Modules.Crm.Reporting;

public enum PeriodKind
{
    Month,
    Quarter,
    Year,
}

/// <summary>
/// Raporlama dönemi: firmanın yerel takviminde [Start, EndExclusive) aralığı (başlangıç dahil, sonraki dönemin başlangıcı hariç).
/// Fırsat kapanış tarihleri gün olarak saklandığından dönem gün aralığıdır; zaman damgalı alanlar için <see cref="ReportPeriods.ToUtcRange"/> kullanılır.
/// </summary>
public sealed record ReportPeriod(PeriodKind Kind, DateOnly Start, DateOnly EndExclusive, string Label)
{
    public DateOnly LastDay => EndExclusive.AddDays(-1);

    public bool Contains(DateOnly day) => day >= Start && day < EndExclusive;

    /// <summary>Dönem tamamen geçmişte mi (bugün dönem bitişinden sonra)?</summary>
    public bool HasEnded(DateOnly today) => today >= EndExclusive;
}

/// <summary>
/// Dönem hesabı (saf işlevler). Ay takvim ayıdır; çeyrek ve yıl mali takvime göre belirlenir (mali yıl başlangıç ayı ayardan gelir, varsayılan Ocak).
/// Mali yıl etiketi başlangıç yılını taşır: Ocak başlangıçlı yıl "FY2026", Temmuz başlangıçlı yıl "FY2026/2027".
/// </summary>
public static class ReportPeriods
{
    public const int DefaultFiscalStartMonth = 1;

    public static ReportPeriod Containing(PeriodKind kind, DateOnly day, int fiscalStartMonth = DefaultFiscalStartMonth)
    {
        var start = NormalizeMonth(fiscalStartMonth);
        switch (kind)
        {
            case PeriodKind.Month:
            {
                var first = new DateOnly(day.Year, day.Month, 1);
                return new ReportPeriod(kind, first, first.AddMonths(1), first.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            }

            case PeriodKind.Quarter:
            {
                var fiscalStart = FiscalYearStart(day, start);
                var monthsIn = ((day.Year - fiscalStart.Year) * 12) + day.Month - fiscalStart.Month;
                var quarter = monthsIn / 3;
                var first = fiscalStart.AddMonths(quarter * 3);
                return new ReportPeriod(kind, first, first.AddMonths(3), FormattableString.Invariant($"{YearLabel(fiscalStart, start)}-Q{quarter + 1}"));
            }

            default:
            {
                var fiscalStart = FiscalYearStart(day, start);
                return new ReportPeriod(kind, fiscalStart, fiscalStart.AddYears(1), YearLabel(fiscalStart, start));
            }
        }
    }

    /// <summary>Dönemi <paramref name="steps"/> kadar ileri (pozitif) ya da geri (negatif) kaydırır.</summary>
    public static ReportPeriod Shift(ReportPeriod period, int steps, int fiscalStartMonth = DefaultFiscalStartMonth)
    {
        var months = period.Kind switch
        {
            PeriodKind.Month => steps,
            PeriodKind.Quarter => steps * 3,
            _ => steps * 12,
        };
        return Containing(period.Kind, period.Start.AddMonths(months), fiscalStartMonth);
    }

    /// <summary>Anın firmanın saat dilimindeki takvim günü.</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    /// <summary>
    /// Dönemin UTC aralığı [başlangıç, bitiş): yerel gece yarısı sınırları saat diliminin kaydırmasıyla çevrilir. Yaz saatine geçişte
    /// gece yarısı yoksa ilk geçerli ana kayar. Zaman damgalı alanlara (oluşturulma, sunum) bu aralıkla süzgeç uygulanır.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) ToUtcRange(ReportPeriod period, TimeZoneInfo zone) =>
        (StartOfDayUtc(period.Start, zone), StartOfDayUtc(period.EndExclusive, zone));

    public static DateTimeOffset StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static int NormalizeMonth(int month) => month is >= 1 and <= 12 ? month : DefaultFiscalStartMonth;

    private static DateOnly FiscalYearStart(DateOnly day, int startMonth) =>
        new(day.Month >= startMonth ? day.Year : day.Year - 1, startMonth, 1);

    private static string YearLabel(DateOnly fiscalStart, int startMonth) =>
        startMonth == 1
            ? FormattableString.Invariant($"FY{fiscalStart.Year}")
            : FormattableString.Invariant($"FY{fiscalStart.Year}/{fiscalStart.Year + 1}");
}
