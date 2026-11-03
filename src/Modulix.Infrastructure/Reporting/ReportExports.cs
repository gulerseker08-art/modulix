using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Reporting;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Reporting;

/// <summary>
/// Dışa aktarma (ADR-0026, dilim 4). Dosya, ekrandaki sonuçla aynı hesaptan (<see cref="IReportRunner"/>) çağıranın yetkisiyle üretilir; bu yüzden
/// hassas alan, veri kapsamı ve tenant sınırı çalıştırıcıda uygulanır, burada tekrar hesap yoktur. Dosya yalnızca sahibine verilir. İndirmede
/// <b>yetki yeniden denetlenir</b>: rapor merkezi ve dışa aktarma izni, modül, veri kümesi okuma yetkisi ve hazırlandığı andaki erişim parmak izi
/// (<see cref="ReportAccessStamp"/>); biri tutmuyorsa dosya verilmez. Üretim ve indirme denetim kaydına yazılır (kim, kaç satır).
/// </summary>
internal sealed class ReportExports(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    IReportCenter center,
    IReportRunner runner,
    ReportColumnResolver resolver,
    ReportAccessStamp stamps,
    ICurrentAccess access,
    TimeProvider time) : IReportExports
{
    public async Task<ReportExportInfo?> CreateAsync(Guid reportId, CancellationToken cancellationToken)
    {
        if (await center.GetAsync(reportId, cancellationToken) is not { } report)
        {
            return null;
        }

        return await CreateCoreAsync(reportId, report.Summary.Name, report.Spec, ReportExportSource.Manual, cancellationToken);
    }

    public Task<ReportExportInfo> CreateAsync(ReportSpec spec, string? name, CancellationToken cancellationToken) =>
        CreateCoreAsync(null, string.IsNullOrWhiteSpace(name) ? "Rapor" : name.Trim(), ReportSpecJson.Defaulted(spec), ReportExportSource.Manual, cancellationToken);

    public async Task<ReportExportInfo?> CreateScheduledAsync(Guid reportId, CancellationToken cancellationToken) =>
        await center.GetAsync(reportId, cancellationToken) is { } report
            ? await CreateCoreAsync(reportId, report.Summary.Name, report.Spec, ReportExportSource.Schedule, cancellationToken)
            : null;

    private async Task<ReportExportInfo> CreateCoreAsync(
        Guid? reportId, string name, ReportSpec spec, ReportExportSource source, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan dışa aktarılamaz.");
        var now = time.GetUtcNow();
        if (await resolver.ResolveAsync(spec.Dataset, cancellationToken) is not { } resolved)
        {
            throw new ReportException("Rapor veri kümesi bulunamadı.");
        }

        if (resolved.Columns is null)
        {
            var message = resolved.Issues.FirstOrDefault()?.Message ?? "Bu veri kümesini dışa aktaramazsınız.";
            throw resolved.Access == ReportRunStatus.Forbidden ? new ReportForbiddenException(message) : new ReportException(message, resolved.Issues);
        }

        // Süresi geçmiş dosyalar önce silinir; sonra kullanıcının etkin dosya sayısı sınırlanır (depolama sınırsız büyümez).
        await db.ReportExports.Where(e => e.OwnerUserId == tenant.UserId && e.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        if (await db.ReportExports.CountAsync(e => e.OwnerUserId == tenant.UserId, cancellationToken) >= ReportExport.MaxActivePerUser)
        {
            throw new ReportException($"En fazla {ReportExport.MaxActivePerUser} hazır dosyanız olabilir; eskilerini silin ya da süresinin dolmasını bekleyin.");
        }

        var result = await runner.RunAsync(new ReportRunRequest(spec, null, 1, ReportLimits.MaxRowsRead, null, ForExport: true), cancellationToken);
        if (result.Status != ReportRunStatus.Ok)
        {
            var message = result.Issues.FirstOrDefault()?.Message ?? "Rapor çalıştırılamadı.";
            throw result.Status == ReportRunStatus.Forbidden ? new ReportForbiddenException(message) : new ReportException(message, result.Issues);
        }

        var bytes = ReportCsv.Build(result);
        if (bytes.Length > ReportExport.MaxBytes)
        {
            throw new ReportException($"Dosya çok büyük ({bytes.Length / 1024 / 1024} MB; sınır {ReportExport.MaxBytes / 1024 / 1024} MB). Süzgeçlerle daraltın.");
        }

        var rows = result.IsSummary ? result.Rows.Count : result.RowsMatched;
        var fileName = $"{ReportCsv.FileSlug(name)}-{now:yyyyMMdd-HHmm}.csv";
        var export = new ReportExport(
            tenant.UserId, reportId, name.Length > ReportDefinition.NameMaxLength ? name[..ReportDefinition.NameMaxLength] : name, resolved.Dataset.Key, fileName,
            bytes, rows, result.Truncated || result.RowsLimited, await stamps.ComputeAsync(resolved.Dataset.Key, cancellationToken), source, now);
        db.ReportExports.Add(export);
        audit.Add(new AuditEntry(
            tenant.TenantId, tenant.UserId, CoreModule.AuditedEntityKeys.Report, reportId ?? export.Id, AuditAction.Exported,
            new JsonObject { ["export"] = export.Id.ToString(), ["dataset"] = resolved.Dataset.Key, ["rows"] = rows, ["bytes"] = bytes.Length, ["source"] = source.ToString() },
            ExecutionSource.Api, null));
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(export, now);
    }

    public async Task<IReadOnlyList<ReportExportInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        var now = time.GetUtcNow();

        // İçerik (blob) okunmaz: yalnızca üst bilgi.
        var rows = await db.ReportExports.AsNoTracking().Where(e => e.OwnerUserId == me).OrderByDescending(e => e.CreatedAt).Take(100)
            .Select(e => new ReportExportInfo(
                e.Id, e.ReportId, e.ReportName, e.DatasetKey, e.FileName, e.SizeBytes, e.RowCount, e.Truncated, e.Source, e.CreatedAt, e.ExpiresAt, e.DownloadCount,
                e.ExpiresAt <= now))
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<ReportDownload> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var now = time.GetUtcNow();

        // Başkasının dosyası ile olmayan dosya aynı yanıtı verir (varlığı sızdırılmaz).
        var meta = await db.ReportExports.AsNoTracking().Where(e => e.Id == id && e.OwnerUserId == tenant.UserId)
            .Select(e => new { e.DatasetKey, e.AccessStamp, e.ExpiresAt }).SingleOrDefaultAsync(cancellationToken);
        if (meta is null)
        {
            return new ReportDownload(ReportDownloadStatus.NotFound, "Dosya bulunamadı.");
        }

        if (now >= meta.ExpiresAt)
        {
            return new ReportDownload(ReportDownloadStatus.Expired, "Dosyanın süresi doldu; raporu yeniden dışa aktarın.");
        }

        // İndirme anında yetki yeniden denetlenir (hazırlandıktan sonra izin/kapsam/modül değişmiş olabilir).
        if (!await access.HasPermissionAsync(CoreModule.Permissions.ReadReports, cancellationToken)
            || !await access.HasPermissionAsync(CoreModule.Permissions.ExportReports, cancellationToken))
        {
            return new ReportDownload(ReportDownloadStatus.Forbidden, "Rapor dışa aktarma yetkiniz yok.");
        }

        if (await resolver.ResolveAsync(meta.DatasetKey, cancellationToken) is not { Columns: not null })
        {
            return new ReportDownload(ReportDownloadStatus.Forbidden, "Bu verilere erişiminiz kalmadı ya da modül etkin değil.");
        }

        if (await stamps.ComputeAsync(meta.DatasetKey, cancellationToken) != meta.AccessStamp)
        {
            return new ReportDownload(ReportDownloadStatus.AccessChanged, "Erişim yetkiniz dosya hazırlandıktan sonra değişti; raporu yeniden dışa aktarın.");
        }

        var export = await db.ReportExports.SingleAsync(e => e.Id == id && e.OwnerUserId == tenant.UserId, cancellationToken);
        export.MarkDownloaded(now);
        audit.Add(new AuditEntry(
            tenant.TenantId, tenant.UserId, CoreModule.AuditedEntityKeys.Report, export.ReportId ?? export.Id, AuditAction.Exported,
            new JsonObject { ["export"] = export.Id.ToString(), ["download"] = true, ["dataset"] = export.DatasetKey }, ExecutionSource.Api, null));
        await db.SaveChangesAsync(cancellationToken);
        return new ReportDownload(ReportDownloadStatus.Ok, null, export.FileName, export.ContentType, export.Content);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        return await db.ReportExports.Where(e => e.Id == id && e.OwnerUserId == me).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return await db.ReportExports.Where(e => e.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }

    private static ReportExportInfo ToInfo(ReportExport e, DateTimeOffset now) => new(
        e.Id, e.ReportId, e.ReportName, e.DatasetKey, e.FileName, e.SizeBytes, e.RowCount, e.Truncated, e.Source, e.CreatedAt, e.ExpiresAt, e.DownloadCount,
        e.IsExpired(now));

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan dışa aktarma yönetilemez.");
}
