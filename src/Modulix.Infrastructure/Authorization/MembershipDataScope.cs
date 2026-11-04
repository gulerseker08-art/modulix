using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Boru hattının veri kapsamı adımı (Y8, ADR-0030): kişinin rollerindeki kapsamların birleşimi, kayıt türüne göre. "Ekibimin kayıtları"nda kişi ve ekip
/// arkadaşları (kişinin ekiplerinden herhangi birindeki herkes) sorumlu olabilir. İstek başına bir kez hesaplanır
/// (scoped); rol veya ekip değişikliği bir sonraki istekte geçerli olur. Olay abonesinin kapsamı tenant genelidir (ADR-0007).
/// </summary>
internal sealed class MembershipDataScope(
    ModulixDbContext db, MembershipPermissions permissions, ScopeLimits limits, EventHandlingScope handling) : IDataScopeService
{
    private readonly Dictionary<(Guid MembershipId, string EntityKey), IReadOnlySet<Guid>?> cached = [];

    /// <summary>
    /// Kapsam kayıt türüne göre çözülür (ADR-0030): rolün türe özel istisnası varsa o, yoksa varsayılan kapsamı; sonra platformun
    /// o tür için koyduğu tavana indirilir (okuma tarafı savunması, tavan sonradan daralsa da rol kaydı beklenmez). İstek başına
    /// (üyelik, tür) çifti için bir kez hesaplanır. Tavan ve istisna kaydı yoksa sonuç eski tek-kapsam hesabıyla aynıdır.
    /// </summary>
    public async Task<IReadOnlySet<Guid>?> GetOwnersAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        if (handling.Subscription is not null)
        {
            return null;
        }

        if (cached.TryGetValue((tenant.MembershipId, entityKey), out var hit))
        {
            return hit;
        }

        var owners = await OwnersAsync(tenant, entityKey, cancellationToken);
        cached[(tenant.MembershipId, entityKey)] = owners;
        return owners;
    }

    /// <summary>Olay işleyicisi tenant genelini görür (<c>GetOwnersAsync</c> ile aynı kural); kullanıcı isteğinde rollerin ayarına bakılır.</summary>
    public async Task<bool> HidesOutOfScopeSharedAsync(TenantContext tenant, CancellationToken cancellationToken) =>
        handling.Subscription is null && await permissions.HidesOutOfScopeSharedAsync(tenant.MembershipId, cancellationToken);

    /// <summary>
    /// Kişinin etkin rollerinin (doğrudan ve departmandan gelen) her kapsamı bir sahip kümesi üretir; sonuç kümelerin
    /// birleşimidir (ADR-0026 K5). Herhangi biri "tümü" ise süzgeç yoktur (<c>null</c>). Her kapsam kişinin kendi kayıtlarını
    /// kapsar. "Departman ve altı" kök departmanda tüm firmadır ve "tümü"ne indirgenir (sahipsiz kayıtlar dahil).
    /// </summary>
    private async Task<IReadOnlySet<Guid>?> OwnersAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)
    {
        var (isAdministrator, scopes) = await permissions.GetScopesForAsync(tenant.MembershipId, entityKey, cancellationToken);

        // Platform tavanı (ADR-0030 K5/2, K6): tavan olan türde kapsam tavana indirilir; yönetici yalnızca tavan onu da bağlıyorsa.
        if ((await limits.GetAsync(tenant.TenantId, cancellationToken)).TryGetValue(entityKey, out var limit)
            && (!isAdministrator || limit.AppliesToAdministrators))
        {
            scopes = [.. scopes.Select(s => RecordScopeRules.Clamp(s, limit.MaxScope)).Distinct()];
        }

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
