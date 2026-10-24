namespace Modulix.Shared.Access;

// API ile arayüz (Modulix.Web) arasında paylaşılan modül, rol ve izin sözleşmeleri.

/// <summary>Kayıtlı bir modül ve aktif tenant için açık olup olmadığı.</summary>
public sealed record ModuleDto(string Key, string DisplayName, bool IsEnabled);

public sealed record PermissionDto(string Key, string DisplayName, string ModuleKey);

/// <summary>
/// Oturumdaki kullanıcının aktif tenant'taki erişimi; arayüz menüleri buna göre gösterir.
/// <see cref="IsAdministrator"/>: Yönetici (sistem) rolü var; yönetici rolünü vermek gibi işlemler yalnızca ona açıktır.
/// </summary>
public sealed record MyAccessResponse(IReadOnlyList<string> EnabledModules, IReadOnlyList<string> Permissions, bool IsAdministrator = false);

/// <summary>Sistem rolünde (<see cref="IsSystem"/>, "Yönetici") izin listesi boştur: rol tüm izinleri verir ve düzenlenemez.</summary>
public sealed record RoleDto(Guid Id, string Name, IReadOnlyList<string> Permissions, bool IsSystem = false);

public sealed record SaveRoleRequest(string Name, IReadOnlyList<string> Permissions);

public sealed record SetMembershipRolesRequest(IReadOnlyList<Guid> RoleIds);

/// <summary>Çekirdek (yönetim) izinleri; arayüz menüsü bunlara bakar. <c>CoreModule.Permissions</c> ile aynıdır (test denetler).</summary>
public static class CoreAccess
{
    public const string ModuleKey = "core";

    public const string ManageUsers = "core.users.manage";
    public const string ManageRoles = "core.roles.manage";
    public const string ManageSettings = "core.settings.manage";
    public const string ReadAudit = "core.audit.read";

    /// <summary>Bunlardan biri olan kullanıcı menüde "Yönetim" bölümünü görür.</summary>
    public static IReadOnlyList<string> AdministrationPermissions { get; } = [ManageUsers, ManageRoles, ManageSettings, ReadAudit];
}
