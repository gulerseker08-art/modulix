using Modulix.Core.Settings;

namespace Modulix.IntegrationTests;

/// <summary>
/// Testlerin "bugün"ü: sunucu tarih kurallarını firmanın saat diliminde (ayar yoksa İstanbul) işler, bu yüzden test
/// verisi de UTC gününe değil yerel güne göre kurulur. UTC kullanmak her gün 21:00–24:00 UTC arasında bir gün sapar.
/// </summary>
internal static class TenantToday
{
    public static DateOnly At(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, RegionalTime.Find(null)).DateTime);
}
