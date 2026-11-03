using Modulix.Core.Contracts.Authorization;

namespace Modulix.Core.Authorization;

/// <summary>
/// Rol özeti. Sistem rolünde (<see cref="IsSystem"/>) <see cref="Permissions"/> boştur: rol, tenant için açık
/// tüm modüllerin tüm izinlerini verir. <see cref="MemberCount"/> yalnızca listede doldurulur.
/// </summary>
public sealed record RoleSummary(Guid Id, string Name, IReadOnlyList<string> Permissions, bool IsSystem = false, int MemberCount = 0)
{
    /// <summary>Kayıt kapsamı (Y8).</summary>
    public RecordScope Scope { get; init; } = RecordScope.All;

    /// <summary>Rolün kullanıcıya, departmana ya da ikisine atanabilmesi (ADR-0026).</summary>
    public RoleAssignLevel AssignLevel { get; init; } = RoleAssignLevel.User;

    /// <summary>Rolün atandığı departman sayısı; yalnızca listede doldurulur.</summary>
    public int UnitCount { get; init; }

    /// <summary>Kapsam dışı ortak firma/kişiler gizlenir mi (ADR-0026 K8).</summary>
    public bool HideOutOfScopeShared { get; init; }
}

/// <summary>Departmana atanmış rol (ADR-0026 K6).</summary>
public sealed record BusinessUnitRoleAssignment(Guid RoleId, string RoleName, RecordScope Scope, bool InheritToChildren);

/// <summary>Departmana atanacak rol ve alt departmanlara inip inmeyeceği.</summary>
public sealed record BusinessUnitRoleRequest(Guid RoleId, bool InheritToChildren = false);

/// <summary>
/// İstek kurallara uygun ama tenant'ın durumuyla çelişiyor: sistem rolünü düzenlemek veya son yöneticinin
/// yönetici rolünü kaldırmak gibi. API 409 döner.
/// </summary>
public sealed class RoleConflictException(string message) : Exception(message);

/// <summary>
/// Aktif tenant'ın rolleri ve üyelik rol atamaları. Aktif tenant yoksa yazma işlemleri
/// <see cref="InvalidOperationException"/> fırlatır. Geçersiz ad, katalogda olmayan izin veya
/// tenant'ta bulunmayan rol için <see cref="ArgumentException"/>, sistem rolünü değiştirme veya
/// son yöneticiyi kaldırma için <see cref="RoleConflictException"/> fırlatır.
/// Rol ve atama değişiklikleri denetim kaydına yazılır.
///
/// <c>actingMembershipId</c> verilirse (kullanıcı isteği) yetki yükseltmeye karşı kural uygulanır: yönetici olmayan kişi
/// bir role yalnızca kendisinde olan izni ekleyebilir veya rolden çıkarabilir; değilse
/// <see cref="Platform.UserAdministrationException"/> (<see cref="Platform.UserAdministrationError.PermissionDenied"/>).
/// Aynı kural kapsamda da geçerlidir: yönetici olmayan kişi bir role kendi kapsamından geniş kapsam veremez.
/// </summary>
public interface IRoleService
{
    /// <summary>Ada göre sıralı; üye sayılarıyla.</summary>
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken);

    Task<RoleSummary> CreateAsync(
        string name,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken,
        Guid? actingMembershipId = null,
        RecordScope scope = RecordScope.All,
        RoleAssignLevel? assignLevel = null,
        bool? hideOutOfScopeShared = null);

    /// <summary>
    /// Rol bu tenant'ta yoksa <c>null</c>. <paramref name="scope"/> ve <paramref name="assignLevel"/> verilmezse değişmez.
    /// Departmana atanmış bir rolün atanabilir düzeyi "yalnızca kullanıcıya" yapılamaz (önce departman atamaları kaldırılır).
    /// </summary>
    Task<RoleSummary?> UpdateAsync(
        Guid roleId,
        string name,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken,
        Guid? actingMembershipId = null,
        RecordScope? scope = null,
        RoleAssignLevel? assignLevel = null,
        bool? hideOutOfScopeShared = null);

    /// <summary>
    /// Rolü siler; rol bu tenant'ta yoksa <c>false</c>. Sistem rolü silinemez (<see cref="RoleConflictException"/>).
    /// Rolün üyesi varsa <paramref name="moveToRoleId"/> gerekir (yoksa <see cref="RoleConflictException"/>): üyeler o
    /// role taşınır, taşıma ve silme tek işlemdedir. Üye taşımak rol atamasıdır; <c>actingMembershipId</c> verilirse
    /// işlemi yapanın kullanıcı yönetme izni olmalı, hedef yönetici rolüyse kendisi yönetici olmalıdır.
    /// </summary>
    Task<bool> DeleteAsync(Guid roleId, Guid? moveToRoleId, CancellationToken cancellationToken, Guid? actingMembershipId = null);

    /// <summary>
    /// Tenant'ın sistem rolünü ("Yönetici") döner; yoksa oluşturur. Aynı adlı eski bir rol varsa sistem rolüne çevrilir.
    /// </summary>
    Task<RoleSummary> EnsureSystemRoleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliğin rollerini verilen listeyle değiştirir. Üyelik aktif tenant'a ait değilse <c>false</c>.
    /// Değişiklik tenant'ı etkin yöneticisiz bırakacaksa <see cref="RoleConflictException"/>.
    /// <paramref name="actingMembershipId"/> verilirse (kullanıcı isteği) yönetici kuralı da uygulanır: yönetici rolünü
    /// vermek veya bir yöneticinin rollerini değiştirmek için işlemi yapan da yönetici olmalıdır; değilse
    /// <see cref="Platform.UserAdministrationException"/>. Kural yönetici kilidi altında denetlenir.
    /// </summary>
    Task<bool> SetMembershipRolesAsync(
        Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken, Guid? actingMembershipId = null);

    /// <summary>Üyeliğin <b>doğrudan</b> rolleri; üyelik aktif tenant'a ait değilse <c>null</c>. Departmandan gelenler dahil değildir.</summary>
    Task<IReadOnlyList<RoleSummary>?> GetMembershipRolesAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>Departmana atanmış roller (role göre sıralı); departman bu tenant'ta yoksa <c>null</c>.</summary>
    Task<IReadOnlyList<BusinessUnitRoleAssignment>?> GetBusinessUnitRolesAsync(Guid businessUnitId, CancellationToken cancellationToken);

    /// <summary>
    /// Departmanın rollerini verilen listeyle değiştirir (ADR-0026 K6); departman yoksa <c>null</c>. Kurallar: sistem rolü
    /// ("Yönetici") departmana atanamaz; rolün atanabilir düzeyi departmanı içermelidir; departman etkin olmalıdır (rol
    /// eklenirken); <paramref name="actingMembershipId"/> verilirse işlemi yapan, rolün izinlerini ve kapsamını kendisi de
    /// taşımalıdır (yönetici hariç). Aksi halde <see cref="RoleConflictException"/>, <see cref="ArgumentException"/> ya da
    /// <see cref="Platform.UserAdministrationException"/>. Değişiklik, etkilenen kullanıcı sayısıyla denetim kaydına yazılır.
    /// </summary>
    Task<IReadOnlyList<BusinessUnitRoleAssignment>?> SetBusinessUnitRolesAsync(
        Guid businessUnitId,
        IReadOnlyCollection<BusinessUnitRoleRequest> roles,
        CancellationToken cancellationToken,
        Guid? actingMembershipId = null);
}
