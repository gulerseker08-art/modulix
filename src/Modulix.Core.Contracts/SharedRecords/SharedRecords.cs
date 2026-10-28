using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Contracts.SharedRecords;

// Ortak kayıtlar (ADR-0006): firma (Taraf) ve kişi çekirdekte tek kayıttır. Modüller kendi bilgilerini bu kayda bağlı
// uzantı entity'lerinde tutar ve ortak kayda navigation ile bağlanır (ör. CRM firması → Party). Tablolar çekirdekte,
// "core" şemasında yapılandırılır; uzantının deposu ortak alanları navigation üzerinden okur ve yazar, ikisi aynı işlemde
// kaydedilir.

/// <summary>Ortak kayıt türlerinin anahtarları (kayıt boru hattındaki EntityKey).</summary>
public static class SharedEntities
{
    public const string Party = "core.party";
    public const string Person = "core.person";

    public static IReadOnlyList<string> All { get; } = [Party, Person];
}

/// <summary>Ortak kayıtların ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class SharedEntity : ITenantEntity, ICompanyShared
{
    protected SharedEntity()
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

    /// <summary>Her değişiklikte artar; başka bir modülün araya giren değişikliği eşzamanlılık hatası verir.</summary>
    public long Version { get; private set; }

    /// <summary>Kayıt değiştiyse çağrılır (değişikliği hangi modülün yaptığından bağımsız).</summary>
    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

public enum PartyKind
{
    /// <summary>Tüzel kişi (şirket, kurum); vergi numarası 10 hanedir.</summary>
    Organization = 1,

    /// <summary>Gerçek kişi (şahıs firması); vergi numarası yerine T.C. kimlik numarası.</summary>
    Individual = 2,
}

/// <summary>
/// Taraf: müşteri, tedarikçi, taşıyıcı... Rol ayrı bir alan değildir, modül uzantısının varlığından anlaşılır.
/// Vergi numarası tenant içinde tekildir ve kesin eşleşmedir: aynı numarayla açılan uzantı mevcut tarafa bağlanır.
/// </summary>
public sealed class Party : SharedEntity
{
    public const int NameMaxLength = 200;
    public const int TaxOfficeMaxLength = 100;
    public const int TaxNumberMaxLength = 20;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 254;
    public const int WebsiteMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int AddressMaxLength = 500;

    public PartyKind Kind { get; set; } = PartyKind.Organization;

    /// <summary>Ünvan.</summary>
    public string Name { get; set; } = string.Empty;

    public string? TaxOffice { get; set; }

    /// <summary>Vergi numarası veya T.C. kimlik numarası.</summary>
    public string? TaxNumber { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    public string? City { get; set; }

    public string? Address { get; set; }
}

/// <summary>
/// Kişi: müşteri yetkilisi, çalışan, sürücü... Kullanıcı olabilir de olmayabilir de (üyelik bağı sonraki adımda).
/// <see cref="PartyId"/> boşsa kişi firmanın kendi personeli veya bağımsızdır.
/// </summary>
public sealed class Person : SharedEntity
{
    public const int NameMaxLength = 100;
    public const int TitleMaxLength = 100;
    public const int EmailMaxLength = Party.EmailMaxLength;
    public const int PhoneMaxLength = Party.PhoneMaxLength;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>Unvan / görev (ör. "Satın alma müdürü").</summary>
    public string? Title { get; set; }

    /// <summary>Çalıştığı taraf; aynı tenant'ta olmak zorundadır.</summary>
    public Guid? PartyId { get; set; }
}

/// <summary>
/// Modülün bir ortak kayıt türünü hangi izinleriyle kullandığı (ADR-0006). Ortak kaydı, tenant için açık modüllerden
/// birinde bildirilen okuma iznine sahip olan okur; yazmak için bildirilen yazma izni gerekir (null: bu modül yazmaz).
/// İzinler modülün kendi izinleridir.
/// </summary>
public sealed record SharedRecordAccess(string EntityKey, string ReadPermission, string? WritePermission = null);

/// <summary>
/// Ortak kayda başvuran bir alan (ör. CRM firmasının "partyId"si). Çekirdek, ortak kaydın nerede kullanıldığını bu
/// bildirimlerle sayar (silme, birleştirme): kayıt deposu <see cref="Field"/> adında, kimlik değeri alan bir filtre
/// sunmalıdır. <see cref="Label"/>: kullanım sayısında gösterilen ad (ör. "CRM firması").
/// </summary>
public sealed record SharedRecordReference(string SharedEntityKey, string EntityKey, string Field, string Label);

/// <summary>
/// Ortak kaydın modül uzantısının kayıt kapsamı (ADR-0010). Genel ortak kayıt uçları (<c>core.party</c>, <c>core.person</c>)
/// uzantının kapsam kısıtını dolanamaz: modül, çağıranın uzantısını göremediği ortak kayıtları bildirir; bunlar yalnızca
/// asgari kimlik bilgisiyle (seçim için) verilir, tam profil okunamaz, değiştirilemez ve silinemez. Modül, çağıranın
/// erişimi başka bir modülden geliyorsa (kendi okuma izni yoksa) kısıt bildirmez. Birden fazla modül varsa en kısıtlısı geçerlidir.
/// </summary>
public interface ISharedRecordVisibility
{
    /// <summary>Verilen ortak kayıtlardan, çağıranın bu modüldeki kapsamı nedeniyle tam profilini göremeyecek olanların kimlikleri.</summary>
    Task<IReadOnlySet<Guid>> RestrictedAsync(string sharedEntityKey, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
