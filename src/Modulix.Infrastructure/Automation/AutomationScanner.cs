using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Automation;

/// <summary>
/// Tarama tabanlı tetikleyicileri (<see cref="ScheduledTrigger"/>, <see cref="DateRelativeTrigger"/>, <see cref="InactivityTrigger"/>) kural
/// çalışmasına çevirir (ADR-0027, Dilim 2b). <see cref="AutomationRunner"/> tenant kilidi altında her turda bunu çağırır; bu yüzden aynı tenant
/// için iki tarama aynı anda koşmaz ve acil durdurma anahtarları taramayı da durdurur.
/// <list type="bullet">
///   <item>Tarama kayda bakmaz: kayıt türünü beyan eden modülün <see cref="IAutomationRecordSource"/> kaynağından yalnızca kayıt kimlikleri alınır;
///   koşul ve aksiyon çalışma işlenirken kuralın kimliğiyle ve alan yetkisiyle değerlendirilir.</item>
///   <item>Her (kural sürümü, tenant günü) için bir imleç tutulur (<see cref="AutomationScanCursor"/>); tarama sayfa sayfa ve birden çok turda ilerler,
///   kalınan yerden sürer. Tekilleştirme anahtarı gün parçası taşır: aynı gün ikinci tarama aynı kayıtta ikinci çalışma açmaz, ertesi gün meşru olarak açabilir.</item>
///   <item>Koruma: tur başına en çok <see cref="AutomationOptions.ScanRecordsPerTenantPerRound"/> kayıt okunur; bekleyen çalışma sayısı
///   <see cref="AutomationOptions.ScanBacklogLimit"/>'e ulaşmışsa yeni kayıt alınmaz (işleyici yetişene dek). Kapalı modülün ve etkinlik tarihi dışındaki
///   kuralların taraması yapılmaz. Bozuk bir kayıt kaynağı yalnızca kendi kuralını o turda atlatır, turu bozmaz.</item>
/// </list>
/// Kaçırılan günler geriye dönük taranmaz: işçi bir gün çalışmadıysa o günün taraması yapılmamış olur.
/// </summary>
public sealed class AutomationScanner(
    ModuleCatalog catalog, IOptions<AutomationOptions> options, TimeProvider time, ILogger<AutomationScanner> logger)
{
    /// <summary>Tenant için taramayı bir tur ilerletir; kuyruğa alınan çalışma sayısını döndürür. <paramref name="services"/> tenant kapsamındadır.</summary>
    public async Task<int> ScanAsync(IServiceProvider services, ModulixDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var pending = await db.AutomationRuns.AsNoTracking().CountAsync(r => r.Status == AutomationRunStatus.Pending, cancellationToken);
        var budget = Math.Min(Math.Max(1, settings.ScanRecordsPerTenantPerRound), settings.ScanBacklogLimit - pending);
        if (budget <= 0)
        {
            return 0;
        }

        var candidates = await (
            from rule in db.AutomationRules.AsNoTracking()
            join version in db.AutomationRuleVersions.AsNoTracking() on rule.PublishedVersionId equals (Guid?)version.Id
            where rule.Status == AutomationRuleStatus.Active
            orderby version.Priority, rule.Id
            select new ActiveAutomationRule(rule, version))
            .ToListAsync(cancellationToken);
        candidates = [.. candidates.Where(c => AutomationTriggerMatcher.IsScan(c.Version.Definition.Trigger))];
        if (candidates.Count == 0)
        {
            return 0;
        }

        var day = await services.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
        var period = AutomationScanCursor.PeriodOf(day.Today);
        var enabledModules = await services.GetRequiredService<IModuleEntitlements>().GetEnabledAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        var enqueued = 0;
        foreach (var (rule, version) in candidates)
        {
            if (budget <= 0)
            {
                break;
            }

            var trigger = version.Definition.Trigger;
            if ((version.ActiveFrom is { } from && now < from) || (version.ActiveUntil is { } until && now > until)
                || !AutomationScanPlan.IsDue(trigger, day)
                || (rule.ModuleKey != CoreModule.ModuleKey && !enabledModules.Contains(rule.ModuleKey))
                || catalog.FindAutomationEntity(rule.EntityKey)?.Entity.RecordSource is not { } sourceType)
            {
                continue;
            }

            var cursor = await db.AutomationScanCursors
                .SingleOrDefaultAsync(c => c.RuleVersionId == version.Id && c.PeriodKey == period, cancellationToken);
            if (cursor is { Completed: true })
            {
                continue;
            }

            try
            {
                var taken = await ScanRuleAsync(services, db, tenantId, rule, version, sourceType, cursor, period, day, now, budget, cancellationToken);
                enqueued += taken;
                budget -= taken;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Kaynağın hatası kayıt değeri taşıyabilir; yalnızca tür yazılır. İmleç ilerlemediği için kural sonraki turda kaldığı yerden dener.
                db.ChangeTracker.Clear();
                logger.LogError(
                    "Tenant {TenantId} kural {RuleId} taraması başarısız oldu ({ErrorType}).", tenantId, rule.Id, exception.GetType().Name);
            }
        }

        return enqueued;
    }

    private async Task<int> ScanRuleAsync(
        IServiceProvider services, ModulixDbContext db, Guid tenantId, AutomationRule rule, AutomationRuleVersion version, Type sourceType,
        AutomationScanCursor? cursor, string period, TenantDay day, DateTimeOffset now, int budget, CancellationToken cancellationToken)
    {
        var source = (IAutomationRecordSource)services.GetRequiredService(sourceType);
        var trigger = version.Definition.Trigger;
        var enqueued = 0;
        var isNew = cursor is null;
        cursor ??= AutomationScanCursor.Start(tenantId, rule.Id, version.Id, period, now);
        var dedupeTrigger = AutomationScanCursor.DedupeTrigger(period);
        while (budget > 0 && !cursor.Completed)
        {
            var size = Math.Min(Math.Max(1, options.Value.ScanPageSize), budget);
            var query = AutomationScanPlan.QueryFor(trigger, rule.EntityKey, day, cursor.AfterRecordId, size)!;
            var page = await source.ScanAsync(query, cancellationToken);

            // Kaynağın sırasına güvenilmez: yalnızca imleçten büyük, tekil kimlikler artan sırada alınır (kaynak hatası sonsuz döngü kurmaz).
            var ids = page.RecordIds
                .Where(id => id != Guid.Empty && (cursor.AfterRecordId is not { } after || id.CompareTo(after) > 0))
                .Distinct().Order().Take(size).ToList();
            var keys = ids.ToDictionary(id => id, id => AutomationRunKeys.Dedupe(tenantId, dedupeTrigger, version.Id, rule.EntityKey, id));
            var keyList = keys.Values.ToList();
            var existing = keyList.Count == 0
                ? []
                : (await db.AutomationRuns.AsNoTracking()
                    .Where(r => keyList.Contains(r.DedupeKey)).Select(r => r.DedupeKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var id in ids)
            {
                if (existing.Contains(keys[id]))
                {
                    continue;
                }

                db.AutomationRuns.Add(AutomationRun.Create(
                    tenantId, rule.Id, version.Id, rule.EntityKey, id, AutomationRunOrigin.Scan, null, keys[id],
                    Guid.NewGuid().ToString("N"), 0, null, now));
                added++;
            }

            // Sayfa, imleç ve çalışmalar birlikte yazılır: yarım sayfa kalıcı olmaz, yeniden denemede tekilleştirme zaten çift açılmasını önler.
            cursor.Advance(ids.Count > 0 ? ids[^1] : null, added, page.RecordIds.Count < size, now);
            if (isNew)
            {
                db.AutomationScanCursors.Add(cursor);
                isNew = false;
            }

            await db.SaveChangesAsync(cancellationToken);
            enqueued += added;
            budget -= Math.Max(ids.Count, 1);
        }

        return enqueued;
    }
}
