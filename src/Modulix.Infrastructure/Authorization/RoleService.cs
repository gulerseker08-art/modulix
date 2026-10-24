using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

internal sealed class RoleService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    MembershipPermissions membershipPermissions,
    AdministratorGuard administrators,
    IAuditWriter audit) : IRoleService
{
    /// <summary>Denetim kaydındaki kayıt türleri.</summary>
    public const string RoleEntityKey = "core.role";

    public const string MembershipEntityKey = "core.membership";

    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var counts = await db.MembershipRoles.AsNoTracking()
            .GroupBy(mr => mr.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RoleId, g => g.Count, cancellationToken);
        return roles.Select(r => ToSummary(r) with { MemberCount = counts.GetValueOrDefault(r.Id) }).ToList();
    }

    public async Task<RoleSummary> CreateAsync(
        string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken, Guid? actingMembershipId = null)
    {
        var tenant = RequireTenant();
        var role = new Role(name, permissions);
        EnsureKnownPermissions(role);
        await EnsureUniqueNameAsync(role.Name, exceptRoleId: null, cancellationToken);
        await EnsureActorMayChangeAsync(actingMembershipId, role.Permissions, cancellationToken);

        db.Roles.Add(role);
        AuditRole(tenant, role.Id, AuditAction.Created, before: null, after: Snapshot(role));
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    public async Task<RoleSummary?> UpdateAsync(
        Guid roleId, string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken, Guid? actingMembershipId = null)
    {
        var tenant = RequireTenant();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return null;
        }

        var before = Snapshot(role);
        var previous = role.Permissions;
        role.Rename(name);
        role.SetPermissions(permissions);
        EnsureKnownPermissions(role);
        await EnsureUniqueNameAsync(role.Name, role.Id, cancellationToken);

        // Eklenen ve çıkarılan izinler; değişmeyenler (ör. kapalı modülün izni) denetlenmez.
        var changed = previous.Except(role.Permissions).Concat(role.Permissions.Except(previous)).ToList();
        await EnsureActorMayChangeAsync(actingMembershipId, changed, cancellationToken);

        var after = Snapshot(role);
        if (!JsonNode.DeepEquals(before, after))
        {
            AuditRole(tenant, role.Id, AuditAction.Updated, before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    public async Task<bool> DeleteAsync(
        Guid roleId, Guid? moveToRoleId, CancellationToken cancellationToken, Guid? actingMembershipId = null)
    {
        var tenant = RequireTenant();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await administrators.LockAsync(cancellationToken);

        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return false;
        }

        if (role.IsSystem)
        {
            throw new RoleConflictException($"'{role.Name}' sistem rolüdür; silinemez.");
        }

        // Rolü silmek, izinlerini üyelerinden kaldırmaktır.
        await EnsureActorMayChangeAsync(actingMembershipId, role.Permissions, cancellationToken);

        var assignments = await db.MembershipRoles.Where(mr => mr.RoleId == roleId).ToListAsync(cancellationToken);
        if (assignments.Count > 0)
        {
            var target = await LoadMoveTargetAsync(role, moveToRoleId, assignments.Count, actingMembershipId, cancellationToken);
            await MoveMembersAsync(tenant, role, target, assignments, cancellationToken);
        }

        db.Roles.Remove(role);
        AuditRole(tenant, role.Id, AuditAction.Deleted, Snapshot(role), after: null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<RoleSummary> EnsureSystemRoleAsync(CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var existing = await db.Roles.SingleOrDefaultAsync(r => r.IsSystem, cancellationToken);
        if (existing is not null)
        {
            return ToSummary(existing);
        }

        var sameName = await db.Roles.SingleOrDefaultAsync(r => r.Name == Role.SystemRoleName, cancellationToken);
        if (sameName is not null)
        {
            var before = Snapshot(sameName);
            sameName.MarkAsSystem();
            AuditRole(tenant, sameName.Id, AuditAction.Updated, before, Snapshot(sameName));
            await db.SaveChangesAsync(cancellationToken);
            return ToSummary(sameName);
        }

        var role = Role.CreateSystem();
        db.Roles.Add(role);
        AuditRole(tenant, role.Id, AuditAction.Created, before: null, after: Snapshot(role));
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(role);
    }

    public async Task<bool> SetMembershipRolesAsync(
        Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken, Guid? actingMembershipId = null)
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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var systemRoleId = await administrators.LockAsync(cancellationToken);
        var current = await db.MembershipRoles.Where(mr => mr.MembershipId == membershipId).ToListAsync(cancellationToken);
        var currentIds = current.Select(mr => mr.RoleId).ToHashSet();

        if (actingMembershipId is { } actor
            && systemRoleId is { } system
            && (currentIds.Contains(system) || wanted.Contains(system)))
        {
            await administrators.EnsureIsAdministratorAsync(system, actor, cancellationToken);
        }

        if (systemRoleId is { } systemId && currentIds.Contains(systemId) && !wanted.Contains(systemId))
        {
            await administrators.EnsureAnotherActiveAdministratorAsync(systemId, membershipId, cancellationToken);
        }

        if (currentIds.SetEquals(wanted))
        {
            return true;
        }

        db.MembershipRoles.RemoveRange(current.Where(mr => !wanted.Contains(mr.RoleId)));
        foreach (var roleId in wanted.Except(currentIds))
        {
            db.MembershipRoles.Add(new MembershipRole(membershipId, roleId));
        }

        var names = await db.Roles.AsNoTracking()
            .Where(r => currentIds.Contains(r.Id) || wanted.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        Audit(
            tenant,
            MembershipEntityKey,
            membershipId,
            AuditAction.Updated,
            new JsonObject { ["roles"] = RoleNames(currentIds, names) },
            new JsonObject { ["roles"] = RoleNames(wanted, names) });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    private async Task<Role> LoadMoveTargetAsync(
        Role role, Guid? moveToRoleId, int memberCount, Guid? actingMembershipId, CancellationToken cancellationToken)
    {
        if (moveToRoleId is not { } targetId)
        {
            throw new RoleConflictException(
                $"'{role.Name}' rolünde {memberCount} kişi var. Silmeden önce üyelerin taşınacağı rolü seçin.");
        }

        if (targetId == role.Id)
        {
            throw new ArgumentException("Üyeler silinen rolün kendisine taşınamaz.", "moveTo");
        }

        var target = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == targetId, cancellationToken)
            ?? throw new ArgumentException("Üyelerin taşınacağı rol bulunamadı.", "moveTo");

        if (actingMembershipId is { } actor)
        {
            var access = await GetActorAccessAsync(actor, cancellationToken);
            if (!access.Permissions.Contains(CoreModule.Permissions.ManageUsers))
            {
                throw new UserAdministrationException(
                    UserAdministrationError.PermissionDenied,
                    "Üyesi olan rolü silmek kullanıcıların rolünü değiştirir; bunun için kullanıcı yönetme izni de gerekir.");
            }

            if (target.IsSystem)
            {
                await administrators.EnsureIsAdministratorAsync(target.Id, actor, cancellationToken);
            }
        }

        return target;
    }

    /// <summary>Silinen rolün üyelerine hedef rolü verir; her üyeliğin rol değişikliği denetim kaydına yazılır.</summary>
    private async Task MoveMembersAsync(
        TenantContext tenant, Role role, Role target, IReadOnlyList<MembershipRole> assignments, CancellationToken cancellationToken)
    {
        var membershipIds = assignments.Select(a => a.MembershipId).ToHashSet();
        var current = await db.MembershipRoles.AsNoTracking()
            .Where(mr => membershipIds.Contains(mr.MembershipId))
            .ToListAsync(cancellationToken);
        var roleIds = current.Select(mr => mr.RoleId).Append(target.Id).Distinct().ToList();
        var names = await db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);

        db.MembershipRoles.RemoveRange(assignments);
        foreach (var membershipId in membershipIds)
        {
            var before = current.Where(mr => mr.MembershipId == membershipId).Select(mr => mr.RoleId).ToHashSet();
            var after = before.Where(id => id != role.Id).Append(target.Id).ToHashSet();
            if (!before.Contains(target.Id))
            {
                db.MembershipRoles.Add(new MembershipRole(membershipId, target.Id));
            }

            Audit(
                tenant,
                MembershipEntityKey,
                membershipId,
                AuditAction.Updated,
                new JsonObject { ["roles"] = RoleNames(before, names) },
                new JsonObject { ["roles"] = RoleNames(after, names) });
        }
    }

    /// <summary>
    /// Yetki yükseltmeye karşı: yönetici olmayan kişi yalnızca kendisinde olan izni verebilir veya kaldırabilir.
    /// İşlemi yapan verilmemişse (sistem işlemi) denetim yapılmaz.
    /// </summary>
    private async Task EnsureActorMayChangeAsync(
        Guid? actingMembershipId, IReadOnlyCollection<string> changedPermissions, CancellationToken cancellationToken)
    {
        if (actingMembershipId is not { } actor || changedPermissions.Count == 0)
        {
            return;
        }

        var access = await GetActorAccessAsync(actor, cancellationToken);
        if (access.IsAdministrator)
        {
            return;
        }

        var denied = changedPermissions.Where(p => !access.Permissions.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (denied.Count > 0)
        {
            var names = denied.Select(p => catalog.FindPermission(p)?.DisplayName ?? p);
            throw new UserAdministrationException(
                UserAdministrationError.PermissionDenied,
                $"Kendinizde olmayan izinleri veremez veya kaldıramazsınız: {string.Join(", ", names)}.");
        }
    }

    private async Task<MembershipAccess> GetActorAccessAsync(Guid actingMembershipId, CancellationToken cancellationToken) =>
        await membershipPermissions.GetAsync(
            actingMembershipId, await entitlements.GetEnabledAsync(RequireTenant().TenantId, cancellationToken), cancellationToken);

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

    private void AuditRole(TenantContext tenant, Guid roleId, AuditAction action, JsonObject? before, JsonObject? after) =>
        Audit(tenant, RoleEntityKey, roleId, action, before, after);

    private void Audit(TenantContext tenant, string entityKey, Guid recordId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            entityKey,
            recordId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(Role role) => new()
    {
        ["name"] = role.Name,
        ["isSystem"] = role.IsSystem,
        ["permissions"] = new JsonArray([.. role.Permissions.Select(p => JsonValue.Create(p))]),
    };

    private static JsonArray RoleNames(IEnumerable<Guid> roleIds, IReadOnlyDictionary<Guid, string> names) =>
        new([.. roleIds.Select(id => names.GetValueOrDefault(id, id.ToString())).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n))]);

    private static RoleSummary ToSummary(Role role) => new(role.Id, role.Name, role.Permissions, role.IsSystem);
}
