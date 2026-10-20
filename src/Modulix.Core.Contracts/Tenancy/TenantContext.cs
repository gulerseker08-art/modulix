namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Sunucuda doğrulanmış aktif tenant bağlamı. Yalnızca kullanıcının bu tenant'ta etkin bir
/// üyeliği ve tenant'ın kendisi etkin olduğunda oluşur; istemcinin gönderdiği bir TenantId'den
/// asla doğrudan oluşturulmaz (talimat Bölüm 4, Multi-tenant).
/// </summary>
public sealed record TenantContext(Guid TenantId, Guid UserId, Guid MembershipId);

/// <summary>İsteğin aktif tenant bağlamı. Aktif tenant yoksa veya erişim kalktıysa <c>null</c>.</summary>
public interface ITenantContextAccessor
{
    TenantContext? Current { get; }
}
