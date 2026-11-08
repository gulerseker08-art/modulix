using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Reporting;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Reporting;

/// <summary>
/// Kullanıcının zamanlanmış raporları (ADR-0026, dilim 4). Zamanlama yalnızca sahibine görünür ve yalnızca sahibinin görebildiği rapora kurulur.
/// Takvim kuralları saftır (<see cref="ReportScheduleRules"/>); saat firmanın saat dilimindedir. Oluşturma, değiştirme, açma/kapama ve silme
/// <c>core.report-schedule</c> kaydı olarak denetime yazılır. Çalıştırma <see cref="ReportSchedule"/> sınıfının açıkladığı kurallarla arka planda yapılır.
/// </summary>
internal sealed class ReportSchedules(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    IReportCenter center,
    ITenantClock clock,
    TimeProvider time) : IReportSchedules
{
    public async Task<IReadOnlyList<ReportScheduleInfo>> ListAsync(Guid? reportId, CancellationToken cancellationToken)
    {
        var me = RequireTenant().UserId;
        var schedules = await db.ReportSchedules.AsNoTracking()
            .Where(s => s.OwnerUserId == me && (reportId == null || s.ReportId == reportId))
            .OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
        var ids = schedules.Select(s => s.ReportId).Distinct().ToList();
        var names = await db.Reports.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        return [.. schedules.Select(s => ToInfo(s, names.GetValueOrDefault(s.ReportId)))];
    }

    public async Task<ReportScheduleInfo?> CreateAsync(Guid reportId, ReportScheduleInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (await center.GetAsync(reportId, cancellationToken) is not { } report)
        {
            return null;
        }

        Validate(input);
        if (await db.ReportSchedules.CountAsync(s => s.OwnerUserId == tenant.UserId, cancellationToken) >= ReportScheduleRules.MaxPerUser)
        {
            throw new ReportException($"En fazla {ReportScheduleRules.MaxPerUser} zamanlamanız olabilir.");
        }

        var day = await clock.GetAsync(cancellationToken);
        var next = ReportScheduleRules.NextRun(input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, day.UtcNow, day.Zone);
        var schedule = new ReportSchedule(reportId, tenant.UserId, input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, next, time.GetUtcNow(), input.Format);
        db.ReportSchedules.Add(schedule);
        Audit(tenant, schedule.Id, AuditAction.Created, null, Snapshot(schedule, report.Summary.Name));
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(schedule, report.Summary.Name);
    }

    public async Task<ReportScheduleInfo?> UpdateAsync(Guid id, ReportScheduleInput input, long? expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var schedule = await db.ReportSchedules.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == tenant.UserId, cancellationToken);
        if (schedule is null)
        {
            return null;
        }

        CheckVersion(schedule, expectedVersion);
        Validate(input);
        var name = await NameAsync(schedule.ReportId, cancellationToken);
        var before = Snapshot(schedule, name);
        var day = await clock.GetAsync(cancellationToken);
        schedule.Reschedule(
            input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth,
            ReportScheduleRules.NextRun(input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, day.UtcNow, day.Zone), time.GetUtcNow(), input.Format);
        Audit(tenant, schedule.Id, AuditAction.Updated, before, Snapshot(schedule, name));
        await SaveAsync(cancellationToken);
        return ToInfo(schedule, name);
    }

    public async Task<ReportScheduleInfo?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var schedule = await db.ReportSchedules.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == tenant.UserId, cancellationToken);
        if (schedule is null)
        {
            return null;
        }

        var name = await NameAsync(schedule.ReportId, cancellationToken);
        if (schedule.Enabled != enabled)
        {
            var before = Snapshot(schedule, name);
            var day = await clock.GetAsync(cancellationToken);
            schedule.SetEnabled(
                enabled, ReportScheduleRules.NextRun(schedule.Frequency, schedule.Hour, schedule.DayOfWeek, schedule.DayOfMonth, day.UtcNow, day.Zone), time.GetUtcNow());
            Audit(tenant, schedule.Id, AuditAction.Updated, before, Snapshot(schedule, name));
            await SaveAsync(cancellationToken);
        }

        return ToInfo(schedule, name);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var schedule = await db.ReportSchedules.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == tenant.UserId, cancellationToken);
        if (schedule is null)
        {
            return false;
        }

        Audit(tenant, schedule.Id, AuditAction.Deleted, Snapshot(schedule, await NameAsync(schedule.ReportId, cancellationToken)), null);
        db.ReportSchedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void Validate(ReportScheduleInput input)
    {
        if (ReportScheduleRules.Validate(input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth) is { } error)
        {
            throw new ReportException(error);
        }
    }

    private static void CheckVersion(ReportSchedule schedule, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != schedule.Version)
        {
            throw new ReportConflictException("Zamanlama başka bir işlemle değişti; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ReportConflictException("Zamanlama başka bir işlemle değişti; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private async Task<string?> NameAsync(Guid reportId, CancellationToken cancellationToken) =>
        await db.Reports.AsNoTracking().Where(r => r.Id == reportId).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken);

    private static ReportScheduleInfo ToInfo(ReportSchedule s, string? reportName) => new(
        s.Id, s.ReportId, reportName, s.Frequency, s.Hour, s.DayOfWeek, s.DayOfMonth, s.Enabled, s.NextRunAt, s.LastRunAt, s.LastStatus, s.LastMessage, s.StoppedReason,
        s.Version, s.Format);

    private static JsonObject Snapshot(ReportSchedule s, string? reportName) => new()
    {
        ["report"] = reportName ?? s.ReportId.ToString(),
        ["frequency"] = s.Frequency.ToString(),
        ["hour"] = s.Hour,
        ["dayOfWeek"] = s.DayOfWeek?.ToString(),
        ["dayOfMonth"] = s.DayOfMonth,
        ["format"] = s.Format.ToString(),
        ["enabled"] = s.Enabled,
        ["stoppedReason"] = s.StoppedReason,
    };

    private void Audit(TenantContext tenant, Guid scheduleId, AuditAction action, JsonObject? before, JsonObject? after)
    {
        var changes = RecordDiff.Compute(before, after);
        if (action == AuditAction.Updated && changes.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(tenant.TenantId, tenant.UserId, CoreModule.AuditedEntityKeys.ReportSchedule, scheduleId, action, changes, ExecutionSource.Api, null));
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan zamanlama yönetilemez.");
}
