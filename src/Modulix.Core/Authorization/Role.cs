using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Authorization;

/// <summary>
/// Tenant'a özel rol: bir ad ve izin listesi (Görev E4). Kullanıcıya üyeliği üzerinden atanır
/// (<see cref="MembershipRole"/>); aynı kullanıcının farklı tenant'larda farklı rolleri olur.
/// Tenant verisidir; Row-Level Security ile korunur.
///
/// Her tenant'ta bir sistem rolü ("Yönetici", <see cref="IsSystem"/>) bulunur: izin listesi tutmaz, tenant için
/// açık tüm modüllerin tüm izinlerini verir; adı ve izinleri değiştirilemez. Son yöneticinin bu rolü kaldırılamaz.
/// </summary>
public sealed class Role : ITenantEntity
{
    public const int NameMaxLength = 100;

    public const string SystemRoleName = "Yönetici";

    private Role()
    {
        // EF Core için
    }

    public Role(string name, IEnumerable<string> permissions)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        SetPermissions(permissions);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Tenant'ın yönetici rolünü oluşturur.</summary>
    public static Role CreateSystem()
    {
        var role = new Role(SystemRoleName, []);
        role.IsSystem = true;
        return role;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>İzin anahtarları; tekrarsız ve sıralı.</summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Tenant'ın yönetici rolü: tüm izinleri verir, düzenlenemez.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>
    /// Aynı adlı eski bir rolü sistem rolüne çevirir (sistem rolü öncesi oluşturulmuş "Yönetici" rolleri için).
    /// İzin listesi boşaltılır; sistem rolü izinlerini katalogdan alır.
    /// </summary>
    public void MarkAsSystem()
    {
        IsSystem = true;
        Permissions = [];
    }

    public void Rename(string name)
    {
        EnsureEditable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Rol adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }

    /// <summary>
    /// Yalnızca biçimi denetler. İznin katalogda olup olmadığını <see cref="IRoleService"/> denetler;
    /// katalogdan kalkmış bir izin rolde kalsa bile geçerli sayılmaz (<see cref="ModuleCatalog.EffectivePermissions"/>).
    /// </summary>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        EnsureEditable();
        ArgumentNullException.ThrowIfNull(permissions);
        var normalized = permissions
            .Select(p => p?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var invalid = normalized.Where(p => !AccessKeys.IsValidPermissionKey(p)).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException($"Geçersiz izin anahtarı: {string.Join(", ", invalid)}.", nameof(permissions));
        }

        Permissions = normalized;
    }

    private void EnsureEditable()
    {
        if (IsSystem)
        {
            throw new RoleConflictException($"'{Name}' sistem rolüdür; adı ve izinleri değiştirilemez.");
        }
    }
}
