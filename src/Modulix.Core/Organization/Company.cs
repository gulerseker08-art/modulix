using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Organization;

/// <summary>
/// Tenant'ın işletici şirketi (ADR-0008): belge kesen, hesap tutan tüzel kişi. Müşteri firma (Taraf) değildir. Her tenant'ın
/// en az bir şirketi vardır; tenant açılırken tenant adıyla varsayılan şirket oluşur. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class Company : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 200;

    private Company()
    {
        // EF Core için
    }

    public Company(string name, bool isDefault)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        IsDefault = isDefault;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Tenant'ın varsayılan şirketi; tenant başına tektir ve silinemez.</summary>
    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Şirket adı zorunludur.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Şirket adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }
}

/// <summary>Şirkete bağlı şube veya lokasyon (ADR-0008). Kapsam sınırı değil, idari birimdir. Tenant verisidir; Row-Level Security ile korunur.</summary>
public sealed class Branch : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 200;

    private Branch()
    {
        // EF Core için
    }

    public Branch(Guid companyId, string name)
    {
        Id = Guid.CreateVersion7();
        CompanyId = companyId;
        Rename(name);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Şube adı zorunludur.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Şube adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }
}
