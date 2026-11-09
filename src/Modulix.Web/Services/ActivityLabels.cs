using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>Aktivite durumunun ailesine göre görünen adı ve ekran yardımcıları (ADR-0015). Durum anahtarları ailelerde ortaktır, etiketleri değişir.</summary>
public static class ActivityLabels
{
    public static string Family(CrmLookups lookups, string type) =>
        lookups.CategoryOf(CrmLists.ActivityType, type) is { } category ? category : ActivityFamily.Task;

    public static string Status(string family, string status) =>
        StatusKey(family, status) is { } key ? Texts.Get(key) : status;

    /// <summary>Durum metninin sözlük anahtarı (<c>crm.act.status.*</c>); tanımsız durum için <c>null</c> (durum anahtarı olduğu gibi gösterilir).</summary>
    public static string? StatusKey(string family, string status) => (family, status) switch
    {
        (_, ActivityStatus.Cancelled) => "crm.act.status.cancelled",
        (_, ActivityStatus.Blocked) => "crm.act.status.blocked",
        (_, ActivityStatus.InProgress) => "crm.act.status.inProgress",
        (ActivityFamily.Call, ActivityStatus.Open) => "crm.act.status.planned",
        (ActivityFamily.Call, ActivityStatus.Completed) => "crm.act.status.done",
        (ActivityFamily.Call, ActivityStatus.NoAnswer) => "crm.act.status.unreached",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.Open) => "crm.act.status.planned",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.Completed) => "crm.act.status.held",
        (ActivityFamily.Meeting or ActivityFamily.Visit, ActivityStatus.NoShow) => "crm.act.status.noShow",
        (_, ActivityStatus.Draft) => "crm.act.status.draft",
        (_, ActivityStatus.Queued) => "crm.act.status.queued",
        (_, ActivityStatus.Sent) => "crm.act.status.sent",
        (_, ActivityStatus.Failed) => "crm.act.status.failed",
        (_, ActivityStatus.Open) => "crm.act.status.open",
        (_, ActivityStatus.Completed) => "crm.act.status.completed",
        _ => null,
    };

    /// <summary>Gecikmiş açık kaydın durumunun yanına eklenen son ek (örn. " · gecikti").</summary>
    public static string OverdueSuffix => " · " + Texts.Get("crm.act.overdue");

    public static string Priority(string priority) => Texts.Get(PriorityKey(priority));

    /// <summary>Önceliğin sözlük anahtarı (<c>crm.act.priority.*</c>); bilinmeyen değer Normal sayılır.</summary>
    public static string PriorityKey(string priority) => priority switch
    {
        ActivityPriority.Low => "crm.act.priority.low",
        ActivityPriority.High => "crm.act.priority.high",
        ActivityPriority.Urgent => "crm.act.priority.urgent",
        _ => "crm.act.priority.normal",
    };

    public static bool IsOverdue(ActivityDto activity) => activity.IsOverdue(DateTimeOffset.UtcNow);

    /// <summary>Aktivite ailesinin ikonu (AppIcon adı); takvimde renk de aileden gelir (<c>fam-*</c> sınıfları).</summary>
    public static string Icon(string? family) => family switch
    {
        ActivityFamily.Call => "phone",
        ActivityFamily.Meeting => "users",
        ActivityFamily.Visit => "map-pin",
        ActivityFamily.Email => "mail",
        ActivityFamily.Task => "check-circle",
        _ => "clock",
    };

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
            return Texts.Get("crm.act.busy");
        }

        if (a.IsAllDay)
        {
            return Texts.Get("crm.act.allDay");
        }

        if (a.StartAt is { } start)
        {
            var from = Regional.ToTenant(start).ToString(Regional.TimePattern);
            return a.EndAt is { } end ? $"{from}–{Regional.ToTenant(end).ToString(Regional.TimePattern)}" : from;
        }

        return a.DueAt is { } due ? Texts.Get("crm.act.dueTime", Regional.ToTenant(due).ToString(Regional.TimePattern)) : string.Empty;
    }

    /// <summary>Sunucuya gönderilen takvim penceresi: firma saat dilimindeki gün sınırları ve tarihleri.</summary>
    public static string Range(DateOnly from, DateOnly toInclusive)
    {
        var start = Regional.FromTenant(from.ToDateTime(TimeOnly.MinValue));
        var end = Regional.FromTenant(toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return $"{start.ToUniversalTime():O}|{end.ToUniversalTime():O}|{from:yyyy-MM-dd}|{toInclusive:yyyy-MM-dd}";
    }
}
