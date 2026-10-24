namespace Modulix.Shared.Access;

// API ile arayüz (Modulix.Web) arasında paylaşılan modül, rol ve izin sözleşmeleri.

/// <summary>Kayıtlı bir modül ve aktif tenant için açık olup olmadığı.</summary>
public sealed record ModuleDto(string Key, string DisplayName, bool IsEnabled);

/// <summary>
/// Atanabilir izin. Rol ekranı izinleri modüle (<see cref="ModuleName"/>) ve kaynağa (<see cref="ResourceName"/>, ör.
/// "Firmalar") göre gruplar; aynı kaynağın okuma ve yazma izinleri matriste aynı satırdadır.
/// </summary>
public sealed record PermissionDto(string Key, string DisplayName, string ModuleKey, string ModuleName = "", string ResourceName = "");

/// <summary>
/// Oturumdaki kullanıcının aktif tenant'taki erişimi; arayüz menüleri buna göre gösterir.
/// <see cref="IsAdministrator"/>: Yönetici (sistem) rolü var; yönetici rolünü vermek gibi işlemler yalnızca ona açıktır.
/// </summary>
public sealed record MyAccessResponse(
    IReadOnlyList<string> EnabledModules, IReadOnlyList<string> Permissions, bool IsAdministrator = false, RecordScope Scope = RecordScope.All);

/// <summary>
/// Rolün kayıt kapsamı (Y8): izinler "hangi tür kaydı", kapsam "hangi kayıtları" söyler. Sorumlusu olan kayıtlara
/// uygulanır; birden fazla rolde en geniş kapsam geçerlidir. Değerler sunucudakiyle aynıdır.
/// </summary>
public enum RecordScope
{
    All = 1,
    Team = 2,
    Own = 3,
}

public static class RecordScopeLabels
{
    public static string Of(RecordScope scope) => scope switch
    {
        RecordScope.All => "Tüm kayıtlar",
        RecordScope.Team => "Ekibinin kayıtları",
        RecordScope.Own => "Yalnızca kendi kayıtları",
        _ => scope.ToString(),
    };

    public static string Describe(RecordScope scope) => scope switch
    {
        RecordScope.All => "Firmadaki bütün kayıtlar.",
        RecordScope.Team => "Kendisinin ve ekip arkadaşlarının sorumlu olduğu kayıtlar.",
        RecordScope.Own => "Yalnızca sorumlusu kendisi olan kayıtlar.",
        _ => string.Empty,
    };
}

/// <summary>
/// Sistem rolünde (<see cref="IsSystem"/>, "Yönetici") izin listesi boştur: rol tüm izinleri verir ve düzenlenemez.
/// <see cref="MemberCount"/> rol listesinde doldurulur.
/// </summary>
public sealed record RoleDto(
    Guid Id, string Name, IReadOnlyList<string> Permissions, bool IsSystem = false, int MemberCount = 0, RecordScope Scope = RecordScope.All);

/// <summary>Hazır rol önerisi; "Şablondan başla" ile yeni rol formunu doldurur.</summary>
public sealed record RoleTemplateDto(
    string Key, string Name, string Description, IReadOnlyList<string> Permissions, RecordScope Scope = RecordScope.All);

/// <summary><see cref="Scope"/> verilmezse yeni rol tüm kayıtları görür, mevcut rolün kapsamı değişmez.</summary>
public sealed record SaveRoleRequest(string Name, IReadOnlyList<string> Permissions, RecordScope? Scope = null);

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
