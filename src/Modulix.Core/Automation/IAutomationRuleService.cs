namespace Modulix.Core.Automation;

public sealed record AutomationRuleQuery(
    string? ModuleKey = null, AutomationRuleStatus? Status = null, string? Search = null, int Page = 1, int PageSize = 50, string? Sort = null)
{
    public const int MaxPageSize = 200;

    public const string DefaultSort = "-updatedAt";

    /// <summary>Liste ekranının sıralama anahtarları (başına "-" azalan). Öncelik sürümde durduğu için sıralanamaz.</summary>
    public static IReadOnlyList<string> SortKeys { get; } = ["name", "status", "updatedAt"];

    public static bool IsValidSort(string? sort) =>
        string.IsNullOrWhiteSpace(sort) || SortKeys.Contains(sort.TrimStart('-'), StringComparer.Ordinal);
}

public sealed record AutomationRuleSummary(
    Guid Id,
    string Name,
    string ModuleKey,
    string EntityKey,
    AutomationRuleStatus Status,
    int? PublishedVersionNo,
    bool HasDraft,
    int Priority,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record AutomationRulePage(IReadOnlyList<AutomationRuleSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Bir sürüm ve içeriği. <see cref="Summary"/> okunabilir özettir (kullanıcı katalogu görebildiği ölçüde).</summary>
public sealed record AutomationVersionInfo(
    Guid Id,
    int VersionNo,
    AutomationVersionState State,
    AutomationVersionContent Content,
    string Hash,
    string? Summary,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy);

/// <summary>
/// Kural ayrıntısı. <see cref="DraftIssues"/> ve <see cref="PublishedIssues"/> sürümlerin <b>şu anki</b> katalogla ve okuyanın yetkisiyle
/// doğrulanma sonucudur: bir alan, lookup ya da modül kaldırılırsa/pasifleşirse bağımlı kural sessizce bozulmaz, sorun burada görünür.
/// </summary>
public sealed record AutomationRuleDetail(
    AutomationRuleSummary Rule,
    AutomationVersionInfo? Draft,
    AutomationVersionInfo? Published,
    IReadOnlyList<AutomationIssue> DraftIssues,
    IReadOnlyList<AutomationIssue> PublishedIssues);

/// <summary>Girdi geçersiz (ad, öncelik, tarih, tanım biçimi, bilinmeyen kayıt türü). İleti kullanıcıya gösterilir.</summary>
public sealed class AutomationRuleException(string message) : Exception(message);

/// <summary>Durum ya da sürüm çakışması (kural başkası tarafından değişti, yayınlanmamış kural açılamaz...). API 409.</summary>
public sealed class AutomationRuleConflictException(string message) : Exception(message);

/// <summary>Yayın engellendi: tanım doğrulanamadı. Sorunların hepsi <see cref="Issues"/>'ta.</summary>
public sealed class AutomationPublishException(IReadOnlyList<AutomationIssue> issues)
    : Exception("Kural yayınlanamadı: tanımdaki sorunları giderin.")
{
    public IReadOnlyList<AutomationIssue> Issues { get; } = issues;
}

/// <summary>
/// Aktif tenant'ın kuralları (ADR-0027). Yönetim işlemleri <c>core.automation.manage</c> (taslak) ve <c>core.automation.publish</c>
/// (yayın, açma/kapama) ister (API denetler); hepsi <c>core.automation-rule</c> kaydı olarak denetime yazılır. Yayınlanmış sürüm
/// değişmez: düzenleme yeni taslak sürüm açar. Dilim 1'de kurallar henüz çalışmaz (yürütme ADR-0027 Dilim 2).
/// </summary>
public interface IAutomationRuleService
{
    Task<AutomationRulePage> ListAsync(AutomationRuleQuery query, CancellationToken cancellationToken);

    /// <summary>Kural yoksa (ya da başka tenant'ın) null.</summary>
    Task<AutomationRuleDetail?> GetAsync(Guid ruleId, CancellationToken cancellationToken);

    /// <summary>Yeni taslak kural. Taslak eksik olabilir; tam doğrulama yayında yapılır.</summary>
    Task<AutomationRuleDetail> CreateAsync(
        string moduleKey, string entityKey, AutomationVersionContent content, CancellationToken cancellationToken);

    /// <summary>
    /// Taslağı günceller; kuralın taslağı yoksa (son sürüm yayınlanmışsa) yayınlanmış sürümden yeni taslak açar.
    /// <paramref name="expectedVersion"/> uyuşmazsa <see cref="AutomationRuleConflictException"/>. Kural yoksa null.
    /// </summary>
    Task<AutomationRuleDetail?> UpdateDraftAsync(
        Guid ruleId, AutomationVersionContent content, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Taslağı doğrular ve yayınlar. Sorun varsa <see cref="AutomationPublishException"/>. Kural/taslak yoksa null.</summary>
    Task<AutomationRuleDetail?> PublishAsync(Guid ruleId, bool activate, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Yayınlanmış kuralı açar/kapatır. Açarken tanım yeniden doğrulanır. Yayınlanmamış kural açılamaz.</summary>
    Task<AutomationRuleDetail?> SetActiveAsync(Guid ruleId, bool active, CancellationToken cancellationToken);

    /// <summary>
    /// Taslağı atar. Hiç yayınlanmamış kuralda kuralın kendisi de silinir; yayınlanmış sürümü olan kural silinmez (geçmiş ona bağlıdır).
    /// Kural yoksa false.
    /// </summary>
    Task<bool> DiscardDraftAsync(Guid ruleId, CancellationToken cancellationToken);
}
