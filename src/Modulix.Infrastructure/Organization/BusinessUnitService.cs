using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Infrastructure.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Organization;

/// <summary>
/// Departman ağacı (ADR-0026). Üyelikler platform tablosundadır (RLS dışı); tenant'a ait oldukları burada açıkça denetlenir.
/// Departman değişiklikleri <c>core.business-unit</c>, kullanıcının departmanı <c>core.membership</c> kaydı olarak denetime yazılır.
/// </summary>
internal sealed class BusinessUnitService(
    ModulixDbContext db, ITenantContextAccessor tenantAccessor, MembershipPermissions permissions, IAuditWriter audit) : IBusinessUnitService
{
    private const string PathSeparator = " › ";

    /// <summary>
    /// Yönetici olmayan işlemi yapan yalnızca kendi kapsamındaki departmanları görür ve yönetir (ADR-0026): başka bayilerin
    /// departmanı "bulunamadı" sayılır, adı ve üyeleri belli edilmez. Tüm kayıtlar kapsamında kısıt yoktur.
    /// </summary>
    private async Task<IReadOnlySet<Guid>?> VisibleAsync(CancellationToken cancellationToken) =>
        await permissions.GetVisibleUnitIdsAsync(RequireTenant().MembershipId, cancellationToken);

    public async Task<IReadOnlyList<BusinessUnitSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(cancellationToken);
        var units = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var counts = await MemberCountsAsync(cancellationToken);
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        return units
            .Where(u => visible is null || visible.Contains(u.Id))
            .OrderBy(u => u.Depth)
            .ThenBy(u => u.Name, StringComparer.CurrentCulture)
            .Select(u => ToSummary(u, names, counts))
            .ToList();
    }

    public async Task<BusinessUnitDetail?> GetAsync(Guid businessUnitId, CancellationToken cancellationToken)
    {
        var unit = await db.BusinessUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == businessUnitId, cancellationToken);
        return unit is null || !await IsVisibleAsync(unit.Id, cancellationToken) ? null : await DetailAsync(unit, cancellationToken);
    }

    private async Task<bool> IsVisibleAsync(Guid businessUnitId, CancellationToken cancellationToken) =>
        await VisibleAsync(cancellationToken) is not { } visible || visible.Contains(businessUnitId);

    public async Task<BusinessUnitDetail?> CreateAsync(
        Guid parentId, string name, string? code, string kind, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var parent = await db.BusinessUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == parentId, cancellationToken);
        if (parent is null || !await IsVisibleAsync(parent.Id, cancellationToken))
        {
            return null;
        }

        BusinessUnit unit;
        try
        {
            unit = BusinessUnit.CreateChild(parent, name, code, kind);
        }
        catch (ArgumentException ex) when (ex.ParamName == "parent")
        {
            throw new BusinessUnitConflictException(ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty));
        }

        await EnsureUniqueNameAsync(unit.ParentId, unit.Name, exceptId: null, cancellationToken);
        db.BusinessUnits.Add(unit);
        Audit(tenant, unit.Id, AuditAction.Created, before: null, after: Snapshot(unit, parent.Name));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(unit, cancellationToken);
    }

    public async Task<BusinessUnitDetail?> UpdateAsync(
        Guid businessUnitId, string name, string? code, string kind, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var unit = await db.BusinessUnits.SingleOrDefaultAsync(u => u.Id == businessUnitId, cancellationToken);
        if (unit is null || !await IsVisibleAsync(unit.Id, cancellationToken))
        {
            return null;
        }

        var parentName = await ParentNameAsync(unit, cancellationToken);
        var before = Snapshot(unit, parentName);
        unit.Rename(name);
        unit.SetCode(code);
        unit.SetKind(kind);
        await EnsureUniqueNameAsync(unit.ParentId, unit.Name, unit.Id, cancellationToken);
        var after = Snapshot(unit, parentName);
        if (!JsonNode.DeepEquals(before, after))
        {
            Audit(tenant, unit.Id, AuditAction.Updated, before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(unit, cancellationToken);
    }

    public async Task<BusinessUnitDetail?> SetActiveAsync(Guid businessUnitId, bool isActive, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var unit = await db.BusinessUnits.SingleOrDefaultAsync(u => u.Id == businessUnitId, cancellationToken);
        if (unit is null || !await IsVisibleAsync(unit.Id, cancellationToken))
        {
            return null;
        }

        if (unit.IsActive == isActive)
        {
            return await DetailAsync(unit, cancellationToken);
        }

        var parentName = await ParentNameAsync(unit, cancellationToken);
        var before = Snapshot(unit, parentName);
        if (isActive)
        {
            if (unit.ParentId is { } parentId
                && !await db.BusinessUnits.AnyAsync(u => u.Id == parentId && u.IsActive, cancellationToken))
            {
                throw new BusinessUnitConflictException("Üst departman kapalıyken departman yeniden açılamaz.");
            }

            unit.Reopen();
        }
        else
        {
            if (unit.IsRoot)
            {
                throw new BusinessUnitConflictException("Kök departman kapatılamaz.");
            }

            if (await db.BusinessUnits.AnyAsync(u => u.ParentId == unit.Id && u.IsActive, cancellationToken))
            {
                throw new BusinessUnitConflictException("Etkin alt departmanı olan departman kapatılamaz; önce alt departmanları kapatın.");
            }

            if (await db.BusinessUnitMembers.AnyAsync(m => m.BusinessUnitId == unit.Id, cancellationToken))
            {
                throw new BusinessUnitConflictException(
                    "Kullanıcısı olan departman kapatılamaz; önce kullanıcıları (devre dışı olanlar dahil) başka bir departmana taşıyın.");
            }

            unit.Close();
        }

        Audit(tenant, unit.Id, AuditAction.Updated, before, Snapshot(unit, parentName));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(unit, cancellationToken);
    }

    public async Task<MembershipBusinessUnit?> GetMembershipBusinessUnitAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await MembershipExistsAsync(tenant, membershipId, cancellationToken))
        {
            return null;
        }

        var unitId = await db.BusinessUnitMembers.AsNoTracking()
            .Where(m => m.MembershipId == membershipId)
            .Select(m => (Guid?)m.BusinessUnitId)
            .SingleOrDefaultAsync(cancellationToken);
        if (unitId is null)
        {
            return null;
        }

        // Kullanıcı başka bir bayinin departmanındaysa adı belli edilmez (kullanıcı kartında departman boş görünür).
        if (!await IsVisibleAsync(unitId.Value, cancellationToken))
        {
            return null;
        }

        var units = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        return units.Where(u => u.Id == unitId).Select(u => new MembershipBusinessUnit(u.Id, u.Name, PathNames(u, names), u.Kind, u.IsActive)).SingleOrDefault();
    }

    public async Task<BusinessUnitSummary?> FindAsync(Guid businessUnitId, CancellationToken cancellationToken)
    {
        var units = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        return units.Where(u => u.Id == businessUnitId).Select(u => ToSummary(u, names, new Dictionary<Guid, int>())).SingleOrDefault();
    }

    public async Task<bool> SetMembershipBusinessUnitAsync(
        Guid membershipId, Guid businessUnitId, CancellationToken cancellationToken, string? reason = null, string? recordsNote = null)
    {
        var tenant = RequireTenant();
        if (!await MembershipExistsAsync(tenant, membershipId, cancellationToken))
        {
            return false;
        }

        var units = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        var target = units.SingleOrDefault(u => u.Id == businessUnitId)
            ?? throw new ArgumentException("Bu firmada bulunmayan departman seçildi.", nameof(businessUnitId));
        if (!target.IsActive)
        {
            throw new ArgumentException("Kapalı bir departmana kullanıcı atanamaz.", nameof(businessUnitId));
        }

        var current = await db.BusinessUnitMembers.SingleOrDefaultAsync(m => m.MembershipId == membershipId, cancellationToken);
        if (current?.BusinessUnitId == target.Id)
        {
            return true;
        }

        var previous = current is null ? null : units.SingleOrDefault(u => u.Id == current.BusinessUnitId);
        if (current is null)
        {
            db.BusinessUnitMembers.Add(new BusinessUnitMember(target.Id, membershipId));
        }
        else
        {
            current.MoveTo(target.Id);
        }

        // Gerekçe ve kayıtların akıbeti yalnızca sonraki hâlde yazılır; değişiklik farkında görünürler.
        var after = new JsonObject { ["businessUnit"] = PathNames(target, names) };
        if (!string.IsNullOrWhiteSpace(reason))
        {
            after["businessUnitChangeReason"] = reason.Trim();
        }

        if (!string.IsNullOrWhiteSpace(recordsNote))
        {
            after["businessUnitRecords"] = recordsNote.Trim();
        }

        Audit(
            tenant,
            membershipId,
            AuditAction.Updated,
            new JsonObject { ["businessUnit"] = previous is null ? null : PathNames(previous, names) },
            after,
            CoreModule.AuditedEntityKeys.Membership);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<BusinessUnitDetail> DetailAsync(BusinessUnit unit, CancellationToken cancellationToken)
    {
        var units = await db.BusinessUnits.AsNoTracking().ToListAsync(cancellationToken);
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        var counts = await MemberCountsAsync(cancellationToken);
        var members = await (
            from member in db.BusinessUnitMembers.AsNoTracking()
            join membership in db.Memberships.AsNoTracking() on member.MembershipId equals membership.Id
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where member.BusinessUnitId == unit.Id && membership.TenantId == member.TenantId
            orderby user.DisplayName
            select new BusinessUnitMemberSummary(membership.Id, user.Id, user.DisplayName, user.Email!))
            .ToListAsync(cancellationToken);
        return new BusinessUnitDetail(ToSummary(unit, names, counts), members);
    }

    private async Task<Dictionary<Guid, int>> MemberCountsAsync(CancellationToken cancellationToken) =>
        await db.BusinessUnitMembers.AsNoTracking()
            .GroupBy(m => m.BusinessUnitId)
            .Select(g => new { UnitId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UnitId, g => g.Count, cancellationToken);

    private async Task<string?> ParentNameAsync(BusinessUnit unit, CancellationToken cancellationToken) =>
        unit.ParentId is { } parentId
            ? await db.BusinessUnits.AsNoTracking().Where(u => u.Id == parentId).Select(u => u.Name).SingleOrDefaultAsync(cancellationToken)
            : null;

    private async Task EnsureUniqueNameAsync(Guid? parentId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await db.BusinessUnits.AnyAsync(u => u.ParentId == parentId && u.Name == name && u.Id != exceptId, cancellationToken))
        {
            throw new BusinessUnitConflictException($"Bu üst departmanda aynı adda bir departman zaten var: '{name}'.");
        }
    }

    // Üyelikler platform tablosundadır (RLS dışı); tenant'a ait oldukları burada açıkça denetlenir.
    private async Task<bool> MembershipExistsAsync(TenantContext tenant, Guid membershipId, CancellationToken cancellationToken) =>
        await db.Memberships.AnyAsync(m => m.Id == membershipId && m.TenantId == tenant.TenantId, cancellationToken);

    private static BusinessUnitSummary ToSummary(BusinessUnit unit, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, int> counts) =>
        new(unit.Id, unit.ParentId, unit.Name, unit.Code, unit.Kind, unit.Depth, unit.IsActive, counts.GetValueOrDefault(unit.Id), PathNames(unit, names));

    /// <summary>Kimlik yolundaki her kimliğin adı: "Merkez › Marmara Bayi".</summary>
    private static string PathNames(BusinessUnit unit, IReadOnlyDictionary<Guid, string> names) =>
        string.Join(
            PathSeparator,
            unit.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => Guid.TryParseExact(segment, "N", out var id) && names.TryGetValue(id, out var name) ? name : unit.Name));

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan departmanlar yönetilemez.");

    private void Audit(TenantContext tenant, Guid entityId, AuditAction action, JsonObject? before, JsonObject? after, string? entityKey = null) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            entityKey ?? CoreModule.AuditedEntityKeys.BusinessUnit,
            entityId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(BusinessUnit unit, string? parentName) => new()
    {
        ["name"] = unit.Name,
        ["code"] = unit.Code,
        ["kind"] = unit.Kind,
        ["parent"] = parentName,
        ["isActive"] = unit.IsActive,
    };
}
