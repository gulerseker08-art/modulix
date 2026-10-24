namespace Modulix.Core.Platform;

public enum MembershipStatus
{
    Active = 1,

    /// <summary>Devre dışı: kullanıcı bu tenant'a erişemez; bir sonraki istekte geçerli olur.</summary>
    Disabled = 2,

    /// <summary>
    /// Davet bekliyor: hesap e-postayla davet edildi, şifre henüz belirlenmedi. Giriş yapılamaz; davet
    /// bağlantısıyla şifre belirlenince üyelik etkinleşir.
    /// </summary>
    Invited = 3,
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

    /// <summary>Davetle eklenen üyelik. Hesabın şifresi yoksa davet bekler; varsa (başka tenant'ta kullanılıyor) hemen etkindir.</summary>
    public static Membership CreateInvited(Guid tenantId, Guid userId, bool awaitsAcceptance, DateTimeOffset invitedAt)
    {
        var membership = new Membership(tenantId, userId)
        {
            Status = awaitsAcceptance ? MembershipStatus.Invited : MembershipStatus.Active,
            InvitedAt = invitedAt,
        };
        return membership;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Son davet e-postasının gönderildiği an (yeniden göndermede güncellenir); davetle eklenmediyse <c>null</c>.</summary>
    public DateTimeOffset? InvitedAt { get; private set; }

    /// <summary>Bu tenant'a son giriş (giriş veya tenant seçimi).</summary>
    public DateTimeOffset? LastSignInAt { get; private set; }

    public bool IsActive => Status == MembershipStatus.Active;

    public void Disable()
    {
        if (Status != MembershipStatus.Active)
        {
            throw new InvalidOperationException("Yalnızca etkin üyelik devre dışı bırakılabilir.");
        }

        Status = MembershipStatus.Disabled;
    }

    public void Enable()
    {
        if (Status != MembershipStatus.Disabled)
        {
            throw new InvalidOperationException("Yalnızca devre dışı üyelik etkinleştirilebilir.");
        }

        Status = MembershipStatus.Active;
    }

    public void MarkInvitationSent(DateTimeOffset at)
    {
        if (Status != MembershipStatus.Invited)
        {
            throw new InvalidOperationException("Davet yalnızca bekleyen üyelik için yeniden gönderilir.");
        }

        InvitedAt = at;
    }

    public void AcceptInvitation()
    {
        if (Status != MembershipStatus.Invited)
        {
            throw new InvalidOperationException("Üyelik davet beklemiyor.");
        }

        Status = MembershipStatus.Active;
    }
}
