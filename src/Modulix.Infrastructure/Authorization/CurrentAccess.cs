using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Aktif üyeliğin rollerinden ve tenant'ın modül haklarından geçerli izinleri hesaplar.
/// İstek başına bir kez okur (scoped); değişiklik bir sonraki istekte geçerli olur.
/// </summary>
internal sealed class CurrentAccess(
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ModulixDbContext db) : ICurrentAccess
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private IReadOnlySet<string>? enabledModules;
    private IReadOnlySet<string>? permissions;

    public async Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moduleKey);
        if (tenantAccessor.Current is null)
        {
            return false;
        }

        return catalog.IsEnabled(moduleKey, await GetEnabledModulesAsync(cancellationToken));
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return (await GetPermissionsAsync(cancellationToken)).Contains(permission);
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        if (tenantAccessor.Current is not { } tenant)
        {
            return None;
        }

        if (permissions is null)
        {
            // Roller ve atamalar tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
            var granted = await (
                from assignment in db.MembershipRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                where assignment.MembershipId == tenant.MembershipId
                select role.Permissions)
                .ToListAsync(cancellationToken);

            permissions = catalog.EffectivePermissions(granted.SelectMany(p => p), await GetEnabledModulesAsync(cancellationToken));
        }

        return permissions;
    }

    private async Task<IReadOnlySet<string>> GetEnabledModulesAsync(CancellationToken cancellationToken) =>
        enabledModules ??= await entitlements.GetEnabledAsync(tenantAccessor.Current!.TenantId, cancellationToken);
}
