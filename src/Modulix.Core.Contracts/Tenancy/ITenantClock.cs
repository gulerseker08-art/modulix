namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Aktif tenant'ın saat diliminde "bugün" ve gün sınırları. Zaman UTC saklanır; gün kavramı (ör. "son 30 gün") firmanın ayarındaki
/// saat dilimine göre hesaplanır (yaz saatine duyarlı). Tenant bağlamı olmadan çalışmaz.
/// </summary>
public interface ITenantClock
{
    Task<TenantDay> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Bir anın tenant saat dilimindeki günü ve gün başlangıçları.</summary>
public sealed class TenantDay(DateTimeOffset utcNow, TimeZoneInfo zone)
{
    public DateTimeOffset UtcNow { get; } = utcNow;

    public TimeZoneInfo Zone { get; } = zone;

    public DateOnly Today { get; } = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

    /// <summary>Verilen günün tenant saat dilimindeki başlangıcı (UTC ofsetiyle). Gece yarısı olmayan geçiş gününde ilk geçerli ana kayar.</summary>
    public DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }
}
