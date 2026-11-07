using Modulix.Shared.Automation;

namespace Modulix.Web.Services;

/// <summary>
/// Otomasyon kuralı ekranlarının (ADR-0027, Dilim 5) Türkçe adları ve rozet tonları. Durum değerleri sunucu sözleşmesindeki
/// <c>AutomationRuleStatus</c> adlarıdır (Draft, Active, Inactive); hesap sunucudadır, burada yalnızca gösterim vardır.
/// </summary>
public static class AutomationLabels
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static IReadOnlyList<string> Statuses { get; } = [Draft, Active, Inactive];

    public static string Status(string status) => status switch
    {
        Draft => "Taslak",
        Active => "Açık",
        Inactive => "Kapalı",
        _ => status,
    };

    /// <summary>Rozet tonu (<c>badge stage-*</c> sınıfının eki): açık yeşil, kapalı nötr, taslak bilgi tonu.</summary>
    public static string StatusBadge(string status) => status switch
    {
        Active => "won",
        Inactive => "lead",
        _ => "proposal",
    };

    // Çalışma geçmişi (Dilim 5b): değerler sunucudaki AutomationRunStatus / AutomationStepStatus / AutomationRunOrigin adlarıdır.
    public const string RunPending = "Pending";
    public const string RunRunning = "Running";
    public const string RunSucceeded = "Succeeded";
    public const string RunFailed = "Failed";
    public const string RunCancelled = "Cancelled";
    public const string RunNotMatched = "NotMatched";

    public static IReadOnlyList<string> RunStatuses { get; } =
        [RunPending, RunRunning, RunSucceeded, RunFailed, RunCancelled, RunNotMatched];

    public static string RunStatus(string status) => status switch
    {
        RunPending => "Bekliyor",
        RunRunning => "Çalışıyor",
        RunSucceeded => "Başarılı",
        RunFailed => "Başarısız",
        RunCancelled => "İptal edildi",
        RunNotMatched => "Koşul sağlanmadı",
        _ => status,
    };

    /// <summary>Çalışma durumu rozet tonu: başarılı yeşil, başarısız kırmızı, süren bilgi, bekleyen ve koşulsuz nötr, iptal çizgili.</summary>
    public static string RunStatusBadge(string status) => status switch
    {
        RunSucceeded => "won",
        RunFailed => "lost",
        RunRunning => "proposal",
        RunCancelled => "cancelled",
        _ => "lead",
    };

    public static string StepStatus(string status) => status switch
    {
        "Pending" => "Bekliyor",
        "Succeeded" => "Tamamlandı",
        "Skipped" => "Atlandı",
        "Failed" => "Başarısız",
        "Cancelled" => "İptal edildi",
        _ => status,
    };

    public static string StepStatusBadge(string status) => status switch
    {
        "Succeeded" => "won",
        "Failed" => "lost",
        "Cancelled" => "cancelled",
        "Skipped" => "proposal",
        _ => "lead",
    };

    public static string Origin(string origin) => origin switch
    {
        "Event" => "Olay",
        "Manual" => "Elle",
        "Scan" => "Günlük tarama",
        _ => origin,
    };

    // Kural düzenleyicisi (Dilim 5c): tetikleyici ve operatör adları.
    public static string TriggerType(string type) => type switch
    {
        EditorTrigger.RecordCreated => "Kayıt oluşturulunca",
        EditorTrigger.FieldChanged => "Alan değişince",
        EditorTrigger.StatusTransition => "Durum değişince",
        EditorTrigger.Scheduled => "Her gün belirli saatte",
        EditorTrigger.DateRelative => "Tarihe göre (önce / sonra)",
        EditorTrigger.Inactivity => "Hareketsizlik",
        EditorTrigger.Manual => "Elle çalıştırılınca",
        EditorTrigger.DomainEvent => "İş olayı",
        _ => type,
    };

    /// <summary>Operatör adı (sunucu sözleşmesindeki ad: <c>Eq</c>, <c>WithinNextDays</c>…).</summary>
    public static string Operator(string serverName) => EditorLeaf.OperatorKey(serverName) switch
    {
        "eq" => "eşittir",
        "ne" => "eşit değildir",
        "gt" => "büyüktür",
        "gte" => "büyük ya da eşittir",
        "lt" => "küçüktür",
        "lte" => "küçük ya da eşittir",
        "between" => "arasındadır",
        "contains" => "içerir",
        "startsWith" => "ile başlar",
        "in" => "şunlardan biridir",
        "notIn" => "şunlardan biri değildir",
        "isEmpty" => "boştur",
        "isNotEmpty" => "doludur",
        "before" => "şu tarihten önce",
        "after" => "şu tarihten sonra",
        "withinNextDays" => "önümüzdeki … gün içindedir",
        "withinLastDays" => "son … gün içindedir",
        "olderThanDays" => "en az … gün önceye aittir",
        "changed" => "değişti",
        "changedTo" => "şuna değişti",
        "changedFrom" => "şundan değişti",
        _ => serverName,
    };

    public static string Logic(string logic) => logic == EditorCondition.Or ? "Herhangi biri sağlanırsa" : "Hepsi sağlanırsa";

    /// <summary>Yeniden deneme yalnızca başarısız çalışmada anlamlıdır (sunucu da bunu uygular).</summary>
    public static bool CanRetry(string runStatus) => runStatus == RunFailed;

    /// <summary>İptal yalnızca bitmemiş çalışmada anlamlıdır (sunucu da bunu uygular).</summary>
    public static bool CanCancel(string runStatus) => runStatus is RunPending or RunRunning;

    /// <summary>Kural durumu görünüm seçicisinin "Tümü" anahtarı.</summary>
    public const string AllViews = "all";

    /// <summary>Kayıt türünün kullanıcıya görünen adı; katalogda yoksa (modül kapandı, izin yok) teknik anahtar.</summary>
    public static string EntityName(IReadOnlyList<AutomationEntityDto> entities, string entityKey) =>
        entities.FirstOrDefault(e => e.EntityKey == entityKey)?.DisplayName ?? entityKey;

    /// <summary>
    /// Kuralın şu an ne yaptığı: yayınlanmış sürüm açıksa çalışır, kapalıysa durur; yalnızca taslaksa hiç çalışmamıştır. Liste ve ayrıntıda
    /// durum rozetinin yanındaki açıklama bundan gelir.
    /// </summary>
    public static string Behaviour(AutomationRuleSummaryDto rule) => rule.Status switch
    {
        Active when rule.HasDraft => "Çalışıyor; bekleyen taslak yayınlanana kadar eski sürüm geçerli.",
        Active => "Çalışıyor.",
        Inactive => "Kapalı; yayınlanmış sürüm çalışmaz.",
        _ => "Taslak; yayınlanana kadar hiçbir şey yapmaz.",
    };
}
