using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Authorization;

/// <summary>
/// Tenant'a özel rol: bir ad ve izin listesi (Görev E4). Kullanıcıya üyeliği üzerinden atanır
/// (<see cref="MembershipRole"/>); aynı kullanıcının farklı tenant'larda farklı rolleri olur.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class Role : ITenantEntity
{
    public const int NameMaxLength = 100;

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

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>İzin anahtarları; tekrarsız ve sıralı.</summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
    {
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
}
