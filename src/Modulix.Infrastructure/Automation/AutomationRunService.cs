using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Automation;

/// <summary>
/// <see cref="IAutomationRunService"/>: çalışma geçmişi ve elle müdahale (ADR-0027, Dilim 2). Her işlem aktif tenant'ta çalışır
/// (sorgu süzgeci + RLS). Elle çalıştırma kaydı çağıranın kendi yetkisiyle okur; çalışmanın kendisi sonra kuralın kimliğiyle işler.
/// </summary>
internal sealed class AutomationRunService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAutomationRecordReader records,
    ITenantClock clock,
    ITenantSettings settings,
    IAuditWriter audit,
    TimeProvider time) : IAutomationRunService
{
    public async Task<AutomationRunPage> ListAsync(AutomationRunQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, AutomationRunQuery.MaxPageSize);
        var runs = db.AutomationRuns.AsNoTracking().AsQueryable();
        if (query.RuleId is { } ruleId)
        {
            runs = runs.Where(r => r.RuleId == ruleId);
        }

        if (query.Status is { } status)
        {
            runs = runs.Where(r => r.Status == status);
        }

        if (query.RecordId is { } recordId)
        {
            runs = runs.Where(r => r.RecordId == recordId);
        }

        var total = await runs.CountAsync(cancellationToken);
        // Anahtarlar AutomationRunQuery.SortKeys'tedir; eşit kayıtlar en yeni önce sıralanır.
        var ordered = query.Sort switch
        {
            "createdAt" => runs.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            "status" => runs.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "-status" => runs.OrderByDescending(r => r.Status).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "entityKey" => runs.OrderBy(r => r.EntityKey).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            "-entityKey" => runs.OrderByDescending(r => r.EntityKey).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            _ => runs.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
        };
        var items = query.Sort is "rule" or "-rule"
            ? await PageByRuleAsync(runs, query.Sort == "-rule", page, pageSize, cancellationToken)
            : await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new AutomationRunPage(await SummariesAsync(items, cancellationToken), page, pageSize, total);
    }

    public async Task<AutomationRunDetail?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.AutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var steps = await db.AutomationRunSteps.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.StepNo).ToListAsync(cancellationToken);
        return new AutomationRunDetail(
            (await SummariesAsync([run], cancellationToken)).Single(),
            run.NextAttemptAt,
            [.. steps.Select(s => new AutomationRunStepInfo(
                s.StepNo, s.StepId, s.Action, s.Status, s.Attempts, s.Summary, s.EffectEntityKey, s.EffectRecordId, s.CompletedAt)
                {
                    WaitingUntil = s.Status == AutomationStepStatus.Pending ? s.WaitingUntil : null,
                })]);
    }

    public async Task<AutomationRunDetail?> RunManuallyAsync(Guid ruleId, Guid recordId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rule = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        if (rule.Status != AutomationRuleStatus.Active || rule.PublishedVersionId is not { } versionId)
        {
            throw new AutomationRunException("Kural açık değil; önce yayınlayıp açın.");
        }

        var version = await db.AutomationRuleVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, cancellationToken);
        if (version.Definition.Trigger is not ManualTrigger)
        {
            throw new AutomationRunException("Bu kuralın tetikleyicisi 'elle çalıştırma' değil.");
        }

        if (await settings.GetValueAsync(CoreModule.SettingKeys.AutomationEnabled, cancellationToken) == CoreModule.SettingValues.No)
        {
            throw new AutomationRunException("Otomasyon firma ayarıyla durdurulmuş.");
        }

        // Çağıran, kuralı çalıştıracağı kaydı kendi yetkisiyle görebilmelidir (okuma çağıranın kimliğiyle); kural başkasının kaydına erişim aracı olmaz.
        if (!(await records.ReadAsync(new RecordRef(rule.EntityKey, recordId), cancellationToken)).IsSuccess)
        {
            throw new AutomationRunException("Kayıt bulunamadı ya da erişiminiz yok.");
        }

        // Aynı kural sürümü aynı kayıt için tenant gününde bir kez elle çalışır; ikinci tıklama yeni etki üretmez.
        var today = (await clock.GetAsync(cancellationToken)).Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dedupe = AutomationRunKeys.Dedupe(tenant.TenantId, "manual:" + today, version.Id, rule.EntityKey, recordId);
        if (await db.AutomationRuns.AsNoTracking().Where(r => r.DedupeKey == dedupe).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken) is { } existing)
        {
            throw new AutomationRunDuplicateException(existing);
        }

        var run = AutomationRun.Create(
            tenant.TenantId, rule.Id, version.Id, rule.EntityKey, recordId, AutomationRunOrigin.Manual, null, dedupe,
            Activity.Current?.TraceId.ToString(), 0, tenant.UserId, time.GetUtcNow());
        db.AutomationRuns.Add(run);
        Audit(tenant, rule.Id, $"Elle çalıştırıldı ({run.Id:D})");
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı ikinci tıklama: tekilleştirme indeksi ikinci çalışmayı reddetti. Başka bir hataysa olduğu gibi yükselir.
            db.ChangeTracker.Clear();
            if (await db.AutomationRuns.AsNoTracking().Where(r => r.DedupeKey == dedupe).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken) is { } raced)
            {
                throw new AutomationRunDuplicateException(raced);
            }

            throw;
        }

        return await GetAsync(run.Id, cancellationToken);
    }

    public async Task<AutomationRunDetail?> RetryAsync(Guid runId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var run = await db.AutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        if (run.Status != AutomationRunStatus.Failed)
        {
            throw new AutomationRunException("Yalnızca başarısız çalışma yeniden denenebilir.");
        }

        // Başarısız adımdan sonrakiler yeniden bekleyene döner; başarılı adımlar tekrar çalışmaz. Aynı idempotency anahtarları
        // sürdüğü için etkisi zaten oluşmuş bir adım ikinci etki üretmez. Henüz adım yazılmamış çalışma baştan değerlendirilir.
        var now = time.GetUtcNow();
        var hasSteps = await db.AutomationRunSteps.AnyAsync(s => s.RunId == runId, cancellationToken);
        await db.AutomationRunSteps
            .Where(s => s.RunId == runId && (s.Status == AutomationStepStatus.Failed || s.Status == AutomationStepStatus.Cancelled))
            .ExecuteUpdateAsync(
                set => set.SetProperty(s => s.Status, AutomationStepStatus.Pending).SetProperty(s => s.Attempts, 0).SetProperty(s => s.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(s => s.StartedAt, (DateTimeOffset?)null).SetProperty(s => s.WaitingUntil, (DateTimeOffset?)null),
                cancellationToken);
        var updated = await db.AutomationRuns
            .Where(r => r.Id == runId && r.Status == AutomationRunStatus.Failed)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, hasSteps ? AutomationRunStatus.Running : AutomationRunStatus.Pending)
                    .SetProperty(r => r.NextAttemptAt, now)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(r => r.Note, (string?)null)
                    .SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
        if (updated != 1)
        {
            throw new AutomationRunException("Çalışmanın durumu değişti; yenileyip tekrar deneyin.");
        }

        Audit(tenant, run.RuleId, $"Yeniden denendi ({run.Id:D})");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(runId, cancellationToken);
    }

    public async Task<AutomationRunDetail?> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var run = await db.AutomationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        if (run.IsTerminal)
        {
            throw new AutomationRunException("Çalışma zaten bitti.");
        }

        var now = time.GetUtcNow();
        await db.AutomationRunSteps.Where(s => s.RunId == runId && s.Status == AutomationStepStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AutomationStepStatus.Cancelled).SetProperty(s => s.CompletedAt, now), cancellationToken);

        // Kiradaki bir adım o an sürüyor olabilir; bitişi yazılmaz (kira belirteci silinir), adım sınırında durur.
        var updated = await db.AutomationRuns
            .Where(r => r.Id == runId && (r.Status == AutomationRunStatus.Pending || r.Status == AutomationRunStatus.Running))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, AutomationRunStatus.Cancelled)
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.Note, "Yetkili tarafından iptal edildi.")
                    .SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
        if (updated != 1)
        {
            throw new AutomationRunException("Çalışmanın durumu değişti; yenileyip tekrar deneyin.");
        }

        Audit(tenant, run.RuleId, $"İptal edildi ({run.Id:D})");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Kural adına göre sayfa: satır başına ad sorgusu yapılmaz. Süzülen çalışmalar kurala göre sayılır, kurallar ada göre
    /// sıralanır ve sayfaya düşen kuralların çalışmaları en yeni önce okunur.
    /// </summary>
    private async Task<List<AutomationRun>> PageByRuleAsync(
        IQueryable<AutomationRun> runs, bool descending, int page, int pageSize, CancellationToken cancellationToken)
    {
        var counts = await runs.GroupBy(r => r.RuleId).Select(g => new { RuleId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var ruleIds = counts.Select(c => c.RuleId).ToList();
        var names = await db.AutomationRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var ordered = descending
            ? counts.OrderByDescending(c => names.GetValueOrDefault(c.RuleId, string.Empty), StringComparer.CurrentCulture).ThenBy(c => c.RuleId)
            : counts.OrderBy(c => names.GetValueOrDefault(c.RuleId, string.Empty), StringComparer.CurrentCulture).ThenBy(c => c.RuleId);

        var skip = (page - 1) * pageSize;
        List<AutomationRun> items = [];
        foreach (var rule in ordered)
        {
            if (skip >= rule.Count)
            {
                skip -= rule.Count;
                continue;
            }

            items.AddRange(await runs.Where(r => r.RuleId == rule.RuleId)
                .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
                .Skip(skip).Take(pageSize - items.Count)
                .ToListAsync(cancellationToken));
            skip = 0;
            if (items.Count >= pageSize)
            {
                break;
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<AutomationRunSummary>> SummariesAsync(IReadOnlyList<AutomationRun> runs, CancellationToken cancellationToken)
    {
        var ruleIds = runs.Select(r => r.RuleId).Distinct().ToList();
        var versionIds = runs.Select(r => r.RuleVersionId).Distinct().ToList();
        var names = await db.AutomationRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        var numbers = await db.AutomationRuleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VersionNo, cancellationToken);
        return
        [
            .. runs.Select(r => new AutomationRunSummary(
                r.Id, r.RuleId, names.GetValueOrDefault(r.RuleId, string.Empty), numbers.GetValueOrDefault(r.RuleVersionId), r.EntityKey,
                r.RecordId, r.Origin, r.Status, r.CreatedAt, r.CompletedAt, r.Note)),
        ];
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan otomasyon çalışmaları yönetilemez.");

    // Müdahale kuralın denetim kaydına eklenir ("Çalışma" alanı); çalışmanın kimliği ve eylem yazılır, kayıt değeri yazılmaz.
    private void Audit(TenantContext tenant, Guid ruleId, string text) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.AutomationRule,
            ruleId,
            AuditAction.Updated,
            new JsonObject { ["run"] = new JsonObject { ["old"] = null, ["new"] = text } },
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));
}
