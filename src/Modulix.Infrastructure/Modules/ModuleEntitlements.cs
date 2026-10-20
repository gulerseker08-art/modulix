using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Modules;

internal sealed class ModuleEntitlements(ModulixDbContext db, ModuleCatalog catalog) : IModuleEntitlements
{
    public async Task<IReadOnlySet<string>> GetEnabledAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var keys = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IsEnabled)
            .Select(e => e.ModuleKey)
            .ToListAsync(cancellationToken);

        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task SetEnabledAsync(Guid tenantId, string moduleKey, bool enabled, CancellationToken cancellationToken)
    {
        if (!catalog.ContainsModule(moduleKey))
        {
            throw new ArgumentException($"Bilinmeyen modül: '{moduleKey}'.", nameof(moduleKey));
        }

        if (ModuleCatalog.IsAlwaysEnabled(moduleKey))
        {
            throw new ArgumentException($"'{moduleKey}' modülü her zaman açıktır; değiştirilemez.", nameof(moduleKey));
        }

        var entitlement = await db.ModuleEntitlements
            .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.ModuleKey == moduleKey, cancellationToken);

        if (entitlement is null)
        {
            if (!enabled)
            {
                return;
            }

            db.ModuleEntitlements.Add(new ModuleEntitlement(tenantId, moduleKey));
        }
        else if (enabled)
        {
            entitlement.Enable();
        }
        else
        {
            entitlement.Disable();
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
