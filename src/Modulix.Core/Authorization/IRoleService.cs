namespace Modulix.Core.Authorization;

/// <summary>
/// Rol özeti. Sistem rolünde (<see cref="IsSystem"/>) <see cref="Permissions"/> boştur: rol, tenant için açık
/// tüm modüllerin tüm izinlerini verir.
/// </summary>
public sealed record RoleSummary(Guid Id, string Name, IReadOnlyList<string> Permissions, bool IsSystem = false);

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
/// </summary>
public interface IRoleService
{
    /// <summary>Ada göre sıralı.</summary>
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken);

    Task<RoleSummary> CreateAsync(string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);

    /// <summary>Rol bu tenant'ta yoksa <c>null</c>.</summary>
    Task<RoleSummary?> UpdateAsync(Guid roleId, string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);

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

    /// <summary>Üyeliğin rolleri; üyelik aktif tenant'a ait değilse <c>null</c>.</summary>
    Task<IReadOnlyList<RoleSummary>?> GetMembershipRolesAsync(Guid membershipId, CancellationToken cancellationToken);
}
