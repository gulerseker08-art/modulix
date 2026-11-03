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

    // Dilim 1 (ADR-0028): tür anahtarı iletilir ama kapsam henüz türe göre değişmez; sonuç üyelik başına tek hesaptır.
    public async Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        if (handling.Subscription is not null)
        {
            return null;
        }

        if (cached is { } hit && hit.MembershipId == tenant.MembershipId)
        {
            return hit.Owners;
        }

        var owners = await OwnersAsync(tenant, cancellationToken);
        cached = (tenant.MembershipId, owners);
        return owners;
    }

    /// <summary>
    /// Kişinin etkin rollerinin (doğrudan ve departmandan gelen) her kapsamı bir sahip kümesi üretir; sonuç kümelerin
    /// birleşimidir (ADR-0026 K5). Herhangi biri "tümü" ise süzgeç yoktur (<c>null</c>). Her kapsam kişinin kendi kayıtlarını
    /// kapsar. "Departman ve altı" kök departmanda tüm firmadır ve "tümü"ne indirgenir (sahipsiz kayıtlar dahil).
    /// </summary>
    private async Task<IReadOnlySet<Guid>?> OwnersAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        var scopes = await permissions.GetScopesAsync(tenant.MembershipId, cancellationToken);
        if (scopes.Contains(RecordScope.All))
        {
            return null;
        }

        var owners = new HashSet<Guid> { tenant.UserId };
        foreach (var scope in scopes.Distinct())
        {
            switch (scope)
            {
                case RecordScope.Team:
                    owners.UnionWith(await TeamOwnersAsync(db, tenant.MembershipId, tenant.UserId, cancellationToken));
                    break;
                case RecordScope.Unit or RecordScope.UnitAndBelow:
                    var unit = await permissions.GetUnitAsync(tenant.MembershipId, cancellationToken);
                    if (unit is null)
                    {
                        break;
                    }

                    var includeBelow = scope == RecordScope.UnitAndBelow;
                    if (includeBelow && unit.Path.Count(c => c == '/') == 2)
                    {
                        return null;
                    }

                    owners.UnionWith(await UnitOwnersAsync(unit, includeBelow, cancellationToken));
                    break;
            }
        }

        return owners;
    }

    /// <summary>
    /// Departmandaki (ve istenirse alt departmanlardaki) bütün üyeliklerin kullanıcı kimlikleri; pasif ve devre dışı
    /// üyelikler dahildir, böylece ayrılan çalışanın kayıtları departmanda kalır (ADR-0026 K7).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> UnitOwnersAsync(MembershipUnit unit, bool includeBelow, CancellationToken cancellationToken)
    {
        var unitIds = includeBelow ? await permissions.GetUnitTreeIdsAsync(unit.Path, cancellationToken) : [unit.Id];
        return await (
            from member in db.BusinessUnitMembers.AsNoTracking()
            join membership in db.Memberships.AsNoTracking() on member.MembershipId equals membership.Id
            where unitIds.Contains(member.BusinessUnitId) && membership.TenantId == member.TenantId
            select membership.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Kişi ve kişinin ekiplerinden herhangi birindeki üyeler (kullanıcı kimlikleri). Ekip üyelikleri tenant verisidir (sorgu
    /// filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür); üyelik–kullanıcı eşleştirmesi aynı sorguda yapılır (ADR-0020).
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>> TeamOwnersAsync(
        ModulixDbContext db, Guid membershipId, Guid userId, CancellationToken cancellationToken)
    {
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
