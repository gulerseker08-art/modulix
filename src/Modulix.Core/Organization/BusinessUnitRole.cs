using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Organization;

/// <summary>
/// Departmana atanmış rol (ADR-0026 K6): departmandaki her üyelik rolün izinlerini ve kapsamını alır. Kullanıcıya doğrudan
/// atanan rollerle toplanır, birbirini ezmez. <see cref="InheritToChildren"/> kapalıyken (varsayılan) rol yalnızca bu
/// departmandaki kullanıcılar içindir; açıkken alt departmanlardaki kullanıcılar da alır. Tenant verisidir; Row-Level
/// Security ile korunur.
/// </summary>
public sealed class BusinessUnitRole : ITenantEntity, ICompanyShared
{
    private BusinessUnitRole()
    {
        // EF Core için
    }

    public BusinessUnitRole(Guid businessUnitId, Guid roleId, bool inheritToChildren)
    {
        if (businessUnitId == Guid.Empty)
        {
            throw new ArgumentException("BusinessUnitId boş olamaz.", nameof(businessUnitId));
        }

        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("RoleId boş olamaz.", nameof(roleId));
        }

        Id = Guid.CreateVersion7();
        BusinessUnitId = businessUnitId;
        RoleId = roleId;
        InheritToChildren = inheritToChildren;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid BusinessUnitId { get; private set; }

    public Guid RoleId { get; private set; }

    public bool InheritToChildren { get; private set; }

    public void SetInheritToChildren(bool inheritToChildren) => InheritToChildren = inheritToChildren;
}
