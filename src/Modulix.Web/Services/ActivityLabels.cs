using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>Aktivite durumunun ailesine göre görünen adı ve ekran yardımcıları (ADR-0015). Durum anahtarları ailelerde ortaktır, etiketleri değişir.</summary>
public static class ActivityLabels
{
    public static string Family(CrmLookups lookups, string type) =>
        lookups.CategoryOf(CrmLists.ActivityType, type) is { } category ? category : ActivityFamily.Task;

    public static string Status(string family, string status) => (family, status) switch
    {
        (_, ActivityStatus.Cancelled) => "İptal edildi",
        (_, ActivityStatus.Blocked) => "Bloke",
        (_, ActivityStatus.InProgress) => "Devam ediyor",
        (ActivityFamily.Call, ActivityStatus.Open) => "Planlandı",
        (ActivityFamily.Call, ActivityStatus.Completed) => "Gerçekleştirildi",
        (ActivityFamily.Call, ActivityStatus.NoAnswer) => "Ulaşılamadı",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.Open) => "Planlandı",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.Completed) => "Gerçekleşti",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.NoShow) => "Katılım olmadı",
        (_, ActivityStatus.Draft) => "Taslak",
        (_, ActivityStatus.Queued) => "Kuyruğa alındı",
        (_, ActivityStatus.Sent) => "Gönderildi",
        (_, ActivityStatus.Failed) => "Başarısız",
        (_, ActivityStatus.Open) => "Açık",
        (_, ActivityStatus.Completed) => "Tamamlandı",
        _ => status,
    };

    public static string Priority(string priority) => priority switch
    {
        ActivityPriority.Low => "Düşük",
        ActivityPriority.High => "Yüksek",
        ActivityPriority.Urgent => "Acil",
        _ => "Normal",
    };

    public static bool IsOverdue(ActivityDto activity) => activity.IsOverdue(DateTimeOffset.UtcNow);

    /// <summary>Satır / kart sınıfı: kapalı kayıt soluk, gecikmiş kayıt vurgulu; kapalı kayıt hiçbir zaman gecikmiş görünmez.</summary>
    public static string? Css(ActivityDto activity) =>
        activity.Masked ? "busy" : !ActivityStatus.IsOpen(activity.Status) ? "done" : IsOverdue(activity) ? "overdue" : null;

    /// <summary>Aktivitenin takvimde yer kapladığı günler (firma saat dilimine göre). Tüm gün etkinliği saat dilimi dönüşümü görmez.</summary>
    public static IEnumerable<DateOnly> Days(ActivityDto a)
    {
        if (a.IsAllDay && a.StartDate is { } start)
        {
            var end = a.EndDate ?? start;
            for (var day = start; day <= end && day.DayNumber - start.DayNumber < 366; day = day.AddDays(1))
            {
                yield return day;
            }

            yield break;
        }

        if (a.StartAt is { } from)
        {
            var first = DateOnly.FromDateTime(Regional.ToTenant(from).DateTime);
            var last = a.EndAt is { } to && to > from ? DateOnly.FromDateTime(Regional.ToTenant(to.AddTicks(-1)).DateTime) : first;
            for (var day = first; day <= last && day.DayNumber - first.DayNumber < 15; day = day.AddDays(1))
            {
                yield return day;
            }

            yield break;
        }

        if (a.DueAt is { } due)
        {
            yield return DateOnly.FromDateTime(Regional.ToTenant(due).DateTime);
        }
    }

    public static string TimeText(ActivityDto a)
    {
        if (a.Masked && a.StartAt is null && a.StartDate is null)
        {
            return "Meşgul";
        }

        if (a.IsAllDay)
        {
            return "Tüm gün";
        }

        if (a.StartAt is { } start)
        {
            var from = Regional.ToTenant(start).ToString(Regional.TimePattern);
            return a.EndAt is { } end ? $"{from}–{Regional.ToTenant(end).ToString(Regional.TimePattern)}" : from;
        }

        return a.DueAt is { } due ? $"son {Regional.ToTenant(due).ToString(Regional.TimePattern)}" : string.Empty;
    }

    /// <summary>Sunucuya gönderilen takvim penceresi: firma saat dilimindeki gün sınırları ve tarihleri.</summary>
    public static string Range(DateOnly from, DateOnly toInclusive)
    {
        var start = Regional.FromTenant(from.ToDateTime(TimeOnly.MinValue));
        var end = Regional.FromTenant(toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return $"{start.ToUniversalTime():O}|{end.ToUniversalTime():O}|{from:yyyy-MM-dd}|{toInclusive:yyyy-MM-dd}";
    }
}
