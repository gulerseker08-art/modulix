namespace Modulix.Shared.Access;

// API ile arayüz (Modulix.Web) arasında paylaşılan modül, rol ve izin sözleşmeleri.

/// <summary>Kayıtlı bir modül ve aktif tenant için açık olup olmadığı.</summary>
public sealed record ModuleDto(string Key, string DisplayName, bool IsEnabled);

public sealed record PermissionDto(string Key, string DisplayName, string ModuleKey);

/// <summary>Oturumdaki kullanıcının aktif tenant'taki erişimi; arayüz menüleri buna göre gösterir.</summary>
public sealed record MyAccessResponse(IReadOnlyList<string> EnabledModules, IReadOnlyList<string> Permissions);

public sealed record RoleDto(Guid Id, string Name, IReadOnlyList<string> Permissions);

public sealed record SaveRoleRequest(string Name, IReadOnlyList<string> Permissions);

public sealed record SetMembershipRolesRequest(IReadOnlyList<Guid> RoleIds);
