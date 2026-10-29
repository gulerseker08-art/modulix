namespace Modulix.Modules.Crm.Activities;

/// <summary>
/// Aktivite durumları. Tek bir ortak liste değil, her ailenin kendi alt kümesi vardır (<see cref="ActivityRules.StatusesOf"/>):
/// görev Açık/Devam/Bloke/Tamamlandı/İptal; arama Planlandı(Açık)/Gerçekleştirildi(Tamamlandı)/Ulaşılamadı/İptal;
/// toplantı ve ziyaret Planlandı(Açık)/Gerçekleşti(Tamamlandı)/Katılım olmadı/İptal; e-posta Taslak/Kuyrukta/Gönderildi/Başarısız/İptal.
/// Anahtarlar aileler arasında paylaşılır, etiketleri ekran aileye göre seçer.
/// </summary>
public static class ActivityStatus
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Blocked = "Blocked";
    public const string Completed = "Completed";
    public const string NoAnswer = "NoAnswer";
    public const string NoShow = "NoShow";
    public const string Cancelled = "Cancelled";
    public const string Draft = "Draft";
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";

    public static IReadOnlyList<string> All { get; } =
        [Open, InProgress, Blocked, Completed, NoAnswer, NoShow, Cancelled, Draft, Queued, Sent, Failed];

    /// <summary>Hâlâ yapılacak ya da sürmekte olan durumlar. Gecikme yalnızca bunlar için türetilir.</summary>
    public static IReadOnlyList<string> OpenStates { get; } = [Open, InProgress, Blocked, Draft, Queued, Failed];

    public static bool IsOpen(string status) => OpenStates.Contains(status);
}

/// <summary>Aktivite ailesi: tür seçim listesinin kategorisinden gelir (<c>crm.activity-type</c>); kategorisiz/özel tür görev gibi davranır.</summary>
public static class ActivityFamily
{
    public const string Task = "task";
    public const string Call = "call";
    public const string Meeting = "meeting";
    public const string Visit = "visit";
    public const string Email = "email";

    public static IReadOnlyList<string> All { get; } = [Task, Call, Meeting, Visit, Email];

    public static string Of(string? category) => category is not null && All.Contains(category) ? category : Task;
}

/// <summary>Öncelik anahtarları.</summary>
public static class ActivityPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static IReadOnlyList<string> All { get; } = [Low, Normal, High, Urgent];
}

/// <summary>Aktivitenin kaynağı: kullanıcı elle mi açtı, otomasyon/entegrasyon/içe aktarma mı üretti.</summary>
public static class ActivitySource
{
    public const string User = "User";
    public const string Automation = "Automation";
    public const string Integration = "Integration";
    public const string Import = "Import";

    public static IReadOnlyList<string> All { get; } = [User, Automation, Integration, Import];
}

/// <summary>Aktivite katılımcısı: dahili kullanıcı, harici kişi (CRM kişisi ya da serbest e-posta).</summary>
public static class ParticipantRole
{
    public const string Required = "Required";
    public const string Optional = "Optional";
    public const string Watcher = "Watcher";

    public static IReadOnlyList<string> All { get; } = [Required, Optional, Watcher];
}

/// <summary>Olay nedenleri (<c>RecordCommand.Reason</c>): genel kayıt olaylarının yanında iş anlamını taşır.</summary>
public static class ActivityEventReasons
{
    public const string Started = "activity.started";
    public const string Blocked = "activity.blocked";
    public const string Unblocked = "activity.unblocked";
    public const string Completed = "activity.completed";
    public const string Cancelled = "activity.cancelled";
    public const string Reopened = "activity.reopened";
    public const string Rescheduled = "activity.rescheduled";
    public const string Reassigned = "activity.reassigned";
}

/// <summary>
/// Aktivite durum makinesi ve zaman kuralları (saf mantık; veritabanı gerektirmez, birim testlidir).
/// <code>
/// Görev   : Open ⇄ InProgress ⇄ Blocked ; hepsi → Completed | Cancelled ; Completed/Cancelled → Open (yeniden aç)
/// Arama   : Open → Completed (ulaşıldı) | NoAnswer (ulaşılamadı) | Cancelled ; kapalılar → Open (yeniden aç) ; NoAnswer → Completed (düzeltme)
/// Toplantı/Ziyaret : Open → Completed | NoShow (katılım olmadı) | Cancelled ; kapalılar → Open ; NoShow → Completed (düzeltme)
/// E-posta : Draft → Queued | Sent (elle kayıt) | Cancelled ; Queued → Sent | Failed ; Failed → Queued | Draft | Cancelled ; Sent son durum
/// </code>
/// "Gecikmiş" saklanmaz: açık bir aktivite, son tarihi geçmişse gecikmiştir (<see cref="IsOverdue"/>).
/// </summary>
public static class ActivityRules
{
    private static readonly string[] Everything = ActivityStatus.All.ToArray();

    // (aile grubu, mevcut durum) → gidilebilecek durumlar. Toplantı ve ziyaret aynı makineyi paylaşır.
    private static readonly Dictionary<(string Machine, string From), string[]> Transitions = new()
    {
        [("task", ActivityStatus.Open)] = [ActivityStatus.InProgress, ActivityStatus.Blocked, ActivityStatus.Completed, ActivityStatus.Cancelled],
        [("task", ActivityStatus.InProgress)] = [ActivityStatus.Open, ActivityStatus.Blocked, ActivityStatus.Completed, ActivityStatus.Cancelled],
        [("task", ActivityStatus.Blocked)] = [ActivityStatus.Open, ActivityStatus.InProgress, ActivityStatus.Cancelled],
        [("task", ActivityStatus.Completed)] = [ActivityStatus.Open],
        [("task", ActivityStatus.Cancelled)] = [ActivityStatus.Open],

        [("call", ActivityStatus.Open)] = [ActivityStatus.Completed, ActivityStatus.NoAnswer, ActivityStatus.Cancelled],
        [("call", ActivityStatus.NoAnswer)] = [ActivityStatus.Open, ActivityStatus.Completed],
        [("call", ActivityStatus.Completed)] = [ActivityStatus.Open],
        [("call", ActivityStatus.Cancelled)] = [ActivityStatus.Open],

        [("meeting", ActivityStatus.Open)] = [ActivityStatus.Completed, ActivityStatus.NoShow, ActivityStatus.Cancelled],
        [("meeting", ActivityStatus.NoShow)] = [ActivityStatus.Open, ActivityStatus.Completed],
        [("meeting", ActivityStatus.Completed)] = [ActivityStatus.Open],
        [("meeting", ActivityStatus.Cancelled)] = [ActivityStatus.Open],

        [("email", ActivityStatus.Draft)] = [ActivityStatus.Queued, ActivityStatus.Sent, ActivityStatus.Cancelled],
        [("email", ActivityStatus.Queued)] = [ActivityStatus.Sent, ActivityStatus.Failed],
        [("email", ActivityStatus.Failed)] = [ActivityStatus.Queued, ActivityStatus.Draft, ActivityStatus.Cancelled],
        [("email", ActivityStatus.Cancelled)] = [ActivityStatus.Draft],
    };

    private static string Machine(string family) => family == ActivityFamily.Visit ? ActivityFamily.Meeting : family;

    /// <summary>Ailenin kullanabildiği durumlar.</summary>
    public static IReadOnlyList<string> StatusesOf(string family) => Machine(family) switch
    {
        ActivityFamily.Task => [ActivityStatus.Open, ActivityStatus.InProgress, ActivityStatus.Blocked, ActivityStatus.Completed, ActivityStatus.Cancelled],
        ActivityFamily.Call => [ActivityStatus.Open, ActivityStatus.Completed, ActivityStatus.NoAnswer, ActivityStatus.Cancelled],
        ActivityFamily.Meeting => [ActivityStatus.Open, ActivityStatus.Completed, ActivityStatus.NoShow, ActivityStatus.Cancelled],
        _ => [ActivityStatus.Draft, ActivityStatus.Queued, ActivityStatus.Sent, ActivityStatus.Failed, ActivityStatus.Cancelled],
    };

    /// <summary>Yeni (ve yeniden açılan) aktivitenin durumu.</summary>
    public static string InitialStatus(string family) => family == ActivityFamily.Email ? ActivityStatus.Draft : ActivityStatus.Open;

    /// <summary>Saklı durum ailenin durumu değilse (eski kayıt, tür değişimi) ailenin başlangıç durumu sayılır.</summary>
    public static string Normalize(string family, string? status) =>
        status is not null && StatusesOf(family).Contains(status) ? status : InitialStatus(family);

    /// <summary>Geçiş geçerliyse null, değilse kullanıcıya gösterilecek neden.</summary>
    public static string? CanMove(string family, string from, string to)
    {
        from = Normalize(family, from);
        if (from == to)
        {
            return "Aktivite zaten bu durumda.";
        }

        if (!StatusesOf(family).Contains(to))
        {
            return "Bu tür aktivite bu duruma geçemez.";
        }

        return Transitions.TryGetValue((Machine(family), from), out var allowed) && allowed.Contains(to)
            ? null
            : from == ActivityStatus.Sent ? "Gönderilmiş e-posta değiştirilemez." : "Bu durumdan istenen duruma geçilemez.";
    }

    /// <summary>
    /// Tamamlama eyleminin varacağı durum: görev/arama/toplantı için Completed; sonuç <c>NoAnswer</c> ise arama NoAnswer (ulaşılamadı),
    /// toplantı/ziyaret NoShow (katılım olmadı); e-posta için Sent (elle kayıt: teslim edildi/okundu anlamına GELMEZ).
    /// </summary>
    public static string CompletionTarget(string family, string? outcome) => family switch
    {
        ActivityFamily.Email => ActivityStatus.Sent,
        ActivityFamily.Call when outcome == CrmModule.ActivityOutcomes.NoAnswer => ActivityStatus.NoAnswer,
        ActivityFamily.Meeting or ActivityFamily.Visit when outcome == CrmModule.ActivityOutcomes.NoAnswer => ActivityStatus.NoShow,
        _ => ActivityStatus.Completed,
    };

    /// <summary>Tamamlanma/kapanış anı kaydedilen durumlar (gerçekleşti, ulaşılamadı, katılım olmadı, gönderildi). İptal gerçekleşme değildir.</summary>
    public static bool RecordsCompletion(string status) =>
        status is ActivityStatus.Completed or ActivityStatus.NoAnswer or ActivityStatus.NoShow or ActivityStatus.Sent;

    /// <summary>Gecikme türetilir: açık durumda ve son tarihi geçmiş. Tamamlanmış/iptal kayıt hiçbir zaman gecikmiş görünmez.</summary>
    public static bool IsOverdue(string status, DateTimeOffset? dueAt, DateTimeOffset now) =>
        ActivityStatus.IsOpen(status) && dueAt is { } due && due < now;

    /// <summary>Başlangıç/bitiş ve tüm gün alanlarının tutarlılığı; geçerliyse null.</summary>
    public static string? ValidateSchedule(
        bool isAllDay, DateTimeOffset? startAt, DateTimeOffset? endAt, DateOnly? startDate, DateOnly? endDate)
    {
        if (isAllDay)
        {
            if (startDate is null)
            {
                return "Tüm gün etkinliğinde başlangıç tarihi zorunludur.";
            }

            if (startAt is not null || endAt is not null)
            {
                return "Tüm gün etkinliği saat içermez.";
            }

            return endDate is { } end && end < startDate ? "Bitiş tarihi başlangıçtan önce olamaz." : null;
        }

        if (startDate is not null || endDate is not null)
        {
            return "Tarih aralığı yalnızca tüm gün etkinliğinde kullanılır.";
        }

        if (endAt is not null && startAt is null)
        {
            return "Bitiş verilmişse başlangıç da verilmelidir.";
        }

        return startAt is { } start && endAt is { } end2 && end2 <= start ? "Bitiş, başlangıçtan sonra olmalıdır." : null;
    }

    /// <summary>İki zaman bloğu çakışıyor mu? Yarı açık aralıklar: bitişi başka bloğun başlangıcına eşit olan çakışmaz.</summary>
    public static bool Overlaps(DateTimeOffset startA, DateTimeOffset endA, DateTimeOffset startB, DateTimeOffset endB) =>
        startA < endB && startB < endA;

    /// <summary>Tüm gün etkinliğinin kapsadığı gün sayısı (bitiş dahil).</summary>
    public static int DaySpan(DateOnly start, DateOnly? end) => (end ?? start).DayNumber - start.DayNumber + 1;
}
