using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>
/// <see cref="IBusinessUnitDirectory"/> uygulaması (ADR-0042 bölüm 22): departman ağacını ve üyelerin departmanını salt okunur verir. Sorgular tenant
/// süzgeci ve Row-Level Security altındadır; aktif tenant yoksa sonuç boştur. Ağaç istek kapsamında bir kez okunur.
/// </summary>
internal sealed class BusinessUnitDirectory(ModulixDbContext db, ITenantContextAccessor tenant) : IBusinessUnitDirectory
{
    private const string PathSeparator = " › ";

    private IReadOnlyList<BusinessUnitInfo>? units;

    public async Task<IReadOnlyList<BusinessUnitInfo>> ListAsync(CancellationToken cancellationToken)
    {
        if (units is not null)
        {
            return units;
        }

        if (tenant.Current is null)
        {
            return [];
        }

        var rows = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var names = rows.ToDictionary(u => u.Id, u => u.Name);
        units = [.. rows
            .Select(u =>
            {
                Guid[] path = [.. u.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(segment => Guid.TryParseExact(segment, "N", out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)];
                return new BusinessUnitInfo(u.Id, u.ParentId, u.Name,
                    string.Join(PathSeparator, path.Select(id => names.GetValueOrDefault(id, u.Name))), path, u.Depth, u.IsActive);
            })
            .OrderBy(u => u.Depth).ThenBy(u => u.Name, StringComparer.CurrentCulture)];
        return units;
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> UnitsOfUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (tenant.Current is not { } current || userIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        Guid[] wanted = [.. userIds.Distinct()];
        return await (
                from member in db.BusinessUnitMembers.AsNoTracking()
                join membership in db.Memberships.AsNoTracking() on member.MembershipId equals membership.Id
                where membership.TenantId == current.TenantId && wanted.Contains(membership.UserId)
                select new { membership.UserId, member.BusinessUnitId })
            .ToDictionaryAsync(x => x.UserId, x => x.BusinessUnitId, cancellationToken);
    }
}
