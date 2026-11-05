namespace Modulix.Core.Imports;

public sealed record ImportRunQuery(
    string? EntityKey = null,
    ImportRunStatus? Status = null,
    ImportRunMode? Mode = null,
    Guid? UserId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50)
{
    public const int MaxPageSize = 100;
}

public sealed record ImportRunSummary(
    Guid Id,
    Guid UserId,
    string UserName,
    string EntityKey,
    string EntityName,
    string FileName,
    long FileSize,
    ImportRunMode Mode,
    ImportFailurePolicy Policy,
    ImportRunStatus Status,
    int TotalRows,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Note);

public sealed record ImportRunPage(IReadOnlyList<ImportRunSummary> Items, int Page, int PageSize, int TotalCount);

public sealed record ImportIssueQuery(ImportIssueSeverity? Severity = null, string? Code = null, int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 200;
}

public sealed record ImportIssueInfo(int RowNo, string? ColumnKey, string Code, ImportIssueSeverity Severity, string Message);

public sealed record ImportIssuePage(IReadOnlyList<ImportIssueInfo> Items, int Page, int PageSize, int TotalCount);

/// <summary>Hata kodu başına satır hatası sayısı (en sık hatalar özeti).</summary>
public sealed record ImportIssueCodeCount(string Code, int Count);

public sealed record ImportRunDetail(ImportRunSummary Run, IReadOnlyList<ImportIssueCodeCount> IssueCodes, ImportIssuePage Issues);

/// <summary>
/// İçe aktarma günlüğü okuması (ADR-0030, Dilim 1). Her işlem aktif tenant'ta çalışır (sorgu süzgeci + RLS).
/// <paramref name="onlyUserId"/> verilirse yalnızca o kullanıcının koşuları görünür: <c>core.imports.run</c> sahibi kendi
/// koşularını, <c>core.imports.read</c> sahibi firmanın tamamını görür (API karar verir; servis yetkiye bakmaz).
/// </summary>
public interface IImportLog
{
    Task<ImportRunPage> ListAsync(ImportRunQuery query, Guid? onlyUserId, CancellationToken cancellationToken);

    /// <summary>Koşu yoksa, başka tenant'ın ya da <paramref name="onlyUserId"/>'nin değilse null.</summary>
    Task<ImportRunDetail?> GetAsync(Guid runId, ImportIssueQuery issues, Guid? onlyUserId, CancellationToken cancellationToken);
}
