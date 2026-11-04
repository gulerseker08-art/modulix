using Microsoft.EntityFrameworkCore;
using Modulix.Core.Automation;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Automation;

/// <summary>
/// <see cref="IAutomationRunStore"/>: aktif tenant'ın kuralları ve çalışmaları. Sorgular tenant süzgeci ve Row-Level Security ile sınırlıdır;
/// ekleme, aboneyi çalıştıran işlemle (olay teslimiyle birlikte) yazılır.
/// </summary>
internal sealed class AutomationRunStore(ModulixDbContext db) : IAutomationRunStore
{
    public async Task<IReadOnlyList<ActiveAutomationRule>> ActiveRulesAsync(string entityKey, CancellationToken cancellationToken)
    {
        var rows = await (
            from rule in db.AutomationRules.AsNoTracking()
            join version in db.AutomationRuleVersions.AsNoTracking() on rule.PublishedVersionId equals (Guid?)version.Id
            where rule.EntityKey == entityKey && rule.Status == AutomationRuleStatus.Active
            orderby version.Priority, rule.Id
            select new { rule, version })
            .Take(AutomationLimits.MaxRulesPerEvent)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ActiveAutomationRule(r.rule, r.version))];
    }

    public Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken) =>
        db.AutomationRuns.AsNoTracking().AnyAsync(r => r.DedupeKey == dedupeKey, cancellationToken);

    public Task<bool> ChainExistsAsync(Guid ruleId, Guid recordId, string correlationId, CancellationToken cancellationToken)
    {
        // Çalışma kaydı izi kırpılmış tutar; karşılaştırma da aynı uzunlukla yapılır.
        var trace = correlationId.Length > AutomationRun.CorrelationMaxLength ? correlationId[..AutomationRun.CorrelationMaxLength] : correlationId;
        return db.AutomationRuns.AsNoTracking()
            .AnyAsync(r => r.RuleId == ruleId && r.RecordId == recordId && r.CorrelationId == trace, cancellationToken);
    }

    public void Add(AutomationRun run) => db.AutomationRuns.Add(run);
}
