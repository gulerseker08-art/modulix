namespace Modulix.Shared.Auth;

// API ile arayüz (Modulix.Web) arasında paylaşılan giriş ve tenant seçimi sözleşmeleri.

public sealed record LoginRequest(string Email, string Password);

public sealed record SelectTenantRequest(Guid TenantId);

public sealed record MembershipDto(Guid TenantId, string TenantName, string TenantSlug);

/// <summary>
/// Oturumdaki kullanıcı. <see cref="ActiveTenantId"/> sunucuda doğrulanmış aktif tenant'tır.
/// <see cref="IsPlatformAdministrator"/>: platform ekranlarını gösterir; yetkiyi sunucu her istekte denetler.
/// </summary>
public sealed record MeResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? ActiveTenantId,
    IReadOnlyList<MembershipDto> Memberships,
    bool IsPlatformAdministrator = false);

public sealed record CurrentTenantResponse(Guid TenantId, string Name, string Slug);

/// <summary>Aktif tenant'ın etkin üyesi; kayıtlarda sorumlu seçmek için.</summary>
public sealed record TenantMemberResponse(Guid UserId, string DisplayName);
