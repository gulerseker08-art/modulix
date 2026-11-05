using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Sectors;

/// <summary>
/// Firma ilişki rolü kataloğu (ADR-0029 K9). Tablo <c>platform</c> şemasındadır, RLS dışıdır ve tenant verisi içermez; her yazma,
/// değiştiren yönetici ile birlikte <c>PlatformAuditLog</c>'a aynı işlemde yazılır. Rol silinmez, pasife alınır (firma kayıtları
/// kodla başvurduğu için).
/// </summary>
internal sealed class RelationshipRoleService(ModulixDbContext db) : IRelationshipRoles
{
    public async Task<IReadOnlyList<RelationshipRoleView>> ListAsync(CancellationToken cancellationToken) =>
        await db.RelationshipRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => new RelationshipRoleView(r.Id, r.Code, r.Name, r.IsActive)).ToListAsync(cancellationToken);

    public async Task<RelationshipRoleView> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken)
    {
        var role = new RelationshipRole(code, name);
        if (await db.RelationshipRoles.AnyAsync(r => r.Code == code, cancellationToken))
        {
            throw new SectorCatalogConflictException($"Bu kodla bir ilişki rolü zaten var: '{code}'.");
        }

        db.RelationshipRoles.Add(role);
        Audit(actingUserId, "sector.relationship-role.created", new JsonObject { ["code"] = role.Code, ["name"] = role.Name });
        await db.SaveChangesAsync(cancellationToken);
        return ToView(role);
    }

    public async Task<RelationshipRoleView> SetActiveAsync(Guid actingUserId, string code, bool active, CancellationToken cancellationToken)
    {
        var role = await db.RelationshipRoles.SingleOrDefaultAsync(r => r.Code == code, cancellationToken)
            ?? throw new SectorCatalogNotFoundException($"İlişki rolü bulunamadı: '{code}'.");
        if (role.IsActive != active)
        {
            role.SetActive(active);
            Audit(actingUserId, "sector.relationship-role.active", new JsonObject { ["code"] = role.Code, ["active"] = active });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToView(role);
    }

    private void Audit(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private static RelationshipRoleView ToView(RelationshipRole r) => new(r.Id, r.Code, r.Name, r.IsActive);
}
