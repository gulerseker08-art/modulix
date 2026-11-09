using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Reporting;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Reporting;

/// <summary>
/// Kayıtlı raporlar (ADR-0035, dilim 3). Raporlar ve paylaşımları tenant verisidir (query filter + RLS); görünürlük her okumada yeniden
/// hesaplanır, bu yüzden paylaşım kaldırılınca ya da rapor özel yapılınca erişim hemen biter. Rapor tanımı veri erişimi vermez; çalıştırma
/// çağıranın yetkisiyle <see cref="IReportRunner"/> üzerinden yapılır. Her yazma <c>core.report</c> kaydı olarak denetime yazılır.
/// </summary>
internal sealed class ReportCenter(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    ModuleCatalog catalog,
    ReportColumnResolver resolver,
    IReportRunner runner,
    ITenantMembers members,
    TimeProvider time) : IReportCenter
{
    public async Task<ReportPage> ListAsync(ReportListQuery query, CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, ReportListQuery.MaxPageSize);

        var reports = Visible(me);
        reports = query.Scope switch
        {
            ReportListScope.Mine => reports.Where(r => r.OwnerUserId == me),
            ReportListScope.Shared => reports.Where(r => r.OwnerUserId != me),
            ReportListScope.Favorites => reports.Where(r => db.ReportFavorites.Any(f => f.ReportId == r.Id && f.UserId == me)),
            _ => reports,
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            reports = reports.Where(r => r.Name.Contains(search));
        }

        var total = await reports.CountAsync(cancellationToken);
        var items = await reports.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new ReportPage(await SummariesAsync(items, me, cancellationToken), page, pageSize, total);
    }

    public async Task<ReportDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        var report = await Visible(me).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return report is null ? null : await DetailAsync(report, me, cancellationToken);
    }

    public async Task<ReportDetail> CreateAsync(ReportInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (spec, dataset) = await ValidatedAsync(input, cancellationToken);
        var name = NormalizeName(input.Name);
        var description = NormalizeDescription(input.Description);

        if (await db.Reports.CountAsync(r => r.OwnerUserId == tenant.UserId, cancellationToken) >= ReportDefinition.MaxPerUser)
        {
            throw new ReportException($"En fazla {ReportDefinition.MaxPerUser} rapor oluşturabilirsiniz.");
        }

        await EnsureNameFreeAsync(tenant.UserId, name, null, cancellationToken);
        var report = ReportDefinition.Create(tenant.UserId, name, description, dataset.Key, dataset.Version, ReportSpecJson.Serialize(spec), time.GetUtcNow());
        db.Reports.Add(report);
        Audit(tenant, report.Id, AuditAction.Created, null, Snapshot(report, dataset, spec, []));
        await SaveAsync(cancellationToken);
        return await DetailAsync(report, tenant.UserId, cancellationToken);
    }

    public async Task<ReportDetail?> UpdateAsync(Guid id, ReportInput input, long? expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var report = await Visible(tenant.UserId).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return null;
        }

        RequireOwner(report, tenant.UserId);
        CheckVersion(report, expectedVersion);
        var (spec, dataset) = await ValidatedAsync(input, cancellationToken);
        var name = NormalizeName(input.Name);
        var description = NormalizeDescription(input.Description);
        await EnsureNameFreeAsync(tenant.UserId, name, report.Id, cancellationToken);

        var shared = await SharedWithAsync(report.Id, cancellationToken);
        var before = Snapshot(report, catalog.FindDataset(report.DatasetKey)?.Dataset, TryReadSpec(report), shared);
        report.Update(name, description, dataset.Key, dataset.Version, ReportSpecJson.Serialize(spec), time.GetUtcNow());
        Audit(tenant, report.Id, AuditAction.Updated, before, Snapshot(report, dataset, spec, shared));
        await SaveAsync(cancellationToken);
        return await DetailAsync(report, tenant.UserId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var report = await Visible(tenant.UserId).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return false;
        }

        RequireOwner(report, tenant.UserId);
        var shared = await SharedWithAsync(report.Id, cancellationToken);
        Audit(tenant, report.Id, AuditAction.Deleted, Snapshot(report, catalog.FindDataset(report.DatasetKey)?.Dataset, TryReadSpec(report), shared), null);
        db.Reports.Remove(report);
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<ReportDetail?> ShareAsync(
        Guid id, ReportVisibility visibility, IReadOnlyList<Guid> users, long? expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var report = await Visible(tenant.UserId).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return null;
        }

        RequireOwner(report, tenant.UserId);
        CheckVersion(report, expectedVersion);
        if (!Enum.IsDefined(visibility))
        {
            throw new ReportException("Paylaşım türü geçersiz.");
        }

        var wanted = visibility == ReportVisibility.Users ? users.Where(u => u != tenant.UserId).Distinct().ToList() : [];
        if (visibility == ReportVisibility.Users)
        {
            if (wanted.Count == 0)
            {
                throw new ReportException("Paylaşmak için en az bir kullanıcı seçin.");
            }

            var active = (await members.ListActiveAsync(cancellationToken)).Select(m => m.UserId).ToHashSet();
            if (wanted.Any(u => !active.Contains(u)))
            {
                throw new ReportException("Seçilen kullanıcılardan biri firmanın etkin üyesi değil.");
            }
        }

        var current = await db.ReportShares.Where(s => s.ReportId == report.Id).ToListAsync(cancellationToken);
        var before = Snapshot(report, catalog.FindDataset(report.DatasetKey)?.Dataset, TryReadSpec(report), [.. current.Select(s => s.UserId)]);
        var now = time.GetUtcNow();
        db.ReportShares.RemoveRange(current.Where(s => !wanted.Contains(s.UserId)));
        db.ReportShares.AddRange(wanted.Where(u => current.All(s => s.UserId != u)).Select(u => new ReportShare(report.Id, u, tenant.UserId, now)));
        report.SetVisibility(visibility, now);
        Audit(tenant, report.Id, AuditAction.Updated, before, Snapshot(report, catalog.FindDataset(report.DatasetKey)?.Dataset, TryReadSpec(report), wanted));
        await SaveAsync(cancellationToken);
        return await DetailAsync(report, tenant.UserId, cancellationToken);
    }

    public async Task<bool?> SetFavoriteAsync(Guid id, bool favorite, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await Visible(tenant.UserId).AnyAsync(r => r.Id == id, cancellationToken))
        {
            return null;
        }

        var existing = await db.ReportFavorites.SingleOrDefaultAsync(f => f.ReportId == id && f.UserId == tenant.UserId, cancellationToken);
        if (favorite && existing is null)
        {
            db.ReportFavorites.Add(new ReportFavorite(id, tenant.UserId, time.GetUtcNow()));
            await SaveAsync(cancellationToken);
        }
        else if (!favorite && existing is not null)
        {
            db.ReportFavorites.Remove(existing);
            await SaveAsync(cancellationToken);
        }

        return favorite;
    }

    public async Task<ReportResult?> RunAsync(Guid id, ReportDrill? drill, int page, int pageSize, CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        var report = await Visible(me).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return null;
        }

        if (TryReadSpec(report) is not { } spec)
        {
            return ReportResult.Failed(ReportRunStatus.Invalid,
                [new ReportIssue(ReportIssueCode.UnknownColumn, null, "Rapor tanımı okunamadı. Raporu düzenleyip yeniden kaydedin.")], time.GetUtcNow());
        }

        return await runner.RunAsync(new ReportRunRequest(spec, drill, page, pageSize, report.DatasetVersion), cancellationToken);
    }

    // ---- Görünürlük

    /// <summary>Çağıranın görebildiği raporlar: kendisinin, firmaya açık olanlar ve kendisiyle paylaşılanlar.</summary>
    private IQueryable<ReportDefinition> Visible(Guid me) =>
        db.Reports.Where(r =>
            r.OwnerUserId == me
            || r.Visibility == ReportVisibility.Tenant
            || (r.Visibility == ReportVisibility.Users && db.ReportShares.Any(s => s.ReportId == r.Id && s.UserId == me)));

    private static void RequireOwner(ReportDefinition report, Guid me)
    {
        if (report.OwnerUserId != me)
        {
            throw new ReportForbiddenException("Raporu yalnızca sahibi değiştirebilir. Kopyasını oluşturabilirsiniz.");
        }
    }

    private static void CheckVersion(ReportDefinition report, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != report.Version)
        {
            throw new ReportConflictException("Rapor başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
    }

    // ---- Doğrulama

    private async Task<(ReportSpec Spec, DatasetDefinition Dataset)> ValidatedAsync(ReportInput input, CancellationToken cancellationToken)
    {
        var spec = ReportSpecJson.Defaulted(input.Spec);
        if (await resolver.ResolveAsync(spec.Dataset, cancellationToken) is not { } resolved)
        {
            throw new ReportException("Rapor veri kümesi bulunamadı.", [new ReportIssue(ReportIssueCode.UnknownDataset, null, "Rapor veri kümesi bulunamadı.")]);
        }

        if (resolved.Columns is not { } columns)
        {
            throw new ReportException(resolved.Issues.FirstOrDefault()?.Message ?? "Bu veri kümesi için rapor oluşturamazsınız.", resolved.Issues);
        }

        spec = ReportSpecValidator.Normalize(spec, columns);
        var issues = ReportSpecValidator.Validate(spec, columns);
        if (issues.Count > 0)
        {
            throw new ReportException("Rapor tanımı geçersiz: " + issues[0].Message, issues);
        }

        if (ReportSpecJson.Serialize(spec).Length > ReportDefinition.SpecMaxLength)
        {
            throw new ReportException("Rapor tanımı çok büyük.");
        }

        return (spec, resolved.Dataset);
    }

    private async Task EnsureNameFreeAsync(Guid owner, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await db.Reports.AnyAsync(r => r.OwnerUserId == owner && r.Name == name && r.Id != exceptId, cancellationToken))
        {
            throw new ReportException("Bu adla bir raporunuz zaten var.");
        }
    }

    private static string NormalizeName(string? name)
    {
        try
        {
            return ReportDefinition.NormalizeName(name);
        }
        catch (ArgumentException ex)
        {
            throw new ReportException(Clean(ex));
        }
    }

    private static string? NormalizeDescription(string? description)
    {
        try
        {
            return ReportDefinition.NormalizeDescription(description);
        }
        catch (ArgumentException ex)
        {
            throw new ReportException(Clean(ex));
        }
    }

    private static string Clean(ArgumentException ex) =>
        ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);

    // ---- Özet ve ayrıntı

    private async Task<IReadOnlyList<ReportSummary>> SummariesAsync(List<ReportDefinition> items, Guid me, CancellationToken cancellationToken)
    {
        var ids = items.Select(r => r.Id).ToList();
        var favorites = (await db.ReportFavorites.AsNoTracking().Where(f => f.UserId == me && ids.Contains(f.ReportId)).Select(f => f.ReportId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var shareCounts = await db.ReportShares.AsNoTracking().Where(s => ids.Contains(s.ReportId))
            .GroupBy(s => s.ReportId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var names = (await members.ListActiveAsync(cancellationToken)).ToDictionary(m => m.UserId, m => m.DisplayName);

        var summaries = new List<ReportSummary>();
        foreach (var report in items)
        {
            var (status, message) = await HealthAsync(report, cancellationToken);
            summaries.Add(new ReportSummary(
                report.Id, report.Name, report.Description, report.DatasetKey, catalog.FindDataset(report.DatasetKey)?.Dataset.DisplayName, report.Visibility,
                report.OwnerUserId, names.GetValueOrDefault(report.OwnerUserId), report.OwnerUserId == me, favorites.Contains(report.Id),
                shareCounts.GetValueOrDefault(report.Id), report.UpdatedAt, report.Version, status, message));
        }

        return summaries;
    }

    /// <summary>Raporun şu anki katalogla çalışabilirliği: veri kümesi, modül, yetki ve sütunlar. Veri okumaz.</summary>
    private async Task<(ReportHealth Health, string? Message)> HealthAsync(ReportDefinition report, CancellationToken cancellationToken)
    {
        if (catalog.FindDataset(report.DatasetKey) is null)
        {
            return (ReportHealth.DatasetMissing, "Rapor veri kümesi artık yok.");
        }

        var resolved = await resolver.ResolveAsync(report.DatasetKey, cancellationToken);
        if (resolved is null)
        {
            return (ReportHealth.DatasetMissing, "Rapor veri kümesi artık yok.");
        }

        if (resolved.Columns is not { } columns)
        {
            return (resolved.Access == ReportRunStatus.ModuleDisabled ? ReportHealth.ModuleDisabled : ReportHealth.Forbidden, resolved.Issues.FirstOrDefault()?.Message);
        }

        if (TryReadSpec(report) is not { } spec)
        {
            return (ReportHealth.ColumnsInvalid, "Rapor tanımı okunamadı.");
        }

        var issues = ReportSpecValidator.Validate(ReportSpecValidator.Normalize(spec, columns), columns);
        return issues.Count == 0 ? (ReportHealth.Ok, null) : (ReportHealth.ColumnsInvalid, issues[0].Message);
    }

    private async Task<ReportDetail> DetailAsync(ReportDefinition report, Guid me, CancellationToken cancellationToken)
    {
        var summary = (await SummariesAsync([report], me, cancellationToken))[0];
        var spec = TryReadSpec(report) ?? new ReportSpec(report.DatasetKey, [], [], [], []);
        var sharedWith = report.OwnerUserId == me ? await SharedWithAsync(report.Id, cancellationToken) : [];
        return new ReportDetail(summary, spec, report.DatasetVersion, sharedWith);
    }

    private async Task<List<Guid>> SharedWithAsync(Guid reportId, CancellationToken cancellationToken) =>
        await db.ReportShares.AsNoTracking().Where(s => s.ReportId == reportId).Select(s => s.UserId).ToListAsync(cancellationToken);

    private static ReportSpec? TryReadSpec(ReportDefinition report)
    {
        try
        {
            return ReportSpecJson.Deserialize(report.SpecJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Denetim

    private static JsonObject Snapshot(ReportDefinition report, DatasetDefinition? dataset, ReportSpec? spec, IReadOnlyList<Guid> sharedWith) =>
        new()
        {
            ["name"] = report.Name,
            ["description"] = report.Description,
            ["dataset"] = dataset?.DisplayName ?? report.DatasetKey,
            ["visibility"] = report.Visibility.ToString(),
            ["sharedWith"] = string.Join(", ", sharedWith.OrderBy(u => u)),
            ["summary"] = spec is null ? null : Summarize(spec),
        };

    private static string Summarize(ReportSpec spec)
    {
        var parts = new List<string>();
        parts.Add(spec.IsSummary
            ? $"özet: {spec.Groups.Count} gruplama, {spec.Measures.Count} ölçü"
            : $"tablo: {spec.Columns.Count} sütun");
        if (spec.Filters.Count > 0)
        {
            parts.Add($"{spec.Filters.Count} süzgeç");
        }

        if (spec.Chart != ChartKind.None)
        {
            parts.Add($"grafik: {spec.Chart}");
        }

        return string.Join(", ", parts);
    }

    private void Audit(TenantContext tenant, Guid reportId, AuditAction action, JsonObject? before, JsonObject? after)
    {
        var changes = RecordDiff.Compute(before, after);
        if (action == AuditAction.Updated && changes.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(tenant.TenantId, tenant.UserId, CoreModule.AuditedEntityKeys.Report, reportId, action, changes, ExecutionSource.Api, null));
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ReportConflictException("Rapor başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
        catch (DbUpdateException)
        {
            // En olası neden eşzamanlı aynı ad ya da aynı paylaşım; ön denetimden sonra yarışan ikinci kayıt.
            throw new ReportConflictException("Rapor kaydedilemedi: aynı ad ya da paylaşım başka bir işlemle çakıştı. Tekrar deneyin.");
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan raporlar yönetilemez.");
}
