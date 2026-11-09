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
/// Kullanıcının zamanlanmış raporları (ADR-0035, dilim 4). Zamanlama yalnızca sahibine görünür ve yalnızca sahibinin görebildiği rapora kurulur.
/// Takvim kuralları saftır (<see cref="ReportScheduleRules"/>); saat firmanın saat dilimindedir. Oluşturma, değiştirme, açma/kapama ve silme
/// <c>core.report-schedule</c> kaydı olarak denetime yazılır. Çalıştırma <see cref="ReportSchedule"/> sınıfının açıkladığı kurallarla arka planda yapılır.
/// </summary>
internal sealed class ReportSchedules(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    IReportCenter center,
    ITenantClock clock,
    ITenantMembers members,
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
        var recipients = await LoadRecipientsAsync(schedules.Select(s => s.Id).ToList(), cancellationToken);
        return [.. schedules.Select(s => ToInfo(s, names.GetValueOrDefault(s.ReportId), recipients.GetValueOrDefault(s.Id)))];
    }

    public async Task<ReportScheduleInfo?> CreateAsync(Guid reportId, ReportScheduleInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (await center.GetAsync(reportId, cancellationToken) is not { } report)
        {
            return null;
        }

        Validate(input);
        var wanted = await ResolveRecipientsAsync(input.RecipientIds, tenant.UserId, cancellationToken) ?? [];
        if (await db.ReportSchedules.CountAsync(s => s.OwnerUserId == tenant.UserId, cancellationToken) >= ReportScheduleRules.MaxPerUser)
        {
            throw new ReportException($"En fazla {ReportScheduleRules.MaxPerUser} zamanlamanız olabilir.");
        }

        var day = await clock.GetAsync(cancellationToken);
        var next = ReportScheduleRules.NextRun(input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, day.UtcNow, day.Zone);
        var schedule = new ReportSchedule(reportId, tenant.UserId, input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, next, time.GetUtcNow(), input.Format);
        db.ReportSchedules.Add(schedule);
        ReplaceRecipients(schedule.Id, [], wanted);
        Audit(tenant, schedule.Id, AuditAction.Created, null, Snapshot(schedule, report.Summary.Name, wanted));
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(schedule, report.Summary.Name, await RecipientInfosAsync(wanted, cancellationToken));
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
        var current = await db.ReportScheduleRecipients.Where(r => r.ScheduleId == id).ToListAsync(cancellationToken);
        var currentIds = current.Select(r => r.UserId).ToList();
        var wanted = await ResolveRecipientsAsync(input.RecipientIds, tenant.UserId, cancellationToken) ?? currentIds;
        var name = await NameAsync(schedule.ReportId, cancellationToken);
        var before = Snapshot(schedule, name, currentIds);
        var day = await clock.GetAsync(cancellationToken);
        schedule.Reschedule(
            input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth,
            ReportScheduleRules.NextRun(input.Frequency, input.Hour, input.DayOfWeek, input.DayOfMonth, day.UtcNow, day.Zone), time.GetUtcNow(), input.Format);
        ReplaceRecipients(schedule.Id, current, wanted);
        Audit(tenant, schedule.Id, AuditAction.Updated, before, Snapshot(schedule, name, wanted));
        await SaveAsync(cancellationToken);
        return ToInfo(schedule, name, await RecipientInfosAsync(wanted, cancellationToken));
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
        var recipientIds = await db.ReportScheduleRecipients.AsNoTracking().Where(r => r.ScheduleId == id).Select(r => r.UserId).ToListAsync(cancellationToken);
        if (schedule.Enabled != enabled)
        {
            var before = Snapshot(schedule, name, recipientIds);
            var day = await clock.GetAsync(cancellationToken);
            schedule.SetEnabled(
                enabled, ReportScheduleRules.NextRun(schedule.Frequency, schedule.Hour, schedule.DayOfWeek, schedule.DayOfMonth, day.UtcNow, day.Zone), time.GetUtcNow());
            Audit(tenant, schedule.Id, AuditAction.Updated, before, Snapshot(schedule, name, recipientIds));
            await SaveAsync(cancellationToken);
        }

        return ToInfo(schedule, name, await RecipientInfosAsync(recipientIds, cancellationToken));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var schedule = await db.ReportSchedules.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == tenant.UserId, cancellationToken);
        if (schedule is null)
        {
            return false;
        }

        var deletedRecipients = await db.ReportScheduleRecipients.AsNoTracking().Where(r => r.ScheduleId == id).Select(r => r.UserId).ToListAsync(cancellationToken);
        Audit(tenant, schedule.Id, AuditAction.Deleted, Snapshot(schedule, await NameAsync(schedule.ReportId, cancellationToken), deletedRecipients), null);
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

    /// <summary>İstenen alıcıları doğrular: sahip düşer, tekrarlar birleşir, en çok <see cref="ReportScheduleRules.MaxRecipients"/>, hepsi etkin üye. null = istek yok.</summary>
    private async Task<IReadOnlyList<Guid>?> ResolveRecipientsAsync(IReadOnlyList<Guid>? requested, Guid ownerId, CancellationToken cancellationToken)
    {
        if (requested is null)
        {
            return null;
        }

        var wanted = requested.Where(id => id != ownerId).Distinct().ToList();
        if (wanted.Count > ReportScheduleRules.MaxRecipients)
        {
            throw new ReportException($"En fazla {ReportScheduleRules.MaxRecipients} alıcı eklenebilir.");
        }

        if (wanted.Count > 0)
        {
            var active = (await members.ListActiveAsync(cancellationToken)).Select(m => m.UserId).ToHashSet();
            if (wanted.Any(id => !active.Contains(id)))
            {
                throw new ReportException("Alıcılar firmanın etkin üyeleri olmalıdır.");
            }
        }

        return wanted;
    }

    /// <summary>Alıcı kümesini istenene eşitler: listede olmayan kayıtları siler, eksik olanları ekler.</summary>
    private void ReplaceRecipients(Guid scheduleId, IReadOnlyList<ReportScheduleRecipient> current, IReadOnlyList<Guid> wanted)
    {
        db.ReportScheduleRecipients.RemoveRange(current.Where(r => !wanted.Contains(r.UserId)));
        var now = time.GetUtcNow();
        foreach (var userId in wanted.Where(id => current.All(r => r.UserId != id)))
        {
            db.ReportScheduleRecipients.Add(new ReportScheduleRecipient(scheduleId, userId, now));
        }
    }

    private async Task<Dictionary<Guid, List<ReportScheduleRecipientInfo>>> LoadRecipientsAsync(IReadOnlyList<Guid> scheduleIds, CancellationToken cancellationToken)
    {
        var rows = await db.ReportScheduleRecipients.AsNoTracking().Where(r => scheduleIds.Contains(r.ScheduleId)).OrderBy(r => r.CreatedAt)
            .Select(r => new { r.ScheduleId, r.UserId }).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var names = (await members.ListActiveAsync(cancellationToken)).ToDictionary(m => m.UserId, m => m.DisplayName);
        return rows.GroupBy(r => r.ScheduleId).ToDictionary(g => g.Key, g => g.Select(r => new ReportScheduleRecipientInfo(r.UserId, names.GetValueOrDefault(r.UserId))).ToList());
    }

    private async Task<List<ReportScheduleRecipientInfo>> RecipientInfosAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var names = (await members.ListActiveAsync(cancellationToken)).ToDictionary(m => m.UserId, m => m.DisplayName);
        return [.. ids.Select(id => new ReportScheduleRecipientInfo(id, names.GetValueOrDefault(id)))];
    }

    private static ReportScheduleInfo ToInfo(ReportSchedule s, string? reportName, IReadOnlyList<ReportScheduleRecipientInfo>? recipients) => new(
        s.Id, s.ReportId, reportName, s.Frequency, s.Hour, s.DayOfWeek, s.DayOfMonth, s.Enabled, s.NextRunAt, s.LastRunAt, s.LastStatus, s.LastMessage, s.StoppedReason,
        s.Version, s.Format, recipients ?? []);

    private static JsonObject Snapshot(ReportSchedule s, string? reportName, IReadOnlyList<Guid> recipientIds) => new()
    {
        ["report"] = reportName ?? s.ReportId.ToString(),
        ["frequency"] = s.Frequency.ToString(),
        ["hour"] = s.Hour,
        ["dayOfWeek"] = s.DayOfWeek?.ToString(),
        ["dayOfMonth"] = s.DayOfMonth,
        ["format"] = s.Format.ToString(),
        ["recipients"] = recipientIds.Count == 0 ? null : string.Join(",", recipientIds.Order()),
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
