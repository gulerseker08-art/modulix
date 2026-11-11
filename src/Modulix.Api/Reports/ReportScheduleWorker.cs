using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Reporting;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Api.Reports;

/// <summary>"ReportJobs" yapılandırma bölümü.</summary>
public sealed class ReportJobsOptions
{
    public const string SectionName = "ReportJobs";

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="ReportScheduleRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 300;
}

/// <summary>Bir tenant turunun özeti: vakti gelen, teslim edilen, durdurulan, hata alan, başka örnekçe alındığı için atlanan zamanlama; silinen süresi geçmiş dosya.</summary>
public sealed record ReportJobsRun(int Due, int Delivered, int Stopped, int Failed, int Skipped, int Purged);

/// <summary>
/// Bir tenant'ın zamanlanmış raporlarını çalıştırır (ADR-0035, dilim 4). Her zamanlama <b>sahibinin yetkisiyle</b> çalışır: sahibin tenant bağlamı kurulur
/// ve rapor merkezi, dışa aktarma ve zamanlama izinleri, modül, rapor görünürlüğü ve alıcının etkin üyeliği o anda yeniden denetlenir. Biri bozulduysa
/// zamanlama nedeniyle <b>durdurulur</b> ve hiçbir dosya ya da e-posta üretilmez. Hata bir zamanlamayı, bir tenant'ı ya da turu durdurmaz.
/// <list type="bullet">
///   <item>Tur, tenant başına oturum kilidiyle (<c>sp_getapplock</c>) tek örnekte koşar; kilidi alamayan örnek atlar.</item>
///   <item>Her çalışma (zamanlama, planlı an) için tek kayıt yazılır (<c>UX (ScheduleId, Slot)</c>): yarışan ya da tekrar eden çalışma ikinci dosya
///   ya da e-posta üretmez. Yarıda kesilen ("Started") çalışma <see cref="AbandonedAfter"/> sonra yeniden alınır.</item>
///   <item>E-postada veri yoktur; yalnızca rapor adı, satır sayısı ve indirme bağlantısı. Dosya indirilirken yetki yine denetlenir.</item>
///   <item>Geçici hata (rapor çalışamadı, depolama...) çalışmayı "Failed" yapar ve bir sonraki planlı ana geçer; kalıcı yetki kaybı durdurur.</item>
/// </list>
/// </summary>
public sealed class ReportScheduleRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<ReportScheduleRunner> logger)
{
    public const string LockName = "modulix.reports.jobs";

    /// <summary>Bir turda işlenen en çok zamanlama (kalanlar sonraki turda).</summary>
    public const int MaxPerRun = 50;

    /// <summary>"Started" bir çalışma bu süreden eskiyse yarıda kesilmiş sayılır ve yeniden alınır.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(15);

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    private enum Outcome
    {
        Delivered,
        Stopped,
        Failed,
        Skipped,
    }

    public async Task<(ReportJobsRun? Run, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var system = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = system.ServiceProvider;

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return (null, true);
        }

        try
        {
            var db = services.GetRequiredService<ModulixDbContext>();
            var now = time.GetUtcNow();
            var purged = await services.GetRequiredService<IReportExports>().PurgeExpiredAsync(cancellationToken);
            var due = await db.ReportSchedules.AsNoTracking().Where(s => s.Enabled && s.NextRunAt <= now)
                .OrderBy(s => s.NextRunAt).Select(s => s.Id).Take(MaxPerRun).ToListAsync(cancellationToken);

            var counts = new Dictionary<Outcome, int>();
            foreach (var id in due)
            {
                try
                {
                    var outcome = await ProcessAsync(tenantId, id, cancellationToken);
                    counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Tenant {TenantId} zamanlanmış rapor {ScheduleId} işlenemedi.", tenantId, id);
                    counts[Outcome.Failed] = counts.GetValueOrDefault(Outcome.Failed) + 1;
                }
            }

            return (new ReportJobsRun(due.Count, counts.GetValueOrDefault(Outcome.Delivered), counts.GetValueOrDefault(Outcome.Stopped),
                counts.GetValueOrDefault(Outcome.Failed), counts.GetValueOrDefault(Outcome.Skipped), purged), false);
        }
        finally
        {
            await ReleaseLockAsync(lockConnection, tenantId, cancellationToken);
        }
    }

    private async Task<Outcome> ProcessAsync(Guid tenantId, Guid scheduleId, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(tenantId, scheduleId, cancellationToken);
        if (claim is null)
        {
            return Outcome.Skipped;
        }

        var (outcome, exportId, message) = await DeliverAsync(tenantId, claim, cancellationToken);
        await FinishAsync(tenantId, scheduleId, claim.RunId, outcome, exportId, message, cancellationToken);
        return outcome;
    }

    private sealed record Claim(Guid RunId, Guid ScheduleId, Guid OwnerUserId, Guid ReportId, ReportFileFormat Format);

    /// <summary>Çalışmayı bu örnek için kaydeder; başka örnek aynı planlı anı almışsa (ya da zamanlama değişmişse) null.</summary>
    private async Task<Claim?> ClaimAsync(Guid tenantId, Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var system = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var db = system.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var now = time.GetUtcNow();

        var schedule = await db.ReportSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scheduleId && s.Enabled && s.NextRunAt <= now, cancellationToken);
        if (schedule is null)
        {
            return null;
        }

        var existing = await db.ReportScheduleRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ScheduleId == scheduleId && r.Slot == schedule.NextRunAt, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status != "Started")
            {
                // Aynı planlı an zaten bitmiş (tekrar teslim): yeniden çalışmaz; zamanlama aynı ana takılı kalmasın diye bir sonraki ana ilerletilir.
                var day = await system.ServiceProvider.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
                var next = ReportScheduleRules.NextRun(schedule.Frequency, schedule.Hour, schedule.DayOfWeek, schedule.DayOfMonth, day.UtcNow, day.Zone);
                await db.ReportSchedules.Where(s => s.Id == scheduleId && s.NextRunAt == schedule.NextRunAt)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.NextRunAt, next), cancellationToken);
                return null;
            }

            // Yarıda kesilmişse (eski "Started") atomik olarak yeniden alınır.
            var cutoff = now - AbandonedAfter;
            var reclaimed = await db.ReportScheduleRuns.Where(r => r.Id == existing.Id && r.Status == "Started" && r.StartedAt < cutoff)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.StartedAt, now), cancellationToken);
            return reclaimed == 1 ? new Claim(existing.Id, scheduleId, schedule.OwnerUserId, schedule.ReportId, schedule.Format) : null;
        }

        var run = new ReportScheduleRun(scheduleId, schedule.NextRunAt, now);
        db.ReportScheduleRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Aynı anda başka örnek kaydı yazdı (benzersiz kısıt): bu örnek atlar.
            return null;
        }

        return new Claim(run.Id, scheduleId, schedule.OwnerUserId, schedule.ReportId, schedule.Format);
    }

    /// <summary>Dosyayı sahibin bağlamında üretir ve e-postayı kuyruğa yazar. Yetki ya da üyelik bozuksa durdurma nedeni döner; hiçbir şey üretilmez.</summary>
    private async Task<(Outcome Outcome, Guid? ExportId, string? Message)> DeliverAsync(Guid tenantId, Claim claim, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

        // Sahibin etkin üyeliği olmadan sahibin bağlamı kurulamaz.
        Guid membershipId;
        await using (var lookup = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty)))
        {
            var membership = await lookup.ServiceProvider.GetRequiredService<ModulixDbContext>().Memberships.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.UserId == claim.OwnerUserId)
                .Select(m => new { m.Id, m.Status }).SingleOrDefaultAsync(cancellationToken);
            if (membership is not { Status: MembershipStatus.Active })
            {
                return (Outcome.Stopped, null, "Zamanlama sahibinin firmadaki üyeliği etkin değil; teslim durduruldu.");
            }

            membershipId = membership.Id;
        }

        await using var owner = tenantScopes.Create(new TenantContext(tenantId, claim.OwnerUserId, membershipId));
        var services = owner.ServiceProvider;
        var access = services.GetRequiredService<ICurrentAccess>();
        foreach (var permission in new[] { CoreModule.Permissions.ReadReports, CoreModule.Permissions.ExportReports, CoreModule.Permissions.ScheduleReports })
        {
            if (!await access.HasPermissionAsync(permission, cancellationToken))
            {
                return (Outcome.Stopped, null, "Zamanlama sahibinin rapor, dışa aktarma ya da zamanlama yetkisi kalmadı; teslim durduruldu.");
            }
        }

        try
        {
            var recipient = await services.GetRequiredService<INotificationRecipients>().FindAsync(claim.OwnerUserId, cancellationToken);
            if (recipient is null)
            {
                return (Outcome.Stopped, null, "Zamanlama sahibi firmanın etkin üyesi değil; teslim durduruldu.");
            }

            if (await services.GetRequiredService<IReportExports>().CreateScheduledAsync(claim.ReportId, cancellationToken, claim.Format) is not { } export)
            {
                return (Outcome.Stopped, null, "Rapor silinmiş ya da zamanlama sahibi artık göremiyor; teslim durduruldu.");
            }

            var link = services.GetRequiredService<INotificationRecipients>().Link("raporlar/disa-aktarimlar");
            services.GetRequiredService<IEmailQueue>().Enqueue(Message(recipient, export, link));
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
            var recipientNote = await DeliverToRecipientsAsync(tenantId, claim, recipient.DisplayName, cancellationToken);
            return (Outcome.Delivered, export.Id, $"{export.RowCount} satırlık dosya hazırlandı.{recipientNote}");
        }
        catch (ReportForbiddenException exception)
        {
            return (Outcome.Stopped, null, $"Yetki yok; teslim durduruldu. {exception.Message}");
        }
        catch (ReportException exception)
        {
            return (Outcome.Failed, null, exception.Message);
        }
    }

    /// <summary>Dağıtım listesindeki her alıcıya, <b>alıcının kendi yetkisiyle</b> üretilmiş kendi dosyasını ve e-postasını verir. Özet cümle döner (teslim/atlanan).</summary>
    private async Task<string> DeliverToRecipientsAsync(Guid tenantId, Claim claim, string ownerName, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        List<Guid> ids;
        await using (var lookup = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty)))
        {
            ids = await lookup.ServiceProvider.GetRequiredService<ModulixDbContext>().ReportScheduleRecipients.AsNoTracking()
                .Where(r => r.ScheduleId == claim.ScheduleId).OrderBy(r => r.CreatedAt).Select(r => r.UserId).ToListAsync(cancellationToken);
        }

        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var delivered = 0;
        var skipped = new List<string>();
        foreach (var userId in ids)
        {
            try
            {
                if (await DeliverToRecipientAsync(tenantId, claim, userId, ownerName, cancellationToken) is { } reason)
                {
                    skipped.Add(reason);
                }
                else
                {
                    delivered++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Tenant {TenantId} zamanlama {ScheduleId} alıcı {UserId} için işlenemedi.", tenantId, claim.ScheduleId, userId);
                skipped.Add("hata");
            }
        }

        var summary = $" Alıcılar: {delivered} teslim";
        if (skipped.Count > 0)
        {
            summary += $", {skipped.Count} atlandı ({string.Join(", ", skipped.GroupBy(r => r).Select(g => $"{g.Key}: {g.Count()}"))})";
        }

        return summary + ".";
    }

    /// <summary>Tek alıcı için teslim. Atlanırsa nedeni (kısa metin) döner, teslim edildiyse null. Alıcının yetkisi ya da kapsamı yoksa dosya hiç üretilmez.</summary>
    private async Task<string?> DeliverToRecipientAsync(Guid tenantId, Claim claim, Guid userId, string ownerName, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        Guid membershipId;
        await using (var lookup = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty)))
        {
            var membership = await lookup.ServiceProvider.GetRequiredService<ModulixDbContext>().Memberships.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.UserId == userId).Select(m => new { m.Id, m.Status }).SingleOrDefaultAsync(cancellationToken);
            if (membership is not { Status: MembershipStatus.Active })
            {
                return "üyelik etkin değil";
            }

            membershipId = membership.Id;
        }

        await using var recipientScope = tenantScopes.Create(new TenantContext(tenantId, userId, membershipId));
        var services = recipientScope.ServiceProvider;
        var access = services.GetRequiredService<ICurrentAccess>();
        if (!await access.HasPermissionAsync(CoreModule.Permissions.ReadReports, cancellationToken)
            || !await access.HasPermissionAsync(CoreModule.Permissions.ExportReports, cancellationToken))
        {
            return "yetki yok";
        }

        var recipients = services.GetRequiredService<INotificationRecipients>();
        if (await recipients.FindAsync(userId, cancellationToken) is not { } recipient)
        {
            return "üyelik etkin değil";
        }

        try
        {
            if (await services.GetRequiredService<IReportExports>().CreateScheduledAsync(claim.ReportId, cancellationToken, claim.Format) is not { } export)
            {
                return "raporu göremiyor";
            }

            services.GetRequiredService<IEmailQueue>().Enqueue(Message(recipient, export, recipients.Link("raporlar/disa-aktarimlar"), ownerName));
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (ReportForbiddenException)
        {
            return "yetki yok";
        }
        catch (ReportException)
        {
            return "rapor çalışmadı";
        }
    }

    private async Task FinishAsync(Guid tenantId, Guid scheduleId, Guid runId, Outcome outcome, Guid? exportId, string? message, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var system = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = system.ServiceProvider;
        var db = services.GetRequiredService<ModulixDbContext>();
        var now = time.GetUtcNow();
        var status = outcome.ToString();

        var run = await db.ReportScheduleRuns.SingleAsync(r => r.Id == runId, cancellationToken);
        run.Finish(status, exportId, message, now);

        // Zamanlama bu arada silinmiş ya da kullanıcı tarafından değiştirilmiş olabilir: yalnızca hâlâ aynı planlı andaysa ilerletilir.
        var schedule = await db.ReportSchedules.SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is not null && schedule.NextRunAt == run.Slot)
        {
            if (outcome == Outcome.Stopped)
            {
                schedule.Stop(message ?? "Teslim durduruldu.", now);
            }
            else
            {
                var day = await services.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
                var nextPeriod = ReportScheduleRules.NextRun(schedule.Frequency, schedule.Hour, schedule.DayOfWeek, schedule.DayOfMonth, day.UtcNow, day.Zone);
                if (outcome == Outcome.Failed)
                {
                    // Geçici hata: aynı dönem için kısa gecikmeyle yeniden denenir (en çok ReportSchedule.MaxAttempts deneme), sonra bir sonraki döneme geçilir.
                    schedule.RecordFailure(message, nextPeriod, now);
                }
                else
                {
                    schedule.RecordRun(status, message, nextPeriod, now);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static EmailMessage Message(NotificationRecipient recipient, ReportExportInfo export, string? link, string? scheduledBy = null)
    {
        var html = string.Concat(
            $"<p>Merhaba {WebUtility.HtmlEncode(recipient.DisplayName)},</p>",
            scheduledBy is null
                ? $"<p>Zamanlanmış raporunuz hazır: <strong>{WebUtility.HtmlEncode(export.ReportName)}</strong> ({export.RowCount} satır).</p>"
                : $"<p>{WebUtility.HtmlEncode(scheduledBy)} tarafından zamanlanan rapor sizin yetkinizle hazırlandı: <strong>{WebUtility.HtmlEncode(export.ReportName)}</strong> ({export.RowCount} satır).</p>",
            link is null
                ? "<p>Dosyayı Modülix'te Raporlar &gt; Dışa aktarımlar bölümünden indirebilirsiniz.</p>"
                : $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Dosyayı indir</a></p>",
            $"<p style=\"color:#666\">Dosya {export.ExpiresAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC'ye kadar saklanır. Veri bu e-postada yer almaz; indirirken yetkiniz yeniden denetlenir.</p>");
        return new EmailMessage(recipient.Email, $"Zamanlanmış rapor hazır: {export.ReportName.ReplaceLineEndings(" ")}", html)
        {
            ToName = recipient.DisplayName,
            Reference = $"core.report-export:{export.Id}",
        };
    }

    private static async Task<bool> TryLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        command.Parameters.AddWithValue("@name", LockResource(tenantId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @name, @LockOwner = N'Session';";
            command.Parameters.AddWithValue("@name", LockResource(tenantId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Bağlantı kapanınca oturum kilidi zaten düşer.
        }
    }
}

/// <summary>
/// Zamanlanmış raporları düzenli aralıklarla çalıştırır (<c>ReportJobs:WorkerEnabled</c> ile kapatılır). Etkin her tenant ayrı çalışır; biri başarısız olursa
/// diğerleri etkilenmez. Tenant bağlamı olmadan zamanlama sorgulanamadığı için (Row-Level Security) turda her etkin tenant için hafif bir sorgu atılır.
/// </summary>
public sealed class ReportScheduleWorker(IServiceScopeFactory scopes, ReportScheduleRunner runner, IOptions<ReportJobsOptions> options, ILogger<ReportScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, settings.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var tenantIds = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Tenants.AsNoTracking()
                    .Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(stoppingToken);
                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var (run, _) = await runner.RunTenantAsync(tenantId, stoppingToken);
                        if (run is { } r && (r.Delivered + r.Stopped + r.Failed + r.Purged) > 0)
                        {
                            logger.LogInformation(
                                "Tenant {TenantId} zamanlanmış raporlar: {Delivered} teslim, {Stopped} durduruldu, {Failed} hata, {Purged} süresi geçen dosya silindi.",
                                tenantId, r.Delivered, r.Stopped, r.Failed, r.Purged);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Tenant {TenantId} zamanlanmış raporları bu turda çalıştırılamadı.", tenantId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Zamanlanmış raporlar çalıştırılamadı.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
