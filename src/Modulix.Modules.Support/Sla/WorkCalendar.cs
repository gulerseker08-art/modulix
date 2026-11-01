using System.Globalization;
using System.Text.Json;

namespace Modulix.Modules.Support.Sla;

// SLA iş takvimi (ADR-0024 dilim 2). Saf hesap: veri erişimi yok, saat parametreyle verilir. Zaman UTC saklanır; süre, takvimin saat dilimindeki çalışma
// pencerelerine göre hesaplanır (hafta sonu, tatil, yarım gün, yaz/kış saati). Kodda önceden iş takvimi altyapısı yoktu; bu sınıf destek modülüne aittir.

/// <summary>Haftanın bir günü için çalışma penceresi (yerel saat). Bir günde birden çok pencere (öğle arası) olabilir; pencereler çakışamaz.</summary>
public sealed record DayHours(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>
/// Tatil günü. <see cref="Start"/> ve <see cref="End"/> boşsa gün tamamen kapalıdır; verilmişse yalnızca o pencere açıktır (yarım gün).
/// </summary>
public sealed record CalendarHoliday(DateOnly Date, string? Name, TimeOnly? Start, TimeOnly? End);

public sealed record CalendarSpec(string TimeZoneId, bool AlwaysOpen, IReadOnlyList<DayHours> Weekly, IReadOnlyList<CalendarHoliday> Holidays)
{
    public static CalendarSpec TwentyFourSeven { get; } = new("UTC", true, [], []);
}

/// <summary>Takvim tanımının metin biçimleri (veritabanında JSON) ve doğrulaması.</summary>
public static class CalendarSpecParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record DayDto(int Day, string Start, string End);

    private sealed record HolidayDto(string Date, string? Name, string? Start, string? End);

    public static (CalendarSpec? Spec, IReadOnlyList<(string Field, string Message)> Errors) Parse(
        string timeZoneId, bool alwaysOpen, string? weeklyJson, string? holidaysJson)
    {
        var errors = new List<(string, string)>();
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(("timeZoneId", "Geçerli bir saat dilimi kimliği girin (ör. Europe/Istanbul)."));
        }

        var weekly = new List<DayHours>();
        if (!string.IsNullOrWhiteSpace(weeklyJson))
        {
            try
            {
                foreach (var dto in JsonSerializer.Deserialize<List<DayDto>>(weeklyJson, Json) ?? [])
                {
                    if (dto.Day is < 0 or > 6 || !TryTime(dto.Start, out var start) || !TryTime(dto.End, out var end) || end <= start)
                    {
                        errors.Add(("weeklyHours", "Her çalışma penceresi için gün (0=Pazar … 6=Cumartesi), başlangıç ve daha geç bir bitiş (SS:dd) verin."));
                        break;
                    }

                    weekly.Add(new DayHours((DayOfWeek)dto.Day, start, end));
                }
            }
            catch (JsonException)
            {
                errors.Add(("weeklyHours", "Çalışma saatleri geçerli bir JSON listesi olmalıdır."));
            }
        }

        foreach (var group in weekly.GroupBy(w => w.Day))
        {
            var ordered = group.OrderBy(w => w.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End)
                {
                    errors.Add(("weeklyHours", "Aynı gündeki çalışma pencereleri çakışamaz."));
                }
            }
        }

        if (!alwaysOpen && weekly.Count == 0)
        {
            errors.Add(("weeklyHours", "7/24 değilse en az bir çalışma penceresi gerekir."));
        }

        var holidays = new List<CalendarHoliday>();
        if (!string.IsNullOrWhiteSpace(holidaysJson))
        {
            try
            {
                foreach (var dto in JsonSerializer.Deserialize<List<HolidayDto>>(holidaysJson, Json) ?? [])
                {
                    var hasWindow = dto.Start is not null || dto.End is not null;
                    var windowOk = TryTime(dto.Start, out var windowStart) & TryTime(dto.End, out var windowEnd) && windowEnd > windowStart;
                    if (!DateOnly.TryParseExact(dto.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || (hasWindow && !windowOk))
                    {
                        errors.Add(("holidays", "Her tatil için tarih (yyyy-MM-dd) verin; yarım gün için başlangıç ve daha geç bir bitiş (SS:dd) gerekir."));
                        break;
                    }

                    holidays.Add(new CalendarHoliday(
                        date, string.IsNullOrWhiteSpace(dto.Name) ? null : dto.Name.Trim(), hasWindow ? windowStart : null, hasWindow ? windowEnd : null));
                }
            }
            catch (JsonException)
            {
                errors.Add(("holidays", "Tatiller geçerli bir JSON listesi olmalıdır."));
            }

            if (holidays.GroupBy(h => h.Date).Any(g => g.Count() > 1))
            {
                errors.Add(("holidays", "Aynı tarih için tek tatil kaydı olabilir."));
            }
        }

        return errors.Count > 0 ? (null, errors) : (new CalendarSpec(timeZoneId, alwaysOpen, weekly, holidays), errors);
    }

    public static string WeeklyToJson(IEnumerable<DayHours> weekly) =>
        JsonSerializer.Serialize(weekly.Select(w => new DayDto((int)w.Day, w.Start.ToString("HH:mm", CultureInfo.InvariantCulture), w.End.ToString("HH:mm", CultureInfo.InvariantCulture))), Json);

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

/// <summary>
/// İş takvimi hesaplayıcısı. <see cref="AddWorkingTime"/>: başlangıçtan itibaren verilen iş süresi sonrası an; <see cref="WorkingTimeBetween"/>: iki an arasındaki iş süresi.
/// Çalışma penceresi sınırı dahildir: tam pencere sonunda biten süre o anda biter (ertesi güne taşmaz). Yaz/kış saati geçişinde yerel saat geçersizse (ileri alma
/// boşluğu) bir saat ileri, belirsizse (geri alma) ilk (yaz saati) karşılığı kullanılır; sonuç deterministiktir.
/// </summary>
public sealed class WorkCalendar
{
    private const int MaxDays = 1100;

    private readonly TimeZoneInfo zone;
    private readonly CalendarSpec spec;
    private readonly Dictionary<DateOnly, CalendarHoliday> holidays;

    public WorkCalendar(CalendarSpec spec)
    {
        this.spec = spec;
        zone = spec.AlwaysOpen ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(spec.TimeZoneId);
        holidays = spec.Holidays.ToDictionary(h => h.Date);
    }

    public static WorkCalendar AlwaysOpen { get; } = new(CalendarSpec.TwentyFourSeven);

    public bool IsAlwaysOpen => spec.AlwaysOpen;

    public DateTimeOffset AddWorkingTime(DateTimeOffset from, TimeSpan duration)
    {
        from = from.ToUniversalTime();
        if (duration <= TimeSpan.Zero)
        {
            return from;
        }

        if (spec.AlwaysOpen)
        {
            return from + duration;
        }

        var remaining = duration;
        var date = LocalDate(from);
        for (var day = 0; day < MaxDays; day++, date = date.AddDays(1))
        {
            foreach (var (start, end) in WindowsUtc(date))
            {
                var effective = start > from ? start : from;
                if (effective >= end)
                {
                    continue;
                }

                var available = end - effective;
                if (remaining <= available)
                {
                    return effective + remaining;
                }

                remaining -= available;
            }
        }

        throw new InvalidOperationException("İş takvimi çok uzun bir süre boyunca çalışma penceresi içermiyor.");
    }

    public TimeSpan WorkingTimeBetween(DateTimeOffset from, DateTimeOffset to)
    {
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        if (spec.AlwaysOpen)
        {
            return to - from;
        }

        var total = TimeSpan.Zero;
        var date = LocalDate(from);
        var last = LocalDate(to);
        for (var day = 0; date <= last && day < MaxDays * 2; day++, date = date.AddDays(1))
        {
            foreach (var (start, end) in WindowsUtc(date))
            {
                var s = start > from ? start : from;
                var e = end < to ? end : to;
                if (e > s)
                {
                    total += e - s;
                }
            }
        }

        return total;
    }

    private DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    /// <summary>Bir yerel günün çalışma pencereleri (UTC), sıralı. Tatil varsa haftalık saatlerin yerine geçer.</summary>
    private IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> WindowsUtc(DateOnly date)
    {
        IEnumerable<(TimeOnly, TimeOnly)> windows;
        if (holidays.TryGetValue(date, out var holiday))
        {
            windows = holiday.Start is { } hs && holiday.End is { } he ? [(hs, he)] : [];
        }
        else
        {
            windows = spec.Weekly.Where(w => w.Day == date.DayOfWeek).OrderBy(w => w.Start).Select(w => (w.Start, w.End));
        }

        foreach (var (start, end) in windows)
        {
            yield return (ToUtc(date, start), ToUtc(date, end));
        }
    }

    private DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>SLA sayacı hesapları: harcanan iş süresi, hedef anı ve eşik anları. Hedef ve eşikler gerçek takvim anlarıdır; işin çalıştığı ana bağlı değildir.</summary>
public static class SlaClock
{
    /// <summary>Çalışan sayaç için hedef anı: çalışmaya başlanan andan, hedefin kalan iş süresi kadar sonrası.</summary>
    public static DateTimeOffset DueAt(WorkCalendar calendar, DateTimeOffset runningSince, TimeSpan target, TimeSpan worked) =>
        calendar.AddWorkingTime(runningSince, Max(TimeSpan.Zero, target - worked));

    /// <summary>Hedefin yüzdesine ulaşılan an (yüzde 100 hedeftir; 100 üstü ihlal sonrasıdır).</summary>
    public static DateTimeOffset ThresholdAt(WorkCalendar calendar, DateTimeOffset runningSince, TimeSpan target, TimeSpan worked, int percent) =>
        calendar.AddWorkingTime(runningSince, Max(TimeSpan.Zero, TimeSpan.FromTicks(target.Ticks * percent / 100) - worked));

    /// <summary>Şu ana kadar harcanan iş süresi: duraklatma öncesi birikmiş süre + çalışan dilim.</summary>
    public static TimeSpan WorkedAt(WorkCalendar calendar, TimeSpan worked, DateTimeOffset? runningSince, DateTimeOffset now) =>
        worked + (runningSince is { } since ? calendar.WorkingTimeBetween(since, now) : TimeSpan.Zero);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
