namespace Modulix.Core.Contracts.Authorization;

/// <summary>
/// Rolün hangi düzeyde atanabileceği (ADR-0026 K6). Mevcut roller <see cref="User"/> olarak başlar; departmana atama için
/// yönetici rolü açıkça <see cref="Unit"/> ya da <see cref="Both"/> yapar. Sistem rolü ("Yönetici") her zaman <see cref="User"/>'dır.
/// </summary>
public enum RoleAssignLevel
{
    /// <summary>Yalnızca kullanıcıya (üyeliğe) atanır.</summary>
    User = 1,

    /// <summary>Yalnızca departmana atanır.</summary>
    Unit = 2,

    /// <summary>Hem kullanıcıya hem departmana atanır.</summary>
    Both = 3,
}
