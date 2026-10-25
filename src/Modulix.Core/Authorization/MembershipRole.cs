using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Authorization;

/// <summary>Bir üyeliğe atanmış rol. Tenant verisidir; Row-Level Security ile korunur.</summary>
public sealed class MembershipRole : ITenantEntity, ICompanyShared
{
    private MembershipRole()
    {
        // EF Core için
    }

    public MembershipRole(Guid membershipId, Guid roleId)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("MembershipId boş olamaz.", nameof(membershipId));
        }

        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("RoleId boş olamaz.", nameof(roleId));
        }

        Id = Guid.CreateVersion7();
        MembershipId = membershipId;
        RoleId = roleId;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }
}
