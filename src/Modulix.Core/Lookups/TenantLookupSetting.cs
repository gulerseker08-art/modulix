using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Lookups;

/// <summary>
/// Firmanın bir lookup için yaptığı özelleştirme (ADR-0016). Satır yoksa ya da <see cref="Searchable"/> boşsa kodda tanımlı
/// varsayılan geçerlidir. "Varsayılana dön" satırı silmez, değeri boşaltır: sürüm hiç geri sarmaz, eşzamanlı düzenleme
/// sessizce ezilmez. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class TenantLookupSetting : ITenantEntity, ICompanyShared
{
    public const int LookupKeyMaxLength = 100;

    private TenantLookupSetting()
    {
        // EF Core için
    }

    public TenantLookupSetting(string lookupKey)
    {
        Id = Guid.CreateVersion7();
        LookupKey = lookupKey;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string LookupKey { get; private set; } = string.Empty;

    /// <summary>Firmanın seçimi; null ise lookup tanımının varsayılanı geçerlidir.</summary>
    public bool? Searchable { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı düzenleme denetimi içindir.</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Set(bool? searchable, Guid? userId)
    {
        Searchable = searchable;
        UpdatedBy = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}
