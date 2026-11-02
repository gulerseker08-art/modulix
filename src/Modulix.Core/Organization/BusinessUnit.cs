using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Organization;

/// <summary>
/// Departman türü (ADR-0026). Yalnızca açıklayıcı metadata'dır; kodda tür koşulu yazılmaz, davranış ağaçtaki yerden ve
/// rollerden gelir.
/// </summary>
public static class BusinessUnitKinds
{
    public const string Center = "Center";
    public const string Dealer = "Dealer";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Center, Dealer, Other];

    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// Tenant içi departman ağacının bir düğümü (ADR-0026). Kök departman merkezdir (tenant başına tek); bayiler ve diğer
/// yapılar onun altındadır. "Departman ve altı" kapsamı <see cref="Path"/> üzerinden hesaplanır. Fiziksel silinmez,
/// kapatılır. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class BusinessUnit : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int PathMaxLength = 400;

    /// <summary>Kökten en derin düğüme kadar dört seviye (0..3). Ürün ilk fazda iki seviye sunar.</summary>
    public const int MaxDepth = 3;

    private BusinessUnit()
    {
        // EF Core için
    }

    /// <summary>Tenant'ın kök departmanı (merkez).</summary>
    public static BusinessUnit CreateRoot(string name)
    {
        var unit = new BusinessUnit
        {
            Id = Guid.CreateVersion7(),
            Kind = BusinessUnitKinds.Center,
            Depth = 0,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        unit.Rename(name);
        unit.Path = PathOf(parentPath: null, unit.Id);
        return unit;
    }

    public static BusinessUnit CreateChild(BusinessUnit parent, string name, string? code, string kind)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!parent.IsActive)
        {
            throw new ArgumentException("Kapalı bir departmanın altına departman eklenemez.", nameof(parent));
        }

        if (parent.Depth >= MaxDepth)
        {
            throw new ArgumentException($"Departman ağacı en fazla {MaxDepth + 1} seviye olabilir.", nameof(parent));
        }

        var unit = new BusinessUnit
        {
            Id = Guid.CreateVersion7(),
            ParentId = parent.Id,
            Depth = parent.Depth + 1,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        unit.Rename(name);
        unit.SetCode(code);
        unit.SetKind(kind);
        unit.Path = PathOf(parent.Path, unit.Id);
        return unit;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Üst departman; kökte boştur.</summary>
    public Guid? ParentId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Code { get; private set; }

    /// <summary><see cref="BusinessUnitKinds"/> anahtarı.</summary>
    public string Kind { get; private set; } = BusinessUnitKinds.Other;

    /// <summary>Kökten kendine kimlik yolu (<c>/kök/…/kendisi/</c>); "ve altı" sorguları bu önekle yapılır.</summary>
    public string Path { get; private set; } = string.Empty;

    public int Depth { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsRoot => ParentId is null;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Departman adı zorunludur.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Departman adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }

    public void SetCode(string? code)
    {
        var trimmed = code?.Trim();
        if (trimmed?.Length > CodeMaxLength)
        {
            throw new ArgumentException($"Kod en fazla {CodeMaxLength} karakter olabilir.", nameof(code));
        }

        Code = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Kök her zaman merkezdir; kök dışındaki departman merkez olamaz.</summary>
    public void SetKind(string kind)
    {
        if (!BusinessUnitKinds.IsValid(kind))
        {
            throw new ArgumentException("Geçersiz departman türü.", nameof(kind));
        }

        if (IsRoot ? kind != BusinessUnitKinds.Center : kind == BusinessUnitKinds.Center)
        {
            throw new ArgumentException(
                IsRoot ? "Kök departmanın türü Merkez'dir." : "Merkez türü yalnızca kök departman içindir.", nameof(kind));
        }

        Kind = kind;
    }

    public void Close()
    {
        if (IsRoot)
        {
            throw new ArgumentException("Kök departman kapatılamaz.", nameof(IsActive));
        }

        IsActive = false;
    }

    public void Reopen() => IsActive = true;

    private static string PathOf(string? parentPath, Guid id) => $"{parentPath ?? "/"}{id:N}/";
}

/// <summary>
/// Kullanıcının (üyeliğin) departmanı (ADR-0026 K4): her üyelik tam bir departmana bağlıdır. Tenant verisidir;
/// Row-Level Security ile korunur.
/// </summary>
public sealed class BusinessUnitMember : ITenantEntity, ICompanyShared
{
    private BusinessUnitMember()
    {
        // EF Core için
    }

    public BusinessUnitMember(Guid businessUnitId, Guid membershipId)
    {
        if (businessUnitId == Guid.Empty)
        {
            throw new ArgumentException("BusinessUnitId boş olamaz.", nameof(businessUnitId));
        }

        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("MembershipId boş olamaz.", nameof(membershipId));
        }

        Id = Guid.CreateVersion7();
        BusinessUnitId = businessUnitId;
        MembershipId = membershipId;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid BusinessUnitId { get; private set; }

    public Guid MembershipId { get; private set; }

    public void MoveTo(Guid businessUnitId)
    {
        if (businessUnitId == Guid.Empty)
        {
            throw new ArgumentException("BusinessUnitId boş olamaz.", nameof(businessUnitId));
        }

        BusinessUnitId = businessUnitId;
    }
}
