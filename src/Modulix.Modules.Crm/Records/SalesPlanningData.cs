using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Records;

/// <summary>Fırsatın tahmin karşılaştırmasında gereken güncel durumu.</summary>
internal sealed record OpportunityState(Guid Id, string Status, DateOnly? ExpectedCloseDate);

/// <summary>
/// Satış raporlama ve tahmin servislerinin veri erişim katmanı (mimari kural: <see cref="IModuleData"/> yalnızca <c>Records</c> klasöründe). Okuma yardımcıları izlemesizdir;
/// <b>yazma</b> yardımcıları (<c>Tracked*</c>, <c>Add</c>, <c>Remove</c>) yalnızca <see cref="ForecastWorkflowGate"/> etkisi içinde, kayıt boru hattının aynı işleminde çağrılır
/// ve kaydetmez: kayıt, denetim ve olay boru hattı tarafından birlikte yazılır.
/// </summary>
internal sealed class SalesPlanningData(IModuleData data)
{
    // ---- Okuma

    public async Task<List<RateEntry>> RatesAsync(CancellationToken cancellationToken) =>
        await data.Set<ExchangeRate>().AsNoTracking().Select(r => new RateEntry(r.FromCurrency, r.ToCurrency, r.Rate, r.ValidOn)).ToListAsync(cancellationToken);

    public async Task<List<SalesTarget>> TargetsOverlappingAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken) =>
        await data.Set<SalesTarget>().AsNoTracking().Where(t => t.PeriodEnd >= start && t.PeriodStart <= end).ToListAsync(cancellationToken);

    /// <summary>Fırsatların <b>bugünkü</b> sorumluları (silinmiş fırsat sözlükte yoktur); kayıtlı tahmin satırlarının bugünkü erişimle süzülmesi içindir.</summary>
    public async Task<Dictionary<Guid, Guid?>> CurrentOwnersAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await data.Set<Opportunity>().AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OwnerId, cancellationToken);

    public async Task<Dictionary<Guid, OpportunityState>> OpportunityStatesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await data.Set<Opportunity>().AsNoTracking().Where(o => ids.Contains(o.Id))
                .Select(o => new OpportunityState(o.Id, o.Status, o.ExpectedCloseDate)).ToDictionaryAsync(o => o.Id, cancellationToken);

    public async Task<bool> SlotExistsAsync(
        DateOnly start, DateOnly end, string scopeType, Guid? scopeUserId, string currency, CancellationToken cancellationToken) =>
        await data.Set<ForecastSubmission>().AsNoTracking().AnyAsync(s => s.PeriodStart == start && s.PeriodEnd == end
            && s.ScopeType == scopeType && s.ScopeUserId == scopeUserId && s.ReportingCurrency == currency, cancellationToken);

    /// <summary>Aynı dönem, kapsam ve para birimindeki tüm sürümlerin durumu ve numarası.</summary>
    public async Task<List<(string Status, int VersionNo)>> VersionsAsync(
        DateOnly start, DateOnly end, string scopeType, Guid? scopeUserId, string currency, CancellationToken cancellationToken)
    {
        var rows = await data.Set<ForecastSubmission>().AsNoTracking().Where(s => s.PeriodStart == start && s.PeriodEnd == end
            && s.ScopeType == scopeType && s.ScopeUserId == scopeUserId && s.ReportingCurrency == currency)
            .Select(s => new { s.Status, s.VersionNo }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.Status, r.VersionNo))];
    }

    public async Task<List<ForecastItem>> ItemsAsync(Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastItem>().AsNoTracking().Where(i => i.SubmissionId == submissionId).ToListAsync(cancellationToken);

    public async Task<ForecastItem?> FindItemAsync(Guid itemId, Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastItem>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.SubmissionId == submissionId, cancellationToken);

    public async Task<int> ItemCountAsync(Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastItem>().AsNoTracking().CountAsync(i => i.SubmissionId == submissionId, cancellationToken);

    public async Task<List<ForecastAdjustment>> AdjustmentsAsync(Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastAdjustment>().AsNoTracking().Where(a => a.SubmissionId == submissionId).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);

    // ---- Yazma (yalnızca kapı etkisi içinde)

    public async Task<List<ForecastItem>> TrackedItemsAsync(Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastItem>().Where(i => i.SubmissionId == submissionId).ToListAsync(cancellationToken);

    public async Task<ForecastItem> TrackedItemAsync(Guid itemId, Guid submissionId, CancellationToken cancellationToken) =>
        await data.Set<ForecastItem>().SingleAsync(i => i.Id == itemId && i.SubmissionId == submissionId, cancellationToken);

    public void Add(ForecastItem item) => data.Set<ForecastItem>().Add(item);

    public void Add(ForecastAdjustment adjustment) => data.Set<ForecastAdjustment>().Add(adjustment);

    public void Remove(IEnumerable<ForecastItem> items) => data.Set<ForecastItem>().RemoveRange(items);
}
