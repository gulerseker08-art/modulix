using Modulix.Shared.Access;
using Modulix.Shared.Users;

namespace Modulix.Shared.Platform;

// Platform yönetimi (Y4) sözleşmeleri: /api/platform. Yalnızca platform yöneticisine açıktır.

public enum TenantState
{
    Active = 1,
    Closed = 2,

    /// <summary>Deneme: erişim açık (ADR-0028).</summary>
    Trial = 3,

    /// <summary>Askıda: erişim durur, veri silinmez (ADR-0028).</summary>
    Suspended = 4,
}

/// <summary>Modül hakkının kaynağı (ADR-0028).</summary>
public enum LicenseSource
{
    Manual = 1,
    Plan = 2,
    Trial = 3,
    Exception = 4,
}

public static class LicenseSourceText
{
    public static string Of(LicenseSource source) => source switch
    {
        LicenseSource.Manual => "Elle",
        LicenseSource.Plan => "Plan",
        LicenseSource.Trial => "Deneme",
        LicenseSource.Exception => "İstisna",
        _ => source.ToString(),
    };
}

/// <summary>
/// Firmanın bir modül hakkının koşulları (ADR-0028). <see cref="ValidUntil"/> bu dilimde yalnızca gösterilir; süre dolunca
/// hak kendiliğinden kapanmaz.
/// </summary>
public sealed record TenantEntitlementDto(string ModuleKey, bool IsEnabled, LicenseSource Source, DateTimeOffset? ValidUntil, string? Note);

/// <summary>Platform denetim kaydındaki bir satır (ADR-0028).</summary>
public sealed record PlatformAuditItemDto(
    Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorName, string Action, Guid? TargetTenantId, string? Reason, string? Details);

public sealed record PlatformAuditPageResponse(IReadOnlyList<PlatformAuditItemDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Askıya alma gerekçesi zorunludur; kapatma, yeniden açma ve etkinleştirmede isteğe bağlıdır.</summary>
public sealed record TenantReasonRequest(string? Reason = null);

public sealed record PlatformTenantDto(
    Guid Id,
    string Name,
    string Slug,
    TenantState Status,
    DateTimeOffset CreatedAt,
    int ActiveMemberCount,
    int AdministratorCount,
    IReadOnlyList<string> EnabledModules,
    string? ProfileKey = null,
    DateTimeOffset? ProfileAppliedAt = null);

public sealed record PlatformTenantPageResponse(IReadOnlyList<PlatformTenantDto> Items, int Page, int PageSize, int TotalCount);

public sealed record TenantAdministratorDto(Guid MembershipId, string DisplayName, string Email, UserStatus Status);

public sealed record PlatformTenantDetailResponse(
    PlatformTenantDto Tenant, IReadOnlyList<TenantAdministratorDto> Administrators, IReadOnlyList<TenantEntitlementDto>? Entitlements = null);

/// <summary>
/// Firma açma. <see cref="RoleTemplates"/>: oluşturulacak hazır rollerin anahtarları (seçilen modüllerden).
/// <see cref="Profile"/>: uygulanacak sektör profili (Y9c); alanları ve liste değerleri seçilen modüllerde eklenir.
/// </summary>
public sealed record CreateTenantRequest(
    string Name,
    string Slug,
    IReadOnlyList<string>? Modules,
    IReadOnlyList<string>? RoleTemplates,
    string AdministratorEmail,
    string AdministratorName,
    string? Profile = null,
    bool Trial = false);

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
/// <summary>
/// <see cref="Source"/>, <see cref="ValidUntil"/> ve <see cref="Note"/> yalnızca açarken geçerlidir (ADR-0028); boşsa mevcut
/// koşullar korunur (yeni hak: elle, süresiz). İstisna için not zorunludur; bitiş gelecekte olmalıdır.
/// </summary>
public sealed record SetTenantModuleRequest(bool Enabled, LicenseSource? Source = null, DateTimeOffset? ValidUntil = null, string? Note = null);

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
        TenantState.Trial => "Deneme",
        TenantState.Suspended => "Askıda",
        _ => state.ToString(),
    };
}

/// <summary>Telefonunu ve kurtarma kodlarını kaybeden kişinin iki adımlı doğrulamasını sıfırlar (Y7b).</summary>
public sealed record ResetTwoFactorRequest(string Email);
