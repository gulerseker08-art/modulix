using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Warehouse.Domain;

/// <summary>
/// Depo tablolarının ortak tabanı. Tenant verisidir (<see cref="ITenantEntity"/>): sorgu filtresi ve Row-Level Security uygulanır.
/// Depo kayıtları şirketler arasında ortaktır (<see cref="ICompanyShared"/>, ADR-0008); şirket kapsamına geçiş ayrı bir karardır.
/// </summary>
public abstract class WarehouseEntity : ITenantEntity, ICompanyShared
{
    protected WarehouseEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>
/// Tesis: depo ya da antrepo işletilen yer (ADR-0032). Alanlar, lokasyon seviyeleri ve lokasyon ağacı tesise bağlanır (sonraki
/// dilimler). <see cref="CustomsEnabled"/>, tesiste gümrük uzantısının (<c>warehouse.customs.*</c>) kullanılıp kullanılmayacağını
/// söyler; kapalı tesiste gümrük alanları zorunlu değildir (K1). Yasal kurallar tesise değil rejim profiline bağlıdır (K16).
/// </summary>
public sealed class Facility : WarehouseEntity
{
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int CountryCodeLength = 2;

    /// <summary>Tenant içinde benzersiz kısa kod (ör. "ANT"); lokasyon kodlarının ilk bölümü olabilir.</summary>
    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Tesisin bulunduğu ülke (ISO 3166-1 alfa-2, büyük harf); mevzuat profili seçiminde kullanılır. Boş olabilir.</summary>
    public string? CountryCode { get; internal set; }

    /// <summary>Gümrük uzantısı bu tesiste açık mı? Varsayılan kapalı.</summary>
    public bool CustomsEnabled { get; internal set; }

    /// <summary>Pasif tesis yeni işlem almaz; geçmiş kayıtları korunur.</summary>
    public bool IsActive { get; internal set; } = true;
}
