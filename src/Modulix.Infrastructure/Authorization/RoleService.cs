using Microsoft.EntityFrameworkCore;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

internal sealed class RoleService(ModulixDbContext db, ITenantContextAccessor tenantAccessor, ModuleCatalog catalog) : IRoleService
{
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return roles.Select(ToSummary).ToList();
    }

    public async Task<RoleSummary> CreateAsync(string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        RequireTenant();
        var role = new Role(name, permissions);
        EnsureKnownPermissions(role);
        await EnsureUniqueNameAsync(role.Name, exceptRoleId: null, cancellationToken);

        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    public async Task<RoleSummary?> UpdateAsync(Guid roleId, string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        RequireTenant();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return null;
        }

        role.Rename(name);
        role.SetPermissions(permissions);
        EnsureKnownPermissions(role);
        await EnsureUniqueNameAsync(role.Name, role.Id, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    public async Task<bool> SetMembershipRolesAsync(Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return false;
        }

        var wanted = roleIds.ToHashSet();
        var found = await db.Roles.Where(r => wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken);
        var missing = wanted.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Bu tenant'ta bulunmayan rol: {string.Join(", ", missing)}.", nameof(roleIds));
        }

        var current = await db.MembershipRoles.Where(mr => mr.MembershipId == membershipId).ToListAsync(cancellationToken);
        db.MembershipRoles.RemoveRange(current.Where(mr => !wanted.Contains(mr.RoleId)));
        foreach (var roleId in wanted.Except(current.Select(mr => mr.RoleId)))
        {
            db.MembershipRoles.Add(new MembershipRole(membershipId, roleId));
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RoleSummary>?> GetMembershipRolesAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await MembershipBelongsToTenantAsync(membershipId, tenant.TenantId, cancellationToken))
        {
            return null;
        }

        var roles = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where assignment.MembershipId == membershipId
            orderby role.Name
            select role)
            .ToListAsync(cancellationToken);

        return roles.Select(ToSummary).ToList();
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan roller yönetilemez.");

    // Üyelikler platform tablosundadır (RLS dışı); tenant'a ait olduğu burada açıkça denetlenir.
    private Task<bool> MembershipBelongsToTenantAsync(Guid membershipId, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships.AnyAsync(m => m.Id == membershipId && m.TenantId == tenantId, cancellationToken);

    private void EnsureKnownPermissions(Role role)
    {
        var unknown = role.Permissions.Where(p => !catalog.ContainsPermission(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Bilinmeyen izin: {string.Join(", ", unknown)}.", "permissions");
        }
    }

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptRoleId, CancellationToken cancellationToken)
    {
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != exceptRoleId, cancellationToken))
        {
            throw new ArgumentException($"Bu adda bir rol zaten var: '{name}'.", nameof(name));
        }
    }

    private static RoleSummary ToSummary(Role role) => new(role.Id, role.Name, role.Permissions);
}
