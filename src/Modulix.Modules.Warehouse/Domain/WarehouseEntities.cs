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

    /// <summary>
    /// Aynı tesiste serbest ve gümrük denetimli alanlar birlikte bulunabilir mi? Varsayılan kapalı (K12): hukuki doğrulama olmadan
    /// açılması önerilmez; ileride rejim profilindeki izinle birlikte değerlendirilir.
    /// </summary>
    public bool AllowMixedZones { get; internal set; }

    /// <summary>Pasif tesis yeni işlem almaz; geçmiş kayıtları korunur.</summary>
    public bool IsActive { get; internal set; } = true;
}

/// <summary>
/// Tesisin lokasyon kodundaki bir seviyenin tanımı (K11, ADR'deki <c>LocationLevelDefinition</c>). Seviyeler sabit sütun değildir:
/// her tesis kendi sırasını, adlarını, uzunluklarını ve ayraçlarını tanımlar (ör. <c>ANT-A-02-04-C</c> için Tesis, Koridor, Raf, Kat,
/// Göz; başka tesis üç ya da altı seviye kurabilir). Lokasyon kodu bu tanımdan üretilir ve doğrulanır (sonraki dilim). Kullanımdaki
/// tesiste tanım yalnızca eklenir ve yeniden adlandırılır; bu denetim lokasyonlar gelince eklenir.
/// </summary>
public sealed class LocationLevel : WarehouseEntity
{
    public const int KeyMaxLength = 30;
    public const int NameMaxLength = 100;
    public const int SeparatorMaxLength = 3;
    public const int MaxPosition = 10;
    public const int MaxValueLength = 20;

    /// <summary>Seviye değerinde izin verilen karakterler: harf ve rakam, yalnızca harf, yalnızca rakam.</summary>
    public static class CharacterSets
    {
        public const string Alphanumeric = "alphanumeric";
        public const string Letters = "letters";
        public const string Digits = "digits";

        public static IReadOnlyList<string> All { get; } = [Alphanumeric, Letters, Digits];
    }

    /// <summary>Ayraç olarak izin verilen karakterler; kodu çözümlenebilir tutar (harf ve rakam ayraç olamaz).</summary>
    public const string SeparatorCharacters = "-._/:";

    public Guid FacilityId { get; internal set; }

    /// <summary>Koddaki sıra (1 en dıştaki seviye, ör. tesis); tesis içinde benzersiz.</summary>
    public int Position { get; internal set; }

    /// <summary>Teknik anahtar (ör. "aisle"); tesis içinde benzersiz, küçük harf.</summary>
    public string Key { get; internal set; } = string.Empty;

    /// <summary>Görünen ad (ör. "Koridor"); kullanımdaki tesiste de değiştirilebilir.</summary>
    public string Name { get; internal set; } = string.Empty;

    public string CharacterSet { get; internal set; } = CharacterSets.Alphanumeric;

    public int MinLength { get; internal set; } = 1;

    public int MaxLength { get; internal set; } = 10;

    /// <summary>Bu seviyenin değerinden önce kodda duran ayraç (ilk seviyede yok sayılır); boş olabilir.</summary>
    public string Separator { get; internal set; } = "-";
}

/// <summary>
/// Tesis içindeki alan (K11, K18): gümrük denetimli ya da serbest bölge. Türün <b>kategorisi</b> kodun anladığı anlamdır
/// (<c>controlled</c>, <c>free</c>); adı ve ek değerleri tenant'ındır. Gümrük denetimindeki mal serbest alana yerleştirilemez
/// (K17); bu denetim lokasyon ve hareket dilimlerinde bu alan türüne bakar.
/// </summary>
public sealed class Zone : WarehouseEntity
{
    public const int CodeMaxLength = 30;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    public Guid FacilityId { get; internal set; }

    /// <summary>Tesis içinde benzersiz kısa kod.</summary>
    public string Code { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Alan türü (Y9b listesi <c>warehouse.zone-kind</c>); kategorisi denetim türünü belirler.</summary>
    public string KindKey { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Pasif alan yeni yerleştirme almaz; geçmiş kayıtları korunur.</summary>
    public bool IsActive { get; internal set; } = true;
}
