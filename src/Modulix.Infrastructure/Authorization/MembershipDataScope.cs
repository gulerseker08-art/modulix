using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Boru hattının veri kapsamı adımı (Y8): kişinin rollerindeki en geniş kapsam. "Ekibimin kayıtları"nda kişi ve ekip
/// arkadaşları (kişinin ekiplerinden herhangi birindeki herkes) sorumlu olabilir. İstek başına bir kez hesaplanır
/// (scoped); rol veya ekip değişikliği bir sonraki istekte geçerli olur. Olay abonesinin kapsamı tenant genelidir (ADR-0007).
/// </summary>
internal sealed class MembershipDataScope(ModulixDbContext db, MembershipPermissions permissions, EventHandlingScope handling) : IDataScopeService
{
    private (Guid MembershipId, IReadOnlySet<Guid>? Owners)? cached;

    public async Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        if (handling.Subscription is not null)
        {
            return null;
        }

        if (cached is { } hit && hit.MembershipId == tenant.MembershipId)
        {
            return hit.Owners;
        }

        var owners = await permissions.GetScopeAsync(tenant.MembershipId, cancellationToken) switch
        {
            RecordScope.All => null,
            RecordScope.Team => await TeamOwnersAsync(db, tenant.MembershipId, tenant.UserId, cancellationToken),
            _ => new HashSet<Guid> { tenant.UserId },
        };
        cached = (tenant.MembershipId, owners);
        return owners;
    }

    /// <summary>Kişi ve kişinin ekiplerinden herhangi birindeki üyeler (kullanıcı kimlikleri).</summary>
    internal static async Task<IReadOnlySet<Guid>> TeamOwnersAsync(
        ModulixDbContext db, Guid membershipId, Guid userId, CancellationToken cancellationToken)
    {
        // Ekip üyelikleri tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
        var teamIds = db.TeamMembers.Where(tm => tm.MembershipId == membershipId).Select(tm => tm.TeamId);
        var teammates = await (
            from member in db.TeamMembers.AsNoTracking()
            join membership in db.Memberships.AsNoTracking() on member.MembershipId equals membership.Id
            where teamIds.Contains(member.TeamId)
            select membership.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return teammates.Append(userId).ToHashSet();
    }
}
