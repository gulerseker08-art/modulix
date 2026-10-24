using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

internal sealed class MembershipDirectory(ModulixDbContext db, TimeProvider time) : IMembershipDirectory
{
    public async Task<IReadOnlyList<MembershipSummary>> GetActiveForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await (
            from membership in db.Memberships.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.UserId == userId
                  && membership.Status == MembershipStatus.Active
                  && tenant.Status == TenantStatus.Active
            orderby tenant.Name
            select new MembershipSummary(membership.Id, tenant.Id, tenant.Name, tenant.Slug))
            .ToListAsync(cancellationToken);

    public Task<MembershipSummary?> FindActiveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        (
            from membership in db.Memberships.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.UserId == userId
                  && membership.TenantId == tenantId
                  && membership.Status == MembershipStatus.Active
                  && tenant.Status == TenantStatus.Active
            select new MembershipSummary(membership.Id, tenant.Id, tenant.Name, tenant.Slug))
            .SingleOrDefaultAsync(cancellationToken);

    public Task RecordSignInAsync(Guid membershipId, CancellationToken cancellationToken) =>
        db.Memberships
            .Where(m => m.Id == membershipId)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.LastSignInAt, time.GetUtcNow()), cancellationToken);
}
