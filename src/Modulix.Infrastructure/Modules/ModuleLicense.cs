using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Modules;

/// <summary>
/// Lisans süresi ayarları (ADR-0028). <see cref="GraceDays"/>: bitiş tarihinden sonra yazmanın açık kaldığı ek süre; ardından
/// modül salt okunur olur.
/// </summary>
public sealed class LicenseOptions
{
    public const string SectionName = "License";

    public int GraceDays { get; set; } = EntitlementPhases.DefaultGraceDays;

    /// <summary>Geçersiz değer (eksi ya da çok büyük) varsayılana düşmez, sınıra çekilir.</summary>
    public int EffectiveGraceDays => Math.Clamp(GraceDays, 0, EntitlementPhases.MaxGraceDays);
}

internal sealed class ModuleLicense(ModulixDbContext db, ModuleCatalog catalog, TimeProvider time, IOptions<LicenseOptions> options) : IModuleLicense
{
    public async Task<IReadOnlyDictionary<string, ModuleLicenseInfo>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsEnabled)
            .Select(e => new { e.ModuleKey, e.ValidUntil })
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        var graceDays = options.Value.EffectiveGraceDays;
        return rows.ToDictionary(r => r.ModuleKey, r => EntitlementPhases.Describe(r.ModuleKey, r.ValidUntil, now, graceDays), StringComparer.Ordinal);
    }

    public async Task<bool> IsWritableAsync(Guid tenantId, string moduleKey, CancellationToken cancellationToken)
    {
        if (ModuleCatalog.IsAlwaysEnabled(moduleKey))
        {
            return catalog.ContainsModule(moduleKey);
        }

        var validUntil = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ModuleKey == moduleKey && e.IsEnabled)
            .Select(e => new { e.ValidUntil })
            .SingleOrDefaultAsync(cancellationToken);

        return validUntil is not null
            && EntitlementPhases.At(validUntil.ValidUntil, time.GetUtcNow(), options.Value.EffectiveGraceDays) != EntitlementPhase.ReadOnly;
    }
}
