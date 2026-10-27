using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

internal sealed class TenantMembers(PlatformDbContext db, ITenantContextAccessor tenant) : ITenantMembers
{
    public async Task<IReadOnlyList<TenantMember>> ListActiveAsync(CancellationToken cancellationToken)
    {
        if (tenant.Current is not { } current)
        {
            return [];
        }

        return await (
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.TenantId == current.TenantId && membership.Status == MembershipStatus.Active
            orderby user.DisplayName
            select new TenantMember(user.Id, user.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken) =>
        tenant.Current is { } current
            ? db.Memberships.AsNoTracking().AnyAsync(
                m => m.TenantId == current.TenantId && m.UserId == userId && m.Status == MembershipStatus.Active, cancellationToken)
            : Task.FromResult(false);
}
