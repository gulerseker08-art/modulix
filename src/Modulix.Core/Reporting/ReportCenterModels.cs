using Modulix.Core.Contracts.Reporting;

namespace Modulix.Core.Reporting;

/// <summary>Oluşturucuya sunulan sütun (çağıranın görebildiği). Seçenekler firmanın güncel etiketleriyle gelir.</summary>
public sealed record CatalogColumn(
    string Field, string Label, DatasetColumnKind Kind, ColumnUse Use, bool Custom, string? CurrencyField, IReadOnlyList<DatasetOption>? Options);

public sealed record CatalogDataset(
    string Key,
    string ModuleKey,
    string Name,
    int Version,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<string> DefaultColumns,
    IReadOnlyList<ReadyReport> ReadyReports,
    string? RecordPath);

public sealed record CatalogLink(string Key, string ModuleKey, string Title, string Description, string Path);

public sealed record ReportCatalogView(IReadOnlyList<CatalogDataset> Datasets, IReadOnlyList<CatalogLink> Links);

/// <summary>Rapor merkezinin kataloğu: etkin modüllerin, çağıranın okuyabildiği veri kümeleri ve görebildiği bağlantılar.</summary>
public interface IReportCatalog
{
    Task<ReportCatalogView> GetAsync(CancellationToken cancellationToken);
}

public enum ReportHealth
{
    Ok = 1,
    DatasetMissing = 2,
    ModuleDisabled = 3,

    /// <summary>Çağıranın bu veri kümesini okuma yetkisi yok (rapor görünür ama çalışmaz).</summary>
    Forbidden = 4,

    /// <summary>Tanımda artık geçersiz sütun var (özel alan arşivlendi, sütun kaldırıldı, yetkisiz sütun).</summary>
    ColumnsInvalid = 5,
}

public enum ReportListScope
{
    All = 1,
    Mine = 2,
    Shared = 3,
    Favorites = 4,
}

public sealed record ReportListQuery(ReportListScope Scope = ReportListScope.All, string? Search = null, int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 100;
}

public sealed record ReportSummary(
    Guid Id,
    string Name,
    string? Description,
    string DatasetKey,
    string? DatasetName,
    ReportVisibility Visibility,
    Guid OwnerUserId,
    string? OwnerName,
    bool IsOwner,
    bool IsFavorite,
    int SharedWithCount,
    DateTimeOffset UpdatedAt,
    long Version,
    ReportHealth Health,
    string? HealthMessage);

public sealed record ReportPage(IReadOnlyList<ReportSummary> Items, int Page, int PageSize, int TotalCount);

public sealed record ReportDetail(ReportSummary Summary, ReportSpec Spec, int DatasetVersion, IReadOnlyList<Guid> SharedWith);

public sealed record ReportInput(string Name, string? Description, ReportSpec Spec);

/// <summary>Girdi geçersiz (ad, tanım, sınır). <see cref="Issues"/> sütun/süzgeç sorunlarını taşır.</summary>
public sealed class ReportException(string message, IReadOnlyList<ReportIssue>? issues = null) : Exception(message)
{
    public IReadOnlyList<ReportIssue> Issues { get; } = issues ?? [];
}

/// <summary>Rapor başkası tarafından değiştirildi (sürüm uyuşmuyor). API 409.</summary>
public sealed class ReportConflictException(string message) : Exception(message);

/// <summary>Rapor görünür ama değiştirme hakkı yok (sahibi değil). API 403.</summary>
public sealed class ReportForbiddenException(string message) : Exception(message);

/// <summary>
/// Aktif tenant'ın kayıtlı raporları (ADR-0035, dilim 3). Görünürlük: sahibi, firmaya açık raporlar ve kendisiyle paylaşılanlar; paylaşım
/// kaldırılınca ya da rapor özel yapılınca hemen görünmez olur. Yazma işlemleri yalnızca sahibine açıktır ve denetim kaydına yazılır.
/// Çalıştırma her zaman çağıranın yetkisiyle yapılır (<see cref="IReportRunner"/>). Yetki (<c>core.reports.*</c>) uç noktada denetlenir.
/// </summary>
public interface IReportCenter
{
    Task<ReportPage> ListAsync(ReportListQuery query, CancellationToken cancellationToken);

    /// <summary>Rapor yoksa ya da çağıran göremiyorsa null (iki durum ayırt edilmez).</summary>
    Task<ReportDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ReportDetail> CreateAsync(ReportInput input, CancellationToken cancellationToken);

    /// <exception cref="ReportForbiddenException">Çağıran raporu görüyor ama sahibi değil.</exception>
    Task<ReportDetail?> UpdateAsync(Guid id, ReportInput input, long? expectedVersion, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="users">Yalnızca <see cref="ReportVisibility.Users"/> için; tenant'ın etkin üyeleri olmalı.</param>
    Task<ReportDetail?> ShareAsync(
        Guid id, ReportVisibility visibility, IReadOnlyList<Guid> users, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Rapor görünmüyorsa null.</summary>
    Task<bool?> SetFavoriteAsync(Guid id, bool favorite, CancellationToken cancellationToken);

    /// <summary>Kayıtlı raporu çağıranın yetkisiyle çalıştırır; rapor görünmüyorsa null.</summary>
    Task<ReportResult?> RunAsync(Guid id, ReportDrill? drill, int page, int pageSize, CancellationToken cancellationToken);
}
