using System.Globalization;

namespace Modulix.Modules.Support.Lifecycle;

// Rapor kuralları (ADR-0024 dilim 6): saf sınıflar; sabit girdiyle birim testle sınanır. Hesap backend'dedir, arayüz yalnızca gösterir.

/// <summary>Bir ölçümün özet istatistiği. <see cref="Count"/> örneklem büyüklüğüdür; örneklem boşsa diğer alanlar boştur.</summary>
public sealed record DurationStats(int Count, double? Mean, double? Median, double? P90, double? P95, double? Min, double? Max);

public static class ReportStats
{
    /// <summary>
    /// Ortalama, medyan ve yüzdelikler. Yüzdelik "en yakın sıra" (nearest-rank) yöntemiyle hesaplanır: değerler sıralanır, p yüzdeliği ⌈p/100·n⌉. sıradaki değerdir
    /// (veri uydurulmaz, doğrusal ara değer yoktur). Medyan çift sayıda değerde iki ortanın ortalamasıdır.
    /// </summary>
    public static DurationStats Of(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return new DurationStats(0, null, null, null, null, null, null);
        }

        var sorted = values.Order().ToArray();
        var n = sorted.Length;
        var median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
        return new DurationStats(n, Round(sorted.Average()), Round(median), Round(Percentile(sorted, 90)), Round(Percentile(sorted, 95)), Round(sorted[0]), Round(sorted[^1]));
    }

    public static double Percentile(double[] sorted, int percent)
    {
        var rank = (int)Math.Ceiling(percent / 100d * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>Oran (yüzde, bir ondalık); payda sıfırsa boş (0 ya da %100 uydurulmaz).</summary>
    public static double? Percent(int numerator, int denominator) => denominator <= 0 ? null : Math.Round(100d * numerator / denominator, 1, MidpointRounding.AwayFromZero);
}

/// <summary>Rapor dönemi ve gün sınırları. Raporlardaki "gün" Türkiye saatine göredir (Europe/Istanbul); tenant saat dilimi ayarı raporlara uygulanmaz.</summary>
public static class ReportPeriod
{
    public const int MaxDays = 366;
    public const int DefaultDays = 30;

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static DateOnly LocalDate(DateTimeOffset moment) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, Zone).DateTime);

    /// <summary>Yerel günün başlangıcı (UTC anı).</summary>
    public static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), Zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));

    /// <summary>Dönem sonu dahil (yerel gün); sonraki günün başlangıcı hariç sınırdır.</summary>
    public static DateTimeOffset EndExclusive(DateOnly lastDay) => StartOf(lastDay.AddDays(1));

    /// <summary>Dönemi doğrular: sıra, üst sınır ve gelecek günler. Boşsa son 30 gün (bugün dahil). Hata varsa mesajı döner.</summary>
    public static (DateOnly From, DateOnly To, string? Error) Resolve(DateOnly? from, DateOnly? to, DateTimeOffset now)
    {
        var today = LocalDate(now);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end)
        {
            return (start, end, "Başlangıç tarihi bitişten sonra olamaz.");
        }

        if (end > today)
        {
            return (start, end, "Bitiş tarihi bugünden sonra olamaz.");
        }

        return end.DayNumber - start.DayNumber + 1 > MaxDays ? (start, end, $"Dönem en çok {MaxDays} gün olabilir.") : (start, end, null);
    }

    /// <summary>Zaman serisi kovası: gün, hafta (pazartesi başlar) ya da ay başlangıcı.</summary>
    public static DateOnly Bucket(DateOnly day, string granularity) => granularity switch
    {
        "month" => new DateOnly(day.Year, day.Month, 1),
        "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        _ => day,
    };

    public static readonly IReadOnlyList<string> Granularities = ["day", "week", "month"];
}

/// <summary>Açık talep yaşı kovaları (takvim günü, talep açılışından şimdiye).</summary>
public static class BacklogAge
{
    public static readonly IReadOnlyList<(string Key, string Label, int MinDays, int? MaxDays)> Buckets =
    [
        ("lt1", "1 günden az", 0, 1), ("d1-3", "1-3 gün", 1, 3), ("d3-7", "3-7 gün", 3, 7), ("d7-14", "7-14 gün", 7, 14), ("d14-30", "14-30 gün", 14, 30), ("gt30", "30 günden fazla", 30, null),
    ];

    /// <summary>Yaşın (gün, kesirli) girdiği kovanın anahtarı; alt sınır dahil, üst sınır hariçtir.</summary>
    public static string KeyOf(double ageDays)
    {
        var age = Math.Max(0, ageDays); // saat sapması negatif yaş üretirse en yeni kovaya düşer
        return Buckets.First(b => age >= b.MinDays && (b.MaxDays is null || age < b.MaxDays)).Key;
    }
}

/// <summary>SLA örneklerinin uyum sayımı. Pay: hedefinde tamamlananlar; payda: sonuçlanmış örnekler (hedefinde tamamlanan + hedef aşılarak tamamlanan + hedefi aşılmış açık). Sürenler ve iptaller paydada yoktur.</summary>
public sealed record SlaComplianceCounts(int Met, int Missed, int Breached, int InProgress, int Cancelled)
{
    public int Decided => Met + Missed + Breached;

    public double? CompliancePercent => ReportStats.Percent(Met, Decided);
}

public static class SlaOutcome
{
    public const string Met = "Met";
    public const string Missed = "Missed";
    public const string Breached = "Breached";
    public const string InProgress = "InProgress";
    public const string Cancelled = "Cancelled";

    /// <summary>Bir SLA örneğinin sonucu (otorite örneğin durumu ve ihlal anıdır).</summary>
    public static string Of(string status, DateTimeOffset? breachedAt) => status switch
    {
        Sla.SlaStatus.Completed => breachedAt is null ? Met : Missed,
        Sla.SlaStatus.Cancelled => Cancelled,
        _ => breachedAt is null ? InProgress : Breached,
    };
}

/// <summary>Memnuniyet özeti kuralları.</summary>
public static class CsatRules
{
    /// <summary>Bu sayının altındaki yanıt sayısında ortalama güvenilir değildir; rapor "örneklem küçük" uyarısı taşır (veri gizlenmez).</summary>
    public const int MinReliableSample = 30;

    public static bool IsSmallSample(int answered) => answered < MinReliableSample;
}

/// <summary>Tekrarlayan neden gruplaması: serbest metin kök nedenler küçük/büyük harf, boşluk ve noktalama kenarı farkı gözetilmeden aynı sayılır (anlamsal eşleştirme yoktur).</summary>
public static class RootCauseGrouping
{
    public const int MaxKeyLength = 120;

    public static string Normalize(string? cause)
    {
        if (string.IsNullOrWhiteSpace(cause))
        {
            return string.Empty;
        }

        var lowered = cause.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var collapsed = string.Join(' ', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.', ',', ';', ':', '!', '-');
        return collapsed.Length > MaxKeyLength ? collapsed[..MaxKeyLength] : collapsed;
    }
}

/// <summary>Düzeltici aksiyon durumları ve gecikme kuralı.</summary>
public static class CorrectiveActionStatus
{
    public const string Open = "Open";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Open, Done, Cancelled];

    /// <summary>Açık ve son tarihi bugünden önce olan aksiyon gecikmiştir (son tarih günü gecikme sayılmaz).</summary>
    public static bool IsOverdue(string status, DateOnly dueDate, DateOnly today) => status == Open && dueDate < today;

    public static int DaysOverdue(DateOnly dueDate, DateOnly today) => Math.Max(0, today.DayNumber - dueDate.DayNumber);
}
