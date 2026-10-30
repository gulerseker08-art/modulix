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
internal sealed class MembershipDataScope(
    ModulixDbContext db, MembershipPermissions permissions, EventHandlingScope handling) : IDataScopeService
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

    /// <summary>
    /// Kişi ve kişinin ekiplerinden herhangi birindeki üyeler (kullanıcı kimlikleri). Ekip üyelikleri tenant
    /// bağlamındadır, üyelik–kullanıcı eşleştirmesi platform tablolarındadır (ADR-0020): iki adımda birleştirilir.
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>> TeamOwnersAsync(
        ModulixDbContext db, Guid membershipId, Guid userId, CancellationToken cancellationToken)
    {
        // Ekip üyelikleri tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
        var teamIds = db.TeamMembers.Where(tm => tm.MembershipId == membershipId).Select(tm => tm.TeamId);
        var teammateMembershipIds = await db.TeamMembers.AsNoTracking()
            .Where(member => teamIds.Contains(member.TeamId))
            .Select(member => member.MembershipId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Üyelik kimliklerini kullanıcı kimliklerine platform tablolarından çöz (Dilim 3'te tek sorguya alınabilir).
        var teammates = await db.Memberships.AsNoTracking()
            .Where(m => teammateMembershipIds.Contains(m.Id))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        return teammates.Append(userId).ToHashSet();
    }
}
