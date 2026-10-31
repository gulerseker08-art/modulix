namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>
/// Sözleşme süresi aritmetiği (ADR-0021, dilim 2). Tarihler tenant takviminde gündür (<see cref="DateOnly"/>); başlangıç ve bitiş günü
/// sözleşmeye dahildir. "On iki ay" sabit 365 gün değildir: dönem, başlangıçtan N ay sonraki günün bir öncesinde biter. Başlangıcın gün
/// numarası hedef ayda yoksa (31 → 30 günlük ay, 29 Şubat → artık olmayan yıl) hedef bir sonraki ayın ilk gününe kayar; bu sayede
/// 1 Mart'ta başlayan on iki aylık dönem artık yılda 29 Şubat'ta, 29 Şubat 2024'te başlayan dönem 28 Şubat 2025'te biter.
/// </summary>
public static class ContractPeriod
{
    /// <summary>Başlangıca N ay ekler; gün taşarsa bir sonraki ayın ilk günü (geriye kırpma yapılmaz).</summary>
    public static DateOnly AddMonthsRolling(DateOnly start, int months)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(months);
        var firstOfTarget = new DateOnly(start.Year, start.Month, 1).AddMonths(months);
        var daysInTarget = DateTime.DaysInMonth(firstOfTarget.Year, firstOfTarget.Month);
        return start.Day <= daysInTarget ? new DateOnly(firstOfTarget.Year, firstOfTarget.Month, start.Day) : firstOfTarget.AddMonths(1);
    }

    /// <summary>N aylık dönemin son günü (dahil): başlangıç + N ay − 1 gün.</summary>
    public static DateOnly TermEnd(DateOnly start, int months)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(months, 1);
        return AddMonthsRolling(start, months).AddDays(-1);
    }

    /// <summary>Bir sonraki dönemin başlangıcı: mevcut bitişin ertesi günü (yenileme zinciri boşluksuz, çakışmasız).</summary>
    public static DateOnly NextTermStart(DateOnly currentEnd) => currentEnd.AddDays(1);

    /// <summary>Tenant saat dilimine göre "bugün". Tarih kıyaslamaları UTC değil tenant günüyle yapılır.</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    /// <summary>Gün farkı (hedef − bugün); negatifse geçmiş.</summary>
    public static int DaysUntil(DateOnly today, DateOnly target) => target.DayNumber - today.DayNumber;
}

/// <summary>Tarihe bağlı yürürlük değerlendirmesi; zamanlanmış iş ve imza tamamlanması bunu kullanır (saf, test edilebilir).</summary>
public static class ContractDates
{
    /// <summary>
    /// İmzası tamamlanmış sözleşmenin yürürlük durumu: başlangıç gelmediyse <see cref="EffectiveStatus.AwaitingStart"/>, geldiyse
    /// <see cref="EffectiveStatus.Active"/>, bitişi (dahil) geçtiyse <see cref="EffectiveStatus.Expired"/>. Belirsiz süreli sözleşme
    /// süresi dolmaz. Başlangıç tarihi yoksa imza günü başlangıç sayılmaz; sözleşme başlangıcı bekler (tarih girilmelidir).
    /// </summary>
    public static string StatusOn(DateOnly today, DateOnly? start, DateOnly? end, bool isIndefinite)
    {
        if (start is null || today < start.Value)
        {
            return EffectiveStatus.AwaitingStart;
        }

        return !isIndefinite && end is { } e && today > e ? EffectiveStatus.Expired : EffectiveStatus.Active;
    }

    /// <summary>
    /// Mevcut yürürlük durumundan bugüne göre geçilecek durum; geçiş yoksa null. Askıdaki, feshedilmiş ve iptal edilmiş sözleşmeye
    /// dokunulmaz; süresi dolmuş sözleşme geri aktifleşmez (tarih uzatma yeni sürüm/ek protokol işidir).
    /// </summary>
    public static string? Evaluate(string effective, DateOnly today, DateOnly? start, DateOnly? end, bool isIndefinite)
    {
        if (effective is not (EffectiveStatus.AwaitingStart or EffectiveStatus.Active))
        {
            return null;
        }

        var target = StatusOn(today, start, end, isIndefinite);
        return target == effective ? null : effective == EffectiveStatus.Active && target == EffectiveStatus.AwaitingStart ? null : target;
    }
}
