using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>Bir üyeliğin aktif tenant'taki erişimi: yönetici mi, geçerli izinleri ve izinleri veren rolleri.</summary>
internal sealed record MembershipAccess(bool IsAdministrator, IReadOnlySet<string> Permissions, IReadOnlyList<GrantingRole> Roles)
{
    /// <summary>Kayıt kapsamı (Y8): rollerin en genişi; yönetici her zaman tüm kayıtlar, rolü olmayan yalnızca kendisininkiler.</summary>
    public RecordScope Scope => MembershipPermissions.ScopeOf(IsAdministrator, Roles.Select(r => r.Scope));

    /// <summary>İzni veren roller; sistem rolü her izni verir.</summary>
    public IEnumerable<GrantingRole> RolesGranting(string permission) =>
        Permissions.Contains(permission) ? Roles.Where(r => r.IsSystem || r.Permissions.Contains(permission)) : [];
}

internal sealed record GrantingRole(Guid Id, string Name, bool IsSystem, IReadOnlyList<string> Permissions, RecordScope Scope = RecordScope.All);

/// <summary>
/// Üyeliğin rollerinden ve tenant'ın açık modüllerinden geçerli izinleri hesaplar. Oturumdaki kişinin erişimi
/// (<see cref="CurrentAccess"/>), rol düzenlerken işlemi yapanın sınırı ve kullanıcı detayındaki "Erişim" görünümü aynı
/// hesabı kullanır; ekranda görünen ile sunucunun uyguladığı ayrışmaz.
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
            orderby role.Name
            select new GrantingRole(role.Id, role.Name, role.IsSystem, role.Permissions, role.Scope))
            .ToListAsync(cancellationToken);

        // Sistem rolü ("Yönetici") katalogdaki tüm izinleri verir; yeni açılan modülün izinleri de kendiliğinden gelir.
        var isAdministrator = roles.Any(r => r.IsSystem);
        var granted = isAdministrator ? catalog.PermissionKeys : roles.SelectMany(r => r.Permissions);
        return new MembershipAccess(isAdministrator, catalog.EffectivePermissions(granted, enabledModules), roles);
    }

    /// <summary>Üyeliğin kayıt kapsamı; izinlerden bağımsızdır (modül hakları gerekmez).</summary>
    public async Task<RecordScope> GetScopeAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var roles = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where assignment.MembershipId == membershipId
            select new { role.IsSystem, role.Scope })
            .ToListAsync(cancellationToken);
        return ScopeOf(roles.Any(r => r.IsSystem), roles.Select(r => r.Scope));
    }

    internal static RecordScope ScopeOf(bool isAdministrator, IEnumerable<RecordScope> roleScopes) =>
        isAdministrator ? RecordScope.All : roleScopes.DefaultIfEmpty(RecordScope.Own).Min();
}
