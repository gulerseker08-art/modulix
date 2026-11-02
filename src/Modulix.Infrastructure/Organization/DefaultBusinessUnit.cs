using Microsoft.EntityFrameworkCore;
using Modulix.Core.Organization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>
/// Her tenant'ın tek kök departmanı (merkez) vardır ve her üyelik bir departmandadır (ADR-0026 K4). Kök departman tenant
/// adıyla açılır; departmanı verilmeyen üyelik köke bağlanır.
/// </summary>
public static class DefaultBusinessUnit
{
    /// <summary>
    /// Aktif tenant'ın kök departmanını döner; yoksa ekler (kaydetmez). Tekrar çağırmak güvenlidir.
    /// </summary>
    public static async Task<BusinessUnit> EnsureRootAsync(ModulixDbContext db, string tenantName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var root = db.BusinessUnits.Local.FirstOrDefault(u => u.IsRoot)
            ?? await db.BusinessUnits.FirstOrDefaultAsync(u => u.ParentId == null, cancellationToken);
        if (root is not null)
        {
            return root;
        }

        var name = tenantName.Trim();
        root = BusinessUnit.CreateRoot(name.Length > BusinessUnit.NameMaxLength ? name[..BusinessUnit.NameMaxLength] : name);
        db.BusinessUnits.Add(root);
        return root;
    }

    /// <summary>Üyelik henüz bir departmanda değilse kök departmana ekler (kaydetmez).</summary>
    public static async Task EnsureMemberAsync(ModulixDbContext db, Guid membershipId, BusinessUnit root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(root);
        if (db.BusinessUnitMembers.Local.Any(m => m.MembershipId == membershipId)
            || await db.BusinessUnitMembers.AnyAsync(m => m.MembershipId == membershipId, cancellationToken))
        {
            return;
        }

        db.BusinessUnitMembers.Add(new BusinessUnitMember(root.Id, membershipId));
    }
}
