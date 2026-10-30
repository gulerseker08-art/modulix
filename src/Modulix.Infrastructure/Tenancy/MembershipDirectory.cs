using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>
/// ADR-0020: platform verisi (üyelik, tenant) her istekte okunur; tenant verisi (erişilebilir şirketler) yalnızca aktif
/// tenant varken. Tenant DbContext'i bu yüzden tembel çözülür: aktif tenant olmayan akışlarda (ör. giriş) kurulmaz.
/// </summary>
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

    public async Task<CompanyAccess> GetAccessibleCompaniesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        // Tenant verisi: yalnızca aktif tenant varken çağrılır (CompanyContextMiddleware). Tenant DbContext'i burada çözülür.

        // Yönetici (sistem rolü, ADR-0008 kararı) tenant'ın bütün şirketlerine erişir. Roller tenant verisidir: RLS yalnızca
        // aktif tenant'ınkileri döndürür.
        var isAdministrator = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where assignment.MembershipId == membershipId && role.IsSystem
            select assignment.Id)
            .AnyAsync(cancellationToken);

        // Şirketler RLS altındadır: sorgu yalnızca aktif tenant'ın şirketlerini döndürür. Yönetici hepsini; diğerleri yalnızca
        // görevlendirildikleri şirketleri görür. Geçici vekâlet (ADR-0008, adım 5): yalnızca o an geçerli görevler sayılır,
        // böylece süresi dolan ya da henüz başlamamış vekâlet erişilebilir şirketlerden kendiliğinden düşer.
        var now = time.GetUtcNow();
        IQueryable<Company> companies = isAdministrator
            ? db.Companies.AsNoTracking()
            : from company in db.Companies.AsNoTracking()
              join membershipCompany in db.MembershipCompanies.AsNoTracking() on company.Id equals membershipCompany.CompanyId
              where membershipCompany.MembershipId == membershipId
                    && (membershipCompany.ValidFrom == null || membershipCompany.ValidFrom <= now)
                    && (membershipCompany.ValidUntil == null || membershipCompany.ValidUntil > now)
              select company;

        var summaries = await companies
            .OrderBy(c => c.Name)
            .Select(c => new CompanySummary(c.Id, c.Name, c.IsDefault))
            .ToListAsync(cancellationToken);

        return new CompanyAccess(summaries, isAdministrator);
    }
}
