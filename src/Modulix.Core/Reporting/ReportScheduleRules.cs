namespace Modulix.Core.Reporting;

public enum ScheduleFrequency
{
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
}

/// <summary>
/// Zamanlanmış rapor takvim kuralları (saf). Saat, firmanın saat diliminin yerel saatidir; yaz saati geçişinde olmayan saat (ileri alma) bir sonraki
/// geçerli ana kayar, tekrar eden saat (geri alma) ilk karşılaşmayla çalışır. Ay günü 1-28'dir: ayın 29-31'i her ayda olmadığı için seçilemez
/// ("ayın sonu" belirsizliği yoktur). Bir sonraki çalışma her zaman <b>verilen andan sonrası</b> için hesaplanır; uzun süre kapalı kalan zamanlama
/// geçmiş çalışmaları toplu yığmaz, bir sonraki gelecek ana atlar.
/// </summary>
public static class ReportScheduleRules
{
    /// <summary>Bir kullanıcının sahip olabileceği en fazla zamanlama.</summary>
    public const int MaxPerUser = 10;

    /// <summary>Bir zamanlamanın sahibi dışındaki en fazla alıcı sayısı (dağıtım listesi).</summary>
    public const int MaxRecipients = 20;

    public static string? Validate(ScheduleFrequency frequency, int hour, DayOfWeek? dayOfWeek, int? dayOfMonth)
    {
        if (!Enum.IsDefined(frequency))
        {
            return "Sıklık geçersiz.";
        }

        if (hour is < 0 or > 23)
        {
            return "Saat 0-23 arasında olmalı.";
        }

        if (frequency == ScheduleFrequency.Weekly && (dayOfWeek is null || !Enum.IsDefined(dayOfWeek.Value)))
        {
            return "Haftalık zamanlamada haftanın günü seçilmeli.";
        }

        if (frequency == ScheduleFrequency.Monthly && dayOfMonth is not (>= 1 and <= 28))
        {
            return "Aylık zamanlamada ayın günü 1-28 arasında olmalı.";
        }

        return null;
    }

    public static DateTimeOffset NextRun(
        ScheduleFrequency frequency, int hour, DayOfWeek? dayOfWeek, int? dayOfMonth, DateTimeOffset after, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var offset = 0; offset <= 62; offset++)
        {
            var day = today.AddDays(offset);
            var matches = frequency switch
            {
                ScheduleFrequency.Daily => true,
                ScheduleFrequency.Weekly => day.DayOfWeek == dayOfWeek,
                _ => day.Day == dayOfMonth,
            };
            if (!matches)
            {
                continue;
            }

            var at = At(day, hour, zone);
            if (at > after)
            {
                return at;
            }
        }

        throw new InvalidOperationException("Bir sonraki çalışma zamanı hesaplanamadı.");
    }

    /// <summary>Yerel gün ve saatin UTC karşılığı (olmayan saat ileri kayar, tekrar eden saatte ilki).</summary>
    public static DateTimeOffset At(DateOnly day, int hour, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
