namespace Modulix.Shared.Auth;

// API ile arayüz (Modulix.Web) arasında paylaşılan giriş ve tenant seçimi sözleşmeleri.

public sealed record LoginRequest(string Email, string Password);

public sealed record SelectTenantRequest(Guid TenantId);

/// <summary>Aktif işletici şirketi değiştirir (ADR-0008, adım 2); şirket oturumda sunucuda tutulur, başlıkla taşınmaz.</summary>
public sealed record SelectCompanyRequest(Guid CompanyId);

/// <summary><see cref="DisplayName"/>: firmanın ayarladığı görünen ad (Y6b); yoksa <see cref="TenantName"/> gösterilir.</summary>
public sealed record MembershipDto(Guid TenantId, string TenantName, string TenantSlug, string? DisplayName = null)
{
    public string ShownName => DisplayName ?? TenantName;
}

/// <summary>
/// Oturumdaki kullanıcı. <see cref="ActiveTenantId"/> sunucuda doğrulanmış aktif tenant'tır.
/// <see cref="IsPlatformAdministrator"/>: platform ekranlarını gösterir; yetkiyi sunucu her istekte denetler.
/// <see cref="PlatformRoles"/>: platform hesabının etkin rolleri (Support, Operations, Finance, Owner); ekran yalnızca gizleme içindir.
/// </summary>
public sealed record MeResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? ActiveTenantId,
    IReadOnlyList<MembershipDto> Memberships,
    bool IsPlatformAdministrator = false,
    bool TwoFactorEnabled = false,
    bool TwoFactorRequired = false,
    IReadOnlyList<string>? PlatformRoles = null);

/// <summary>
/// Girişin yanıtı: şifre doğruysa ve iki adımlı doğrulama açıksa 202 ve <see cref="TwoFactorRequired"/> döner; kod
/// <c>/api/auth/login/two-factor</c>'a gönderilir (5 dakika içinde). Aksi halde gövde <see cref="MeResponse"/>'tur.
/// </summary>
public sealed record LoginChallengeResponse(bool TwoFactorRequired);

/// <summary>Uygulamadaki 6 haneli kod veya bir kurtarma kodu.</summary>
public sealed record TwoFactorLoginRequest(string Code);

public sealed record CurrentTenantResponse(Guid TenantId, string Name, string Slug);

/// <summary>Aktif tenant'ın etkin üyesi; kayıtlarda sorumlu seçmek için.</summary>
public sealed record TenantMemberResponse(Guid UserId, string DisplayName);
