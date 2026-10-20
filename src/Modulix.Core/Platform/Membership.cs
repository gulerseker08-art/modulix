namespace Modulix.Core.Platform;

public enum MembershipStatus
{
    Active = 1,

    /// <summary>Devre dışı: kullanıcı bu tenant'a erişemez; bir sonraki istekte geçerli olur.</summary>
    Disabled = 2,
}

/// <summary>
/// Bir kullanıcının bir tenant'taki üyeliği. Bir kullanıcının birden fazla üyeliği olabilir;
/// roller üyelik başına atanır (<see cref="Authorization.MembershipRole"/>); veri kapsamı da üyelik başına tanımlanacaktır.
/// </summary>
public sealed class Membership
{
    private Membership()
    {
        // EF Core için
    }

    public Membership(Guid tenantId, Guid userId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId boş olamaz.", nameof(tenantId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId boş olamaz.", nameof(userId));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        Status = MembershipStatus.Active;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => Status == MembershipStatus.Active;

    public void Disable() => Status = MembershipStatus.Disabled;

    public void Enable() => Status = MembershipStatus.Active;
}
