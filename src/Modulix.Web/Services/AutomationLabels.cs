using Modulix.Shared.Automation;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 16b); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Tablolar özellik olarak tanımlıdır: metinler her okunuşta çözülür (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar
// eklenir (`AutomationApprovalLabelsTests` kapsamı denetler).

/// <summary>
/// Otomasyon kuralı ekranlarının (ADR-0027, Dilim 5) adları ve rozet tonları. Durum değerleri sunucu sözleşmesindeki
/// <c>AutomationRuleStatus</c> adlarıdır (Draft, Active, Inactive); hesap sunucudadır, burada yalnızca gösterim vardır.
/// </summary>
public static class AutomationLabels
{
    public const string StatusGroup = "admin.automation.status";
    public const string RunStatusGroup = "admin.automation.run-status";
    public const string StepStatusGroup = "admin.automation.step-status";
    public const string OriginGroup = "admin.automation.origin";
    public const string TriggerGroup = "admin.automation.trigger";
    public const string OperatorGroup = "admin.automation.operator";
    public const string LogicGroup = "admin.automation.logic";
    public const string BehaviourGroup = "admin.automation.behaviour";

    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static IReadOnlyList<string> Statuses { get; } = [Draft, Active, Inactive];

    internal static IReadOnlyList<(string Key, string Label)> StatusSource => [(Draft, "Taslak"), (Active, "Açık"), (Inactive, "Kapalı")];

    public static string Status(string status) => AdminLabels.Lookup(StatusGroup, StatusSource, status);

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

    internal static IReadOnlyList<(string Key, string Label)> RunStatusSource =>
    [
        (RunPending, "Bekliyor"),
        (RunRunning, "Çalışıyor"),
        (RunSucceeded, "Başarılı"),
        (RunFailed, "Başarısız"),
        (RunCancelled, "İptal edildi"),
        (RunNotMatched, "Koşul sağlanmadı"),
    ];

    public static string RunStatus(string status) => AdminLabels.Lookup(RunStatusGroup, RunStatusSource, status);

    /// <summary>Çalışma durumu rozet tonu: başarılı yeşil, başarısız kırmızı, süren bilgi, bekleyen ve koşulsuz nötr, iptal çizgili.</summary>
    public static string RunStatusBadge(string status) => status switch
    {
        RunSucceeded => "won",
        RunFailed => "lost",
        RunRunning => "proposal",
        RunCancelled => "cancelled",
        _ => "lead",
    };

    internal static IReadOnlyList<(string Key, string Label)> StepStatusSource =>
    [
        ("Pending", "Bekliyor"),
        ("Succeeded", "Tamamlandı"),
        ("Skipped", "Atlandı"),
        ("Failed", "Başarısız"),
        ("Cancelled", "İptal edildi"),
    ];

    public static string StepStatus(string status) => AdminLabels.Lookup(StepStatusGroup, StepStatusSource, status);

    public static string StepStatusBadge(string status) => status switch
    {
        "Succeeded" => "won",
        "Failed" => "lost",
        "Cancelled" => "cancelled",
        "Skipped" => "proposal",
        _ => "lead",
    };

    internal static IReadOnlyList<(string Key, string Label)> OriginSource => [("Event", "Olay"), ("Manual", "Elle"), ("Scan", "Günlük tarama")];

    public static string Origin(string origin) => AdminLabels.Lookup(OriginGroup, OriginSource, origin);

    // Kural düzenleyicisi (Dilim 5c): tetikleyici ve operatör adları.
    internal static IReadOnlyList<(string Key, string Label)> TriggerSource =>
    [
        (EditorTrigger.RecordCreated, "Kayıt oluşturulunca"),
        (EditorTrigger.FieldChanged, "Alan değişince"),
        (EditorTrigger.StatusTransition, "Durum değişince"),
        (EditorTrigger.Scheduled, "Her gün belirli saatte"),
        (EditorTrigger.DateRelative, "Tarihe göre (önce / sonra)"),
        (EditorTrigger.Inactivity, "Hareketsizlik"),
        (EditorTrigger.Manual, "Elle çalıştırılınca"),
        (EditorTrigger.DomainEvent, "İş olayı"),
    ];

    public static string TriggerType(string type) => AdminLabels.Lookup(TriggerGroup, TriggerSource, type);

    /// <summary>Operatör adları; anahtar JSON adıdır (<c>eq</c>, <c>withinNextDays</c>…).</summary>
    internal static IReadOnlyList<(string Key, string Label)> OperatorSource =>
    [
        ("eq", "eşittir"),
        ("ne", "eşit değildir"),
        ("gt", "büyüktür"),
        ("gte", "büyük ya da eşittir"),
        ("lt", "küçüktür"),
        ("lte", "küçük ya da eşittir"),
        ("between", "arasındadır"),
        ("contains", "içerir"),
        ("startsWith", "ile başlar"),
        ("in", "şunlardan biridir"),
        ("notIn", "şunlardan biri değildir"),
        ("isEmpty", "boştur"),
        ("isNotEmpty", "doludur"),
        ("before", "şu tarihten önce"),
        ("after", "şu tarihten sonra"),
        ("withinNextDays", "önümüzdeki … gün içindedir"),
        ("withinLastDays", "son … gün içindedir"),
        ("olderThanDays", "en az … gün önceye aittir"),
        ("changed", "değişti"),
        ("changedTo", "şuna değişti"),
        ("changedFrom", "şundan değişti"),
    ];

    /// <summary>Operatör adı (sunucu sözleşmesindeki ad: <c>Eq</c>, <c>WithinNextDays</c>…).</summary>
    public static string Operator(string serverName) =>
        AdminLabels.Lookup(OperatorGroup, OperatorSource, EditorLeaf.OperatorKey(serverName), serverName);

    internal static IReadOnlyList<(string Key, string Label)> LogicSource =>
        [(EditorCondition.And, "Hepsi sağlanırsa"), (EditorCondition.Or, "Herhangi biri sağlanırsa")];

    public static string Logic(string logic) =>
        AdminLabels.Lookup(LogicGroup, LogicSource, logic == EditorCondition.Or ? EditorCondition.Or : EditorCondition.And);

    /// <summary>Yeniden deneme yalnızca başarısız çalışmada anlamlıdır (sunucu da bunu uygular).</summary>
    public static bool CanRetry(string runStatus) => runStatus == RunFailed;

    /// <summary>İptal yalnızca bitmemiş çalışmada anlamlıdır (sunucu da bunu uygular).</summary>
    public static bool CanCancel(string runStatus) => runStatus is RunPending or RunRunning;

    /// <summary>Kural durumu görünüm seçicisinin "Tümü" anahtarı.</summary>
    public const string AllViews = "all";

    /// <summary>Kayıt türünün kullanıcıya görünen adı; katalogda yoksa (modül kapandı, izin yok) teknik anahtar.</summary>
    public static string EntityName(IReadOnlyList<AutomationEntityDto> entities, string entityKey) =>
        entities.FirstOrDefault(e => e.EntityKey == entityKey)?.DisplayName ?? entityKey;

    internal static IReadOnlyList<(string Key, string Label)> BehaviourSource =>
    [
        ("active-draft", "Çalışıyor; bekleyen taslak yayınlanana kadar eski sürüm geçerli."),
        (Active, "Çalışıyor."),
        (Inactive, "Kapalı; yayınlanmış sürüm çalışmaz."),
        (Draft, "Taslak; yayınlanana kadar hiçbir şey yapmaz."),
    ];

    // Tabloların çevrilmiş halleri (her okunuşta çözülür).
    public static IReadOnlyList<(string Key, string Label)> StatusLabels => AdminLabels.Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> RunStatusLabels => AdminLabels.Translate(RunStatusGroup, RunStatusSource);

    public static IReadOnlyList<(string Key, string Label)> StepStatusLabels => AdminLabels.Translate(StepStatusGroup, StepStatusSource);

    public static IReadOnlyList<(string Key, string Label)> OriginLabels => AdminLabels.Translate(OriginGroup, OriginSource);

    public static IReadOnlyList<(string Key, string Label)> TriggerLabels => AdminLabels.Translate(TriggerGroup, TriggerSource);

    public static IReadOnlyList<(string Key, string Label)> OperatorLabels => AdminLabels.Translate(OperatorGroup, OperatorSource);

    public static IReadOnlyList<(string Key, string Label)> LogicLabels => AdminLabels.Translate(LogicGroup, LogicSource);

    public static IReadOnlyList<(string Key, string Label)> BehaviourLabels => AdminLabels.Translate(BehaviourGroup, BehaviourSource);

    /// <summary>
    /// Kuralın şu an ne yaptığı: yayınlanmış sürüm açıksa çalışır, kapalıysa durur; yalnızca taslaksa hiç çalışmamıştır. Liste ve ayrıntıda
    /// durum rozetinin yanındaki açıklama bundan gelir.
    /// </summary>
    public static string Behaviour(AutomationRuleSummaryDto rule) => AdminLabels.Lookup(BehaviourGroup, BehaviourSource, rule.Status switch
    {
        Active when rule.HasDraft => "active-draft",
        Active => Active,
        Inactive => Inactive,
        _ => Draft,
    });
}
