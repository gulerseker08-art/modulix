namespace Modulix.Core.Settings;

/// <summary>Firmanın saat dilimiyle gün sınırları (Y6b): denetim kaydının tarih filtresi ve dışa aktarma bunu kullanır.</summary>
public static class RegionalTime
{
    /// <summary>Ayardaki saat dilimi; bulunamazsa (ör. sistemde tanımlı değil) İstanbul.</summary>
    public static TimeZoneInfo Find(string? timeZoneId) =>
        timeZoneId is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? zone
            : TimeZoneInfo.FindSystemTimeZoneById(RegionalOptions.DefaultTimeZone);

    /// <summary>Günün firmanın saat dilimindeki başlangıcı.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Yaz saatine geçişte gece yarısı yoksa ilk geçerli ana kayar.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTimeOffset ToLocal(DateTimeOffset time, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(time, zone);
}
