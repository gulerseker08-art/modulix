using Modulix.Shared.Access;
using Modulix.Shared.Users;

namespace Modulix.Shared.Platform;

// Platform yönetimi (Y4) sözleşmeleri: /api/platform. Yalnızca platform yöneticisine açıktır.

public enum TenantState
{
    Active = 1,
    Closed = 2,
}

public sealed record PlatformTenantDto(
    Guid Id,
    string Name,
    string Slug,
    TenantState Status,
    DateTimeOffset CreatedAt,
    int ActiveMemberCount,
    int AdministratorCount,
    IReadOnlyList<string> EnabledModules);

public sealed record PlatformTenantPageResponse(IReadOnlyList<PlatformTenantDto> Items, int Page, int PageSize, int TotalCount);

public sealed record TenantAdministratorDto(Guid MembershipId, string DisplayName, string Email, UserStatus Status);

public sealed record PlatformTenantDetailResponse(PlatformTenantDto Tenant, IReadOnlyList<TenantAdministratorDto> Administrators);

/// <summary>Firma açma. <see cref="RoleTemplates"/>: oluşturulacak hazır rollerin anahtarları (seçilen modüllerden).</summary>
public sealed record CreateTenantRequest(
    string Name,
    string Slug,
    IReadOnlyList<string>? Modules,
    IReadOnlyList<string>? RoleTemplates,
    string AdministratorEmail,
    string AdministratorName);

/// <summary><see cref="ExistingAccount"/>: yöneticinin hesabı vardı; davet yerine bilgi e-postası gitti.</summary>
public sealed record CreateTenantResponse(PlatformTenantDetailResponse Tenant, bool ExistingAccount);

public sealed record RenameTenantRequest(string Name);

public sealed record InviteTenantAdministratorRequest(string Email, string DisplayName);

public sealed record InviteTenantAdministratorResponse(TenantAdministratorDto Administrator, bool ExistingAccount);

/// <summary>Firma açma formu için modül ve hazır rolleri.</summary>
public sealed record PlatformModuleDto(string Key, string DisplayName, IReadOnlyList<RoleTemplateDto> Templates);

/// <summary>
/// <see cref="ReadOnlyTemplate"/>: her firmada seçilebilen "Salt okur" rolü; izinleri açılan modüllere göre oluşur
/// (burada boştur).
/// </summary>
public sealed record PlatformModulesResponse(IReadOnlyList<PlatformModuleDto> Modules, RoleTemplateDto ReadOnlyTemplate);

/// <summary>Firmada modül açma veya kapatma (Y4b). Veri silinmez; değişiklik firmanın bir sonraki isteğinde geçerlidir.</summary>
public sealed record SetTenantModuleRequest(bool Enabled);

/// <summary>
/// Katalogdaki bir modül (salt okunur). <see cref="EnabledTenantCount"/>: modülün açık olduğu açık firma sayısı;
/// çekirdek her firmada açık olduğu için boştur.
/// </summary>
public sealed record CatalogModuleDto(
    string Key,
    string DisplayName,
    bool IsAlwaysEnabled,
    int? EnabledTenantCount,
    IReadOnlyList<PermissionDto> Permissions,
    IReadOnlyList<RoleTemplateDto> Templates);

public static class TenantStateText
{
    public static string Of(TenantState state) => state switch
    {
        TenantState.Active => "Açık",
        TenantState.Closed => "Kapalı",
        _ => state.ToString(),
    };
}
