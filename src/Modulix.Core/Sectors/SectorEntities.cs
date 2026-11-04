using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Sectors;

/// <summary>
/// Sektör/faaliyet katalogunun düğümü (ADR-0029). Platform geneli kayıttır (tenant verisi değil, RLS dışı); platform yöneticisi
/// yönetir. Silinmez, pasife alınır. Üst düğüm değişikliği (taşıma) bu aşamada yoktur: kullanım sayısı tenant sınırı yüzünden
/// platformdan okunamadığı için etki analizi sonraki dilimdedir.
/// </summary>
public sealed class ActivityNode
{
    public const int DescriptionMaxLength = 500;

    private ActivityNode()
    {
        // EF Core için
    }

    public ActivityNode(string code, Guid? parentId, ActivityNodeKind kind, string name, string? description = null, int sortOrder = 0)
    {
        if (!ActivityTreeRules.IsValidCode(code))
        {
            throw new ArgumentException($"Geçersiz faaliyet kodu: '{code}'.", nameof(code));
        }

        if ((kind == ActivityNodeKind.Sector) != (parentId is null))
        {
            throw new ArgumentException("Yalnızca sektör kök olabilir; diğer düğümler üst düğüm ister.", nameof(parentId));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        ParentId = parentId;
        Kind = kind;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
        Describe(name, description);
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }

    /// <summary>Sabit kod; ad değişse de değişmez, referanslar Id/Code iledir.</summary>
    public string Code { get; private set; } = string.Empty;

    public Guid? ParentId { get; private set; }

    public ActivityNodeKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    public ModuleReadiness Readiness { get; private set; }

    /// <summary>Eşzamanlı yönetici düzenlemesinde sessiz ezmeyi önleyen iyimser kilit.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Describe(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > ActivityTreeRules.NameMaxLength)
        {
            throw new ArgumentException("Faaliyet adı boş olamaz ve en çok 100 karakter olabilir.", nameof(name));
        }

        if (description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException("Açıklama en çok 500 karakter olabilir.", nameof(description));
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Touch();
    }

    public void Reorder(int sortOrder)
    {
        SortOrder = sortOrder;
        Touch();
    }

    public void SetActive(bool active)
    {
        IsActive = active;
        Touch();
    }

    public void SetReadiness(ModuleReadiness readiness)
    {
        Readiness = readiness;
        Touch();
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Uygulamanın desteklediği bir işlev (ör. teklif şablonu, hizmet profili). Platform kataloğudur; ağaçtan ayrıdır.</summary>
public sealed class Capability
{
    public const int CodeMaxLength = 100;

    private Capability()
    {
        // EF Core için
    }

    public Capability(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > CodeMaxLength || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            throw new ArgumentException($"Geçersiz yetenek kodu: '{code}'.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > ActivityTreeRules.NameMaxLength)
        {
            throw new ArgumentException("Yetenek adı boş olamaz ve en çok 100 karakter olabilir.", nameof(name));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Name = name.Trim();
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }
}

/// <summary>Yetenek bağımlılığı: <see cref="CapabilityId"/>, <see cref="DependsOnId"/> olmadan çalışmaz. Döngüyü yayın öncesi <see cref="CapabilityGraphRules"/> yakalar.</summary>
public sealed class CapabilityDependency
{
    private CapabilityDependency()
    {
        // EF Core için
    }

    public CapabilityDependency(Guid capabilityId, Guid dependsOnId)
    {
        if (capabilityId == dependsOnId)
        {
            throw new ArgumentException("Yetenek kendine bağlanamaz.", nameof(dependsOnId));
        }

        CapabilityId = capabilityId;
        DependsOnId = dependsOnId;
    }

    public Guid CapabilityId { get; private set; }

    public Guid DependsOnId { get; private set; }
}

/// <summary>Faaliyetin gerektirdiği yetenek.</summary>
public sealed class ActivityNodeCapability
{
    private ActivityNodeCapability()
    {
        // EF Core için
    }

    public ActivityNodeCapability(Guid nodeId, Guid capabilityId)
    {
        NodeId = nodeId;
        CapabilityId = capabilityId;
    }

    public Guid NodeId { get; private set; }

    public Guid CapabilityId { get; private set; }
}

/// <summary>
/// Tenant'ın açıkça seçtiği faaliyet (ADR-0029 K4). Üst bağlam saklanmaz, seçimden türetilir; üst başlığı seçmek alt faaliyeti
/// seçmiş saymaz. Tenant verisidir; Row-Level Security ile korunur. Kayıt silinir (seçimin geri alınması), katalog düğümü silinmez.
/// </summary>
public sealed class TenantActivitySelection : ITenantEntity, ICompanyShared
{
    private TenantActivitySelection()
    {
        // EF Core için
    }

    public TenantActivitySelection(Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Faaliyet kimliği boş olamaz.", nameof(nodeId));
        }

        Id = Guid.CreateVersion7();
        NodeId = nodeId;
        SelectedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid NodeId { get; private set; }

    public DateTimeOffset SelectedAt { get; private set; }
}
