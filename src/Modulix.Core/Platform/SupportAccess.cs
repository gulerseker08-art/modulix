namespace Modulix.Core.Platform;

/// <summary>Destek erişiminin süre sınırları (dakika). Erişim her zaman süreli ve salt okunurdur.</summary>
public static class SupportAccessLimits
{
    public const int MinMinutes = 15;
    public const int MaxMinutes = 480;
    public const int DefaultMinutes = 60;
    public const int ReasonMaxLength = 500;
}

/// <summary>
/// Gerekçeli, süreli ve tek firmayla sınırlı destek erişimi (ADR-0028 Dilim 7, yol haritası Y1.3). Platform yöneticisi firma verisini
/// varsayılan görmez; bu kayıt, o firmada <see cref="ExpiresAt"/>'e kadar salt okunur bir üyelik açar. Silinmez: süresi dolan ya da
/// geri alınan erişim <see cref="EndedAt"/>/<see cref="ExpiresAt"/> ile kapanır ve geçmişte kalır.
/// </summary>
public sealed class SupportAccessGrant
{
    private SupportAccessGrant()
    {
        // EF Core için
    }

    public SupportAccessGrant(Guid tenantId, Guid userId, Guid membershipId, string reason, DateTimeOffset now, int minutes)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Destek erişimi için gerekçe zorunludur.", "reason");
        }

        if (trimmed.Length > SupportAccessLimits.ReasonMaxLength)
        {
            throw new ArgumentException($"Gerekçe en çok {SupportAccessLimits.ReasonMaxLength} karakter olabilir.", "reason");
        }

        if (minutes is < SupportAccessLimits.MinMinutes or > SupportAccessLimits.MaxMinutes)
        {
            throw new ArgumentException(
                $"Süre {SupportAccessLimits.MinMinutes}-{SupportAccessLimits.MaxMinutes} dakika arasında olmalı.", "minutes");
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        MembershipId = membershipId;
        Reason = trimmed;
        GrantedAt = now;
        ExpiresAt = now.AddMinutes(minutes);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Erişimi alan platform hesabı.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Firmadaki destek üyeliği (<see cref="MembershipStatus.Support"/>); aynı kişi için yeniden kullanılır.</summary>
    public Guid MembershipId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Süresi dolmadan geri alındıysa o an; yoksa <c>null</c>.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? EndedByUserId { get; private set; }

    public string? EndReason { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => EndedAt is null && ExpiresAt > now;

    public void End(DateTimeOffset at, Guid? byUserId, string? reason)
    {
        EndedAt ??= at;
        EndedByUserId ??= byUserId;
        EndReason ??= string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}

public enum SupportAccessState
{
    Active = 1,
    Expired = 2,
    Revoked = 3,
}

/// <summary><see cref="TenantName"/>/<see cref="UserName"/>: kayıt anındaki değil güncel adlardır.</summary>
public sealed record SupportAccessInfo(
    Guid Id,
    Guid TenantId,
    string TenantName,
    Guid UserId,
    string UserName,
    string Reason,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    SupportAccessState State);

/// <summary>
/// Destek erişimi (ADR-0028 Dilim 7, Y1.3). Erişim: gerekçe zorunlu, 15 dakika-8 saat, tek firma, <b>salt okunur</b> (yalnızca açık
/// modüllerin okuma izinleri; dışa aktarma, kullanıcı, denetim ve ayar izinleri yok), firma yöneticilerine e-posta, firmanın ve
/// platformun denetim kaydına yazılır. Süresi dolan ya da geri alınan erişim her istekte reddedilir. Hiçbir kayıt silinmez.
/// Hatalar: <see cref="UserAdministrationException"/> (bulunamadı, çakışma), geçersiz alan için <see cref="ArgumentException"/>.
/// </summary>
public interface ISupportAccess
{
    /// <summary>
    /// <paramref name="userId"/> (platform hesabı) için <paramref name="tenantId"/> firmasında erişim açar. Firma kapalıysa, kişi
    /// firmanın olağan üyesiyse ya da kişinin zaten geçerli erişimi varsa çakışma.
    /// </summary>
    Task<SupportAccessInfo> GrantAsync(Guid tenantId, Guid userId, string reason, int minutes, CancellationToken cancellationToken);

    /// <summary>
    /// Geçerli erişimi kapatır. <paramref name="tenantId"/> verilirse erişim o firmanınsa kapanır (firma yöneticisinin geri alması);
    /// başka firmanınki "bulunamadı" sayılır. Geçerli değilse çakışma.
    /// </summary>
    Task RevokeAsync(Guid grantId, Guid? tenantId, Guid actingUserId, string? reason, CancellationToken cancellationToken);

    /// <summary>En yeni üstte; <paramref name="tenantId"/> boşsa bütün firmalar (platform görünümü).</summary>
    Task<IReadOnlyList<SupportAccessInfo>> ListAsync(Guid? tenantId, bool activeOnly, CancellationToken cancellationToken);
}
