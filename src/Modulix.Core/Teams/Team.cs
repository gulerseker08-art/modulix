using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Teams;

/// <summary>
/// Firmanın ekibi, yani çalışma grubu (Y8, ör. "İstanbul Satış"). Örgüt yapısındaki departman değildir (ADR-0026). "Ekibimin kayıtları" kapsamı ekip arkadaşlarının sorumlu
/// olduğu kayıtları açar. Bir kişi birden fazla ekipte olabilir. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class Team : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private Team()
    {
        // EF Core için
    }

    public Team(string name, string? description)
    {
        Id = Guid.CreateVersion7();
        Rename(name);
        Describe(description);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Ekip adı zorunludur.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Ekip adı en fazla {NameMaxLength} karakter olabilir.", nameof(name));
        }

        Name = trimmed;
    }

    public void Describe(string? description)
    {
        var trimmed = description?.Trim();
        if (trimmed?.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Açıklama en fazla {DescriptionMaxLength} karakter olabilir.", nameof(description));
        }

        Description = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Ekip üyeliği: üyelik (firma × kişi) bir ekipte. Tenant verisidir; Row-Level Security ile korunur.</summary>
public sealed class TeamMember : ITenantEntity, ICompanyShared
{
    private TeamMember()
    {
        // EF Core için
    }

    public TeamMember(Guid teamId, Guid membershipId)
    {
        Id = Guid.CreateVersion7();
        TeamId = teamId;
        MembershipId = membershipId;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid MembershipId { get; private set; }
}
