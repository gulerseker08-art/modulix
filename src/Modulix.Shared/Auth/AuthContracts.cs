namespace Modulix.Shared.Auth;

// API ile arayüz (Modulix.Web) arasında paylaşılan giriş ve tenant seçimi sözleşmeleri.

public sealed record LoginRequest(string Email, string Password);

public sealed record SelectTenantRequest(Guid TenantId);

public sealed record MembershipDto(Guid TenantId, string TenantName, string TenantSlug);

/// <summary>Oturumdaki kullanıcı. <see cref="ActiveTenantId"/> sunucuda doğrulanmış aktif tenant'tır.</summary>
public sealed record MeResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? ActiveTenantId,
    IReadOnlyList<MembershipDto> Memberships);

public sealed record CurrentTenantResponse(Guid TenantId, string Name, string Slug);
