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
/// Lokasyon ağacının bir düğümü (K11). Düğümün seviyesi derinliğinden gelir: n. derinlik, tesisin sıraya göre n. seviye tanımına
/// uyar; değeri o seviyenin karakter kümesine ve uzunluk aralığına uymalıdır. <see cref="Code"/> üst düğümün kodu, seviyenin ayracı
/// ve bu düğümün değerinden üretilir (ör. <c>ANT-A-02-04-C</c>) ve tesis içinde benzersizdir. Kod ve değer oluşturulduktan sonra
/// değişmez (alt düğümlerin kodları buna bağlı olduğundan); düğüm taşınması sonraki bir adımdır. Kapasite ve kısıtlar stok
/// dilimiyle gelir.
/// </summary>
public sealed class Location : WarehouseEntity
{
    public const int ValueMaxLength = LocationLevel.MaxValueLength;
    public const int CodeMaxLength = 250;
    public const int PathMaxLength = 450;
    public const int NameMaxLength = 200;

    public Guid FacilityId { get; internal set; }

    /// <summary>Üst düğüm; kök düğümde (1. seviye) boş.</summary>
    public Guid? ParentId { get; internal set; }

    /// <summary>
    /// Düğümün alanı (K18). Üst düğümün alanı varsa alt düğüm aynı alanı taşır. Gümrük denetimindeki malın serbest alana
    /// yerleştirilememesi (K17) bu alanın türüne bakar; yerleştirme denetimi stok dilimiyle gelir.
    /// </summary>
    public Guid? ZoneId { get; internal set; }

    /// <summary>1 kök düğüm; her alt düzey bir artar.</summary>
    public int Depth { get; internal set; }

    /// <summary>Bu düğümün seviye içindeki değeri (ör. "A", "02"); büyük harfe çevrilir.</summary>
    public string Value { get; internal set; } = string.Empty;

    /// <summary>Tam kod (üst kod + ayraç + değer); tesis içinde benzersiz.</summary>
    public string Code { get; internal set; } = string.Empty;

    /// <summary>Kökten kendine kimlik yolu ("/kök/…/kendi/"); alt ağaç sorguları için.</summary>
    public string Path { get; internal set; } = string.Empty;

    public string? Name { get; internal set; }

    /// <summary>Pasif lokasyon yeni yerleştirme almaz; geçmiş kayıtları korunur.</summary>
    public bool IsActive { get; internal set; } = true;
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

/// <summary>
/// Vekâlet (K4): ithalatçının gümrük müşavirine verdiği yetki. İthalatçı ve müşavir Core'daki firma kaydıdır (<c>core.party</c>, K2);
/// vekâlet yalnızca bunlara başvurur. Geçerlilik tarih aralığı ve <see cref="IsActive"/> (iptal edilmemiş) birlikte belirler; kapsam ve
/// belge bilgisi serbest metindir (belge dosyası yükleme sonraki adımdır). Müşavirin hangi ithalatçının kaydını görebileceği bu
/// kayıttan türetilir (ASN dilimi); bu kayıt tek başına kimseye erişim vermez.
/// </summary>
public sealed class PowerOfAttorney : WarehouseEntity
{
    public const int ScopeMaxLength = 500;
    public const int DocumentMaxLength = 200;

    /// <summary>İthalatçı / malik (firma). Oluşturulduktan sonra değişmez: başka ithalatçı yeni vekâlettir.</summary>
    public Guid ImporterPartyId { get; internal set; }

    /// <summary>Gümrük müşaviri (firma). Oluşturulduktan sonra değişmez.</summary>
    public Guid BrokerPartyId { get; internal set; }

    public DateOnly ValidFrom { get; internal set; }

    /// <summary>Bitiş tarihi (dahil); boşsa süresiz.</summary>
    public DateOnly? ValidTo { get; internal set; }

    /// <summary>Vekâletin kapsamı (serbest metin; ör. "ithalat beyannameleri"). Mevzuat doğrulanana kadar yapılandırılmış değildir.</summary>
    public string? Scope { get; internal set; }

    /// <summary>Belge referansı (noter yevmiye no, dosya adı vb.). Dosya yükleme sonraki adımdır.</summary>
    public string? DocumentReference { get; internal set; }

    /// <summary>İptal edilmiş vekâlet false'tur; tarihi geçerli olsa da geçerli sayılmaz.</summary>
    public bool IsActive { get; internal set; } = true;

    /// <summary>Verilen günde geçerli mi? (etkin ve tarih aralığında)</summary>
    public bool IsValidOn(DateOnly day) => IsValid(IsActive, ValidFrom, ValidTo, day);

    /// <summary>Saf geçerlilik kuralı: etkin ve [başlangıç, bitiş] aralığında (bitiş boşsa süresiz).</summary>
    public static bool IsValid(bool isActive, DateOnly validFrom, DateOnly? validTo, DateOnly day) =>
        isActive && validFrom <= day && (validTo is null || day <= validTo);
}
