using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Users;

/// <summary>
/// Kullanıcı yönetiminin kapsamı (<see cref="IMembershipScope"/>): hedef üyeliğin departmanı, işlemi yapanın görünür
/// departmanları (<see cref="MembershipPermissions.GetVisibleUnitIdsAsync"/>) içinde olmalıdır. Departmanı olmayan üyelik
/// (bozuk veri) yalnızca kısıtsız işlemi yapana görünür.
/// </summary>
internal sealed class MembershipScope(
    ModulixDbContext db, ITenantContextAccessor tenantAccessor, MembershipPermissions permissions) : IMembershipScope
{
    public async Task<bool> CanManageAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        if (tenantAccessor.Current is not { } tenant
            || !await db.Memberships.AnyAsync(m => m.Id == membershipId && m.TenantId == tenant.TenantId, cancellationToken))
        {
            return false;
        }

        if (await permissions.GetVisibleUnitIdsAsync(tenant.MembershipId, cancellationToken) is not { } visible)
        {
            return true;
        }

        return await permissions.GetUnitAsync(membershipId, cancellationToken) is { } unit && visible.Contains(unit.Id);
    }
}
