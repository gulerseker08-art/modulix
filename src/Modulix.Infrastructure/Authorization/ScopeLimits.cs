using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>Bir kayıt türünün platform tavanı (ADR-0030).</summary>
internal sealed record ScopeLimit(RecordScope MaxScope, bool AppliesToAdministrators);

/// <summary>
/// Platformun bir tenant için kayıt türü başına koyduğu görünürlük tavanları (ADR-0030 K4). Tavanlar platform tablosundadır
/// (RLS dışı); bu yüzden sorgu <b>her zaman</b> verilen tenant kimliğiyle açıkça süzülür. İstek başına bir kez okunur (scoped);
/// tavan değişikliği bir sonraki istekte geçerli olur. Kaydı olmayan tür için sınır yoktur.
/// </summary>
internal sealed class ScopeLimits(ModulixDbContext db)
{
    private static readonly IReadOnlyDictionary<string, ScopeLimit> None = new Dictionary<string, ScopeLimit>();

    private (Guid TenantId, IReadOnlyDictionary<string, ScopeLimit> Limits)? cached;

    public async Task<IReadOnlyDictionary<string, ScopeLimit>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return None;
        }

        if (cached is { } hit && hit.TenantId == tenantId)
        {
            return hit.Limits;
        }

        var rows = await db.TenantScopeLimits.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => new { l.EntityKey, l.MaxScope, l.AppliesToAdministrators })
            .ToListAsync(cancellationToken);
        var limits = rows.ToDictionary(l => l.EntityKey, l => new ScopeLimit(l.MaxScope, l.AppliesToAdministrators), StringComparer.Ordinal);
        cached = (tenantId, limits);
        return limits;
    }
}
