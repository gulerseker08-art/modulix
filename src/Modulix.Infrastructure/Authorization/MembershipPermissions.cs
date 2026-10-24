using Microsoft.EntityFrameworkCore;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>Bir üyeliğin aktif tenant'taki erişimi: yönetici mi ve geçerli izinleri.</summary>
internal sealed record MembershipAccess(bool IsAdministrator, IReadOnlySet<string> Permissions);

/// <summary>
/// Üyeliğin rollerinden ve tenant'ın açık modüllerinden geçerli izinleri hesaplar. Oturumdaki kişinin erişimi
/// (<see cref="CurrentAccess"/>) ve rol düzenlerken işlemi yapanın sınırı aynı hesabı kullanır.
/// </summary>
internal sealed class MembershipPermissions(ModulixDbContext db, ModuleCatalog catalog)
{
    public async Task<MembershipAccess> GetAsync(Guid membershipId, IReadOnlySet<string> enabledModules, CancellationToken cancellationToken)
    {
        // Roller ve atamalar tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
        var roles = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where assignment.MembershipId == membershipId
            select new { role.IsSystem, role.Permissions })
            .ToListAsync(cancellationToken);

        // Sistem rolü ("Yönetici") katalogdaki tüm izinleri verir; yeni açılan modülün izinleri de kendiliğinden gelir.
        var isAdministrator = roles.Any(r => r.IsSystem);
        var granted = isAdministrator ? catalog.PermissionKeys : roles.SelectMany(r => r.Permissions);
        return new MembershipAccess(isAdministrator, catalog.EffectivePermissions(granted, enabledModules));
    }
}
