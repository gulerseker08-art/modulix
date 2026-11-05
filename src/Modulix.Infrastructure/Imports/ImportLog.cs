using Microsoft.EntityFrameworkCore;
using Modulix.Core.Imports;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Imports;

/// <summary>
/// <see cref="IImportLog"/>: içe aktarma günlüğü okuması (ADR-0033, Dilim 1). Sorgular aktif tenant'ta çalışır (sorgu
/// süzgeci + RLS). Kişi adı firmanın üyesinin (veya eski üyesinin) görünen adıdır; üyeliği olmayan hesabın adı gösterilmez.
/// </summary>
internal sealed class ImportLog(ModulixDbContext db, ModuleCatalog catalog) : IImportLog
{
    public const string UnknownUser = "Eski kullanıcı";

    public async Task<ImportRunPage> ListAsync(ImportRunQuery query, Guid? onlyUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, ImportRunQuery.MaxPageSize);

        var runs = Visible(onlyUserId);
        if (!string.IsNullOrWhiteSpace(query.EntityKey))
        {
            runs = runs.Where(r => r.EntityKey == query.EntityKey);
        }

        if (query.Status is { } status)
        {
            runs = runs.Where(r => r.Status == status);
        }

        if (query.Mode is { } mode)
        {
            runs = runs.Where(r => r.Mode == mode);
        }

        if (query.UserId is { } userId)
        {
            runs = runs.Where(r => r.UserId == userId);
        }

        if (query.From is { } from)
        {
            runs = runs.Where(r => r.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            runs = runs.Where(r => r.CreatedAt < to);
        }

        var total = await runs.CountAsync(cancellationToken);
        var items = await runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new ImportRunPage(await SummariesAsync(items, cancellationToken), page, pageSize, total);
    }

    public async Task<ImportRunDetail?> GetAsync(Guid runId, ImportIssueQuery issues, Guid? onlyUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var run = await Visible(onlyUserId).SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var page = Math.Max(1, issues.Page);
        var pageSize = Math.Clamp(issues.PageSize, 1, ImportIssueQuery.MaxPageSize);

        var all = db.ImportRowIssues.AsNoTracking().Where(i => i.RunId == runId);
        var codes = await all.GroupBy(i => i.Code)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).ThenBy(g => g.Code)
            .ToListAsync(cancellationToken);

        var filtered = all;
        if (issues.Severity is { } severity)
        {
            filtered = filtered.Where(i => i.Severity == severity);
        }

        if (!string.IsNullOrWhiteSpace(issues.Code))
        {
            filtered = filtered.Where(i => i.Code == issues.Code);
        }

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered.OrderBy(i => i.RowNo).ThenBy(i => i.Code).ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new ImportIssueInfo(i.RowNo, i.ColumnKey, i.Code, i.Severity, i.Message))
            .ToListAsync(cancellationToken);

        return new ImportRunDetail(
            (await SummariesAsync([run], cancellationToken)).Single(),
            [.. codes.Select(c => new ImportIssueCodeCount(c.Code, c.Count))],
            new ImportIssuePage(items, page, pageSize, total));
    }

    private IQueryable<ImportRun> Visible(Guid? onlyUserId)
    {
        var runs = db.ImportRuns.AsNoTracking().AsQueryable();
        return onlyUserId is { } userId ? runs.Where(r => r.UserId == userId) : runs;
    }

    private async Task<List<ImportRunSummary>> SummariesAsync(IReadOnlyCollection<ImportRun> runs, CancellationToken cancellationToken)
    {
        var userIds = runs.Select(r => r.UserId).Distinct().ToList();
        var tenantIds = runs.Select(r => r.TenantId).Distinct().ToList();
        Dictionary<Guid, string> names = userIds.Count == 0
            ? []
            : await db.Memberships.AsNoTracking()
                .Where(m => tenantIds.Contains(m.TenantId) && userIds.Contains(m.UserId))
                .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName })
                .Distinct()
                .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);

        return
        [
            .. runs.Select(r => new ImportRunSummary(
                r.Id,
                r.UserId,
                names.GetValueOrDefault(r.UserId) ?? UnknownUser,
                r.EntityKey,
                catalog.FindAuditedEntity(r.EntityKey)?.DisplayName ?? r.EntityKey,
                r.FileName,
                r.FileSize,
                r.Mode,
                r.Policy,
                r.Status,
                r.TotalRows,
                r.CreatedCount,
                r.UpdatedCount,
                r.SkippedCount,
                r.FailedCount,
                r.CreatedAt,
                r.StartedAt,
                r.CompletedAt,
                r.Note)),
        ];
    }
}
