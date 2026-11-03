using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Bir üyeliğin aktif tenant'taki erişimi: yönetici mi, geçerli izinleri ve izinleri veren roller (doğrudan atananlar ile
/// departmandan gelenler, ADR-0026).
/// </summary>
internal sealed record MembershipAccess(bool IsAdministrator, IReadOnlySet<string> Permissions, IReadOnlyList<GrantingRole> Roles)
{
    /// <summary>
    /// Kayıt kapsamları (Y8, ADR-0026): her rolün kapsamı ayrı bir kümedir ve kayıt sahibi kümeleri birleştirilir. Yönetici
    /// her zaman tüm kayıtları görür, rolü olmayan yalnızca kendisininkileri.
    /// </summary>
    public IReadOnlyList<RecordScope> Scopes => MembershipPermissions.ScopesOf(IsAdministrator, Roles.Select(r => r.Scope));

    /// <summary>Gösterim için en geniş kapsam; yetki kararı <see cref="Scopes"/> ile verilir.</summary>
    public RecordScope Scope => RecordScopeRules.Widest(Scopes);

    /// <summary>
    /// Kapsam dışı ortak firma/kişiler gizli mi (ADR-0026 K8)? Yalnızca <b>tüm</b> etkin roller gizliyorsa: roller izinler gibi
    /// eklenir, bir rol asgari kimliği göstermeye izin veriyorsa gösterilir. Yönetici ve rolü olmayan kişi için hayır.
    /// </summary>
    public bool HidesOutOfScopeShared => !IsAdministrator && Roles.Count > 0 && Roles.All(r => r.HideOutOfScopeShared);

    /// <summary>İzni veren roller; sistem rolü her izni verir.</summary>
    public IEnumerable<GrantingRole> RolesGranting(string permission) =>
        Permissions.Contains(permission) ? Roles.Where(r => r.IsSystem || r.Permissions.Contains(permission)) : [];
}

/// <summary>
/// İzin veren rol. <see cref="ViaBusinessUnit"/> doluysa rol kullanıcıya doğrudan değil, o departman üzerinden gelir.
/// </summary>
internal sealed record GrantingRole(
    Guid Id,
    string Name,
    bool IsSystem,
    IReadOnlyList<string> Permissions,
    RecordScope Scope = RecordScope.All,
    string? ViaBusinessUnit = null,
    bool HideOutOfScopeShared = false);

/// <summary>Üyeliğin departmanı ve kimlik yolu.</summary>
internal sealed record MembershipUnit(Guid Id, string Name, string Path);

/// <summary>
/// Üyeliğin rollerinden (doğrudan ve departmandan gelen) ve tenant'ın açık modüllerinden geçerli izinleri hesaplar. Oturumdaki
/// kişinin erişimi (<see cref="CurrentAccess"/>), rol düzenlerken işlemi yapanın sınırı ve kullanıcı detayındaki "Erişim" görünümü
/// aynı hesabı kullanır; ekranda görünen ile sunucunun uyguladığı ayrışmaz.
/// </summary>
internal sealed class MembershipPermissions(ModulixDbContext db, ModuleCatalog catalog)
{
    public async Task<MembershipAccess> GetAsync(Guid membershipId, IReadOnlySet<string> enabledModules, CancellationToken cancellationToken)
    {
        var roles = await LoadRolesAsync(membershipId, cancellationToken);

        // Sistem rolü ("Yönetici") katalogdaki tüm izinleri verir; yeni açılan modülün izinleri de kendiliğinden gelir.
        // Yönetici olmak yalnızca doğrudan atamayla olur: sistem rolü departmana atanamaz, atansa bile yetki vermez.
        var isAdministrator = roles.Any(r => r.IsSystem && r.ViaBusinessUnit is null);
        var effective = roles.Where(r => !r.IsSystem || r.ViaBusinessUnit is null).ToList();
        var granted = isAdministrator ? catalog.PermissionKeys : effective.SelectMany(r => r.Permissions);
        return new MembershipAccess(isAdministrator, catalog.EffectivePermissions(granted, enabledModules), effective);
    }

    /// <summary>Üyeliğin kayıt kapsamları; izinlerden bağımsızdır (modül hakları gerekmez).</summary>
    public async Task<IReadOnlyList<RecordScope>> GetScopesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var roles = await LoadRolesAsync(membershipId, cancellationToken);
        return ScopesOf(roles.Any(r => r.IsSystem && r.ViaBusinessUnit is null), roles.Where(r => !r.IsSystem || r.ViaBusinessUnit is null).Select(r => r.Scope));
    }

    /// <summary>Üyeliğin rolleri kapsam dışı ortak kayıtları gizliyor mu (ADR-0026 K8); bkz. <see cref="MembershipAccess.HidesOutOfScopeShared"/>.</summary>
    public async Task<bool> HidesOutOfScopeSharedAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var roles = await LoadRolesAsync(membershipId, cancellationToken);
        var effective = roles.Where(r => !r.IsSystem || r.ViaBusinessUnit is null).ToList();
        return !effective.Any(r => r.IsSystem) && effective.Count > 0 && effective.All(r => r.HideOutOfScopeShared);
    }

    /// <summary>Üyeliğin en geniş kapsamı (gösterim için); karar <see cref="GetScopesAsync"/> ile verilir.</summary>
    public async Task<RecordScope> GetScopeAsync(Guid membershipId, CancellationToken cancellationToken) =>
        RecordScopeRules.Widest(await GetScopesAsync(membershipId, cancellationToken));

    /// <summary>Üyeliğin departmanı; departmansız üyelik (bozuk veri) için null.</summary>
    public async Task<MembershipUnit?> GetUnitAsync(Guid membershipId, CancellationToken cancellationToken) =>
        await (
            from member in db.BusinessUnitMembers.AsNoTracking()
            join unit in db.BusinessUnits.AsNoTracking() on member.BusinessUnitId equals unit.Id
            where member.MembershipId == membershipId
            select new MembershipUnit(unit.Id, unit.Name, unit.Path))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Departman ve altı kapsamı için: verilen departmanın kendisi ve altındaki departmanların kimlikleri (kimlik yolu
    /// önekiyle). Kök departmanda tüm departmanlardır.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetUnitTreeIdsAsync(string unitPath, CancellationToken cancellationToken)
    {
        // Ağaç küçüktür (tenant başına onlarca departman); yol karşılaştırması bellekte, ham SQL ve LIKE kaçışı olmadan yapılır.
        var units = await db.BusinessUnits.AsNoTracking().Select(u => new { u.Id, u.Path }).ToListAsync(cancellationToken);
        return units.Where(u => u.Path.StartsWith(unitPath, StringComparison.Ordinal)).Select(u => u.Id).ToList();
    }

    /// <summary>
    /// Bir departmanın kullanıcılarına verdiği roller (ADR-0026 K6): o departmana atananlar ile üst departmanlarda "alt
    /// departmanlara da uygula" işaretli olanlar; sistem rolü yetki vermez. Departman taşıma önizlemesinde kaybedilen ve
    /// kazanılan rolleri bulmak için kullanılır.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetUnitRolesAsync(Guid unitId, string unitPath, CancellationToken cancellationToken)
    {
        var assigned = await (
            from assignment in db.BusinessUnitRoles.AsNoTracking()
            join owner in db.BusinessUnits.AsNoTracking() on assignment.BusinessUnitId equals owner.Id
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where !role.IsSystem
            select new { assignment.InheritToChildren, OwnerId = owner.Id, OwnerPath = owner.Path, role.Id, role.Name })
            .ToListAsync(cancellationToken);
        return assigned
            .Where(a => a.OwnerId == unitId || (a.InheritToChildren && unitPath.StartsWith(a.OwnerPath, StringComparison.Ordinal)))
            .Select(a => (a.Id, a.Name))
            .DistinctBy(r => r.Id)
            .OrderBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    internal static IReadOnlyList<RecordScope> ScopesOf(bool isAdministrator, IEnumerable<RecordScope> roleScopes) =>
        isAdministrator ? [RecordScope.All] : roleScopes.Distinct().DefaultIfEmpty(RecordScope.Own).ToList();

    /// <summary>Doğrudan roller ve departmandan gelenler (yinelenen rol önce doğrudan olarak sayılır).</summary>
    private async Task<List<GrantingRole>> LoadRolesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        // Roller ve atamalar tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
        var direct = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where assignment.MembershipId == membershipId
            orderby role.Name
            select new GrantingRole(role.Id, role.Name, role.IsSystem, role.Permissions, role.Scope, null, role.HideOutOfScopeShared))
            .ToListAsync(cancellationToken);

        var unit = await GetUnitAsync(membershipId, cancellationToken);
        if (unit is null)
        {
            return direct;
        }

        // Departman atamaları: kullanıcının kendi departmanındakiler ve üst departmanlarda "alt departmanlara da uygula"
        // işaretli olanlar. Atama sayısı küçüktür; yol karşılaştırması bellekte yapılır.
        var assigned = await (
            from assignment in db.BusinessUnitRoles.AsNoTracking()
            join owner in db.BusinessUnits.AsNoTracking() on assignment.BusinessUnitId equals owner.Id
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            select new { assignment.InheritToChildren, OwnerId = owner.Id, OwnerName = owner.Name, OwnerPath = owner.Path, Role = role })
            .ToListAsync(cancellationToken);

        var directIds = direct.Select(r => r.Id).ToHashSet();
        var viaUnit = assigned
            .Where(a => a.OwnerId == unit.Id || (a.InheritToChildren && unit.Path.StartsWith(a.OwnerPath, StringComparison.Ordinal)))
            .Where(a => !directIds.Contains(a.Role.Id))
            .OrderBy(a => a.Role.Name, StringComparer.CurrentCulture)
            .Select(a => new GrantingRole(a.Role.Id, a.Role.Name, a.Role.IsSystem, a.Role.Permissions, a.Role.Scope, a.OwnerName, a.Role.HideOutOfScopeShared))
            .DistinctBy(r => r.Id);
        return [.. direct, .. viaUnit];
    }
}
