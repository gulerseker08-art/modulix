using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Crm.Domain;

// CRM entity'leri. Alanlar kayıt depolarından (Records) doğrulanarak yazılır; uzunluk sınırları
// hem doğrulamada hem veritabanı kolonlarında aynı sabitlerden gelir.

/// <summary>Tüm CRM kayıtlarının ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class CrmEntity : ITenantEntity, ICompanyShared
{
    protected CrmEntity()
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

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır.</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null. Boru hattı doğrular.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Bir kullanıcıya atanabilen kayıt. Sorumlu, aktif tenant'ın etkin üyesi olmalıdır.</summary>
public interface IOwnedEntity
{
    /// <summary>Sorumlu kullanıcı (platform kullanıcı kimliği). Verilmezse kaydı oluşturan kullanıcı atanır.</summary>
    Guid? OwnerId { get; }
}

// Seçim alanları (Y9b) firmanın düzenleyebildiği listelerden değer anahtarı taşır (CrmModule.ChoiceLists); hazır
// değerlerin anahtarları eski enum adlarıdır (ör. "Proposal"). Varsayılanlar boru hattında listenin ilk etkin değeridir.

/// <summary>
/// CRM firması: ortak taraf kaydının (ADR-0006) CRM uzantısı. Ünvan, vergi no, iletişim ve adres taraftadır; Lojistik
/// gibi diğer modüller aynı tarafı kullanır. Burada yalnızca CRM'e özgü bilgiler durur. Bir tarafın en fazla bir CRM firması olur.
/// </summary>
public sealed class Company : CrmEntity, IOwnedEntity
{
    public const int NotesMaxLength = 4000;
    public const int IndustryMaxLength = 100;
    public const int TagsMaxLength = 200;

    public Guid PartyId { get; private set; }

    /// <summary>Kayıt deposu her zaman yükler (<c>Include</c>); yeni firmada <see cref="AttachParty"/> ile atanır.</summary>
    public Party Party { get; private set; } = null!;

    public string? Industry { get; internal set; }

    public string Status { get; internal set; } = string.Empty;

    /// <summary>Müşterinin bize nereden geldiği.</summary>
    public string? Source { get; internal set; }

    /// <summary>Virgülle ayrılmış etiketler (ör. "öncelikli, ihracat").</summary>
    public string? Tags { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }

    /// <summary>Yeni firmayı ortak tarafa bağlar (yeni açılan veya mevcut).</summary>
    internal void AttachParty(Party party)
    {
        Party = party;
        PartyId = party.Id;
    }
}

/// <summary>
/// CRM kişisi: ortak kişi kaydının (ADR-0006) CRM uzantısı. Ad, unvan ve iletişim kişidedir. Bir kişinin en fazla bir CRM
/// kişisi olur. Firma CRM'e aittir; kişinin çalıştığı taraf firmanın tarafına eşitlenir.
/// </summary>
public sealed class Contact : CrmEntity, IOwnedEntity
{
    public Guid PersonId { get; private set; }

    /// <summary>Kayıt deposu her zaman yükler (<c>Include</c>); yeni kişide <see cref="AttachPerson"/> ile atanır.</summary>
    public Person Person { get; private set; } = null!;

    /// <summary>Kişinin çalıştığı firma; aynı tenant'ta olmak zorundadır.</summary>
    public Guid? CompanyId { get; internal set; }

    /// <summary>
    /// Firmasız kişinin açık sorumlusu (ADR-0010): veri kapsamı (Own/Team/All) firmasız kişide buna göre uygulanır. Firmaya
    /// bağlı kişi firmanın erişim kapsamına göre görünür; o zaman sorumlu bilgi amaçlıdır.
    /// </summary>
    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }

    /// <summary>Yeni kişiyi ortak kişiye bağlar (yeni açılan veya mevcut).</summary>
    internal void AttachPerson(Person person)
    {
        Person = person;
        PersonId = person.Id;
    }
}

public sealed class Opportunity : CrmEntity, IOwnedEntity
{
    public const int TitleMaxLength = 200;
    public const int LostReasonMaxLength = 500;
    public const int CurrencyLength = 3;
    public const string DefaultCurrency = "TRY";

    public string Title { get; internal set; } = string.Empty;

    public Guid CompanyId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    public string Stage { get; internal set; } = string.Empty;

    public decimal? Amount { get; internal set; }

    /// <summary>ISO 4217 kodu (ör. "TRY").</summary>
    public string Currency { get; internal set; } = DefaultCurrency;

    public DateOnly? ExpectedCloseDate { get; internal set; }

    /// <summary>Kazanma olasılığı (yüzde, 0-100).</summary>
    public int? Probability { get; internal set; }

    /// <summary>Fırsat kaybedildiyse nedeni.</summary>
    public string? LostReason { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }
}

public sealed class Activity : CrmEntity, IOwnedEntity
{
    public const int SubjectMaxLength = 200;

    public string Type { get; internal set; } = string.Empty;

    public string Subject { get; internal set; } = string.Empty;

    public DateTimeOffset? DueAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public Guid? CompanyId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    public Guid? OpportunityId { get; internal set; }

    /// <summary>Aktiviteyi yapacak kişi.</summary>
    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }
}

/// <summary>
/// Firma–kişi ilişkisi (ADR-0011): aynı kişi birden fazla firmayla, her biri için ayrı unvan, departman ve ilişki rolleriyle
/// bağlanır. İlişki kaydı doğruluk kaynağıdır; <see cref="Contact.CompanyId"/> eski API için bu kayıtlardan türetilen
/// "birincil firma"dır (en eski aktif ilişkinin firması). İlişki rolleri (satın alma, finans...) uygulama güvenlik rolü değildir.
/// Biten ilişki silinmez: <see cref="EndDate"/> dolar, geçmiş korunur.
/// </summary>
public sealed class CompanyContact : CrmEntity
{
    public const int DepartmentMaxLength = 100;
    public const int JobTitleMaxLength = 100;
    public const int RolesMaxLength = 200;

    public Guid CompanyId { get; internal set; }

    public Guid ContactId { get; internal set; }

    public Company Company { get; internal set; } = null!;

    public Contact Contact { get; internal set; } = null!;

    public string? Department { get; internal set; }

    /// <summary>Kişinin bu firmadaki unvanı; kişinin genel unvanı (<c>Person.Title</c>) değişmez.</summary>
    public string? JobTitle { get; internal set; }

    /// <summary>Virgülle ayrılmış ilişki rolü anahtarları (<c>CrmRelationshipRoles</c>).</summary>
    public string? Roles { get; internal set; }

    /// <summary>Firmanın ana irtibatı. Kapsam: firma başına en fazla bir aktif ilişki (veritabanı da denetler).</summary>
    public bool IsPrimary { get; internal set; }

    public bool IsDecisionMaker { get; internal set; }

    public DateOnly StartDate { get; internal set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public DateOnly? EndDate { get; internal set; }

    /// <summary>Bitiş tarihi yoksa aktiftir; depo <see cref="EndDate"/>'ten türetir (filtreli benzersiz indeks için saklanır).</summary>
    public bool IsActive { get; internal set; } = true;

    public IReadOnlyList<string> RoleList =>
        Roles is { Length: > 0 } ? Roles.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
}

/// <summary>
/// Müşteri lokasyonu (ADR-0012): firmanın aynı tüzel kişilik altındaki şubesi, deposu, fabrikası, teslimat veya fatura
/// noktası. Ayrı bir Taraf (Party) açmaz; ayrı tüzel kişilik ayrı firma (Party) olur ve firma–firma ilişkisiyle bağlanır.
/// İşletmecinin kendi şirket/şubesinden (ADR-0008, <c>ICompanyScoped</c>) bağımsızdır.
/// </summary>
public sealed class Location : CrmEntity
{
    public const int NameMaxLength = 150;
    public const int CodeMaxLength = 50;

    public Guid CompanyId { get; internal set; }

    public Company Company { get; internal set; } = null!;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Firma içinde tekil kısa kod (ör. "IST-DEPO"); boş olabilir.</summary>
    public string? Code { get; internal set; }

    /// <summary>Tür anahtarı (<c>CrmModule.LocationTypes</c>).</summary>
    public string Type { get; internal set; } = "Other";

    public bool IsActive { get; internal set; } = true;

    /// <summary>Lokasyonun yetkili kişisi (isteğe bağlı).</summary>
    public Guid? ContactId { get; internal set; }

    public string? Notes { get; internal set; }
}

/// <summary>
/// Yapılandırılmış adres (ADR-0012): firmaya, isteğe bağlı olarak firmanın bir lokasyonuna aittir. Orijinal alanlar korunur;
/// arama için <see cref="NormalizedText"/> ayrıca tutulur. Firma düzeyindeki ana adres eski <c>Party.Address/City</c> alanlarının kaynağıdır.
/// </summary>
public sealed class Address : CrmEntity
{
    public const int LineMaxLength = 250;
    public const int PartMaxLength = 100;
    public const int PostalCodeMaxLength = 20;
    public const int CountryLength = 2;

    public Guid CompanyId { get; internal set; }

    public Guid? LocationId { get; internal set; }

    public Company Company { get; internal set; } = null!;

    /// <summary>Tür anahtarı (<c>CrmModule.AddressTypes</c>).</summary>
    public string Type { get; internal set; } = "Other";

    public bool IsPrimary { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    /// <summary>ISO 3166-1 alfa-2 ülke kodu (büyük harf).</summary>
    public string Country { get; internal set; } = "TR";

    public string? City { get; internal set; }

    public string? District { get; internal set; }

    public string? PostalCode { get; internal set; }

    public string Line1 { get; internal set; } = string.Empty;

    public string? Line2 { get; internal set; }

    /// <summary>Arama/eşleştirme için küçük harfe ve aksansız biçime çevrilmiş birleşik metin; orijinal alanlar değişmez.</summary>
    public string NormalizedText { get; internal set; } = string.Empty;
}

/// <summary>
/// İletişim noktası (ADR-0012): telefon veya e-posta. Sahibi firma (isteğe bağlı lokasyonuyla) ya da kişidir; kişinin noktası
/// isteğe bağlı olarak bir firma–kişi ilişkisine özeldir (ör. o firmadaki iş e-postası). Orijinal değer korunur,
/// <see cref="NormalizedValue"/> arama/eşleştirme içindir. Paylaşılan adres/santral (<see cref="IsShared"/>) kimlik kanıtı sayılmaz.
/// Bir iletişim noktası pazarlama izni değildir; izin ayrı bir kavramdır ve burada tutulmaz.
/// </summary>
public sealed class ContactPoint : CrmEntity
{
    public const int ValueMaxLength = 254;
    public const int NormalizedMaxLength = 254;
    public const int NoteMaxLength = 200;

    public Guid? CompanyId { get; internal set; }

    public Guid? LocationId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    /// <summary>Yalnızca kişi sahipli noktada: noktanın özel olduğu firma–kişi ilişkisi.</summary>
    public Guid? CompanyContactId { get; internal set; }

    /// <summary>"Phone" veya "Email".</summary>
    public string Channel { get; internal set; } = "Phone";

    /// <summary>Tür anahtarı (<c>CrmModule.ContactPointTypes</c>).</summary>
    public string Type { get; internal set; } = "Work";

    public string Value { get; internal set; } = string.Empty;

    public string NormalizedValue { get; internal set; } = string.Empty;

    public bool IsPrimary { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    /// <summary>Birden fazla kişinin/firmanın paylaştığı santral veya ortak posta kutusu.</summary>
    public bool IsShared { get; internal set; }

    public string? Note { get; internal set; }
}

/// <summary>
/// Firma–firma ilişkisi (ADR-0012): ana firma → bağlı firma (iştirak). Geçerlilik tarihleri vardır; kendine bağ ve döngü reddedilir.
/// Bitiş tarihi verilen ilişki silinmeden pasifleşir.
/// </summary>
public sealed class CompanyRelation : CrmEntity
{
    public const int NotesMaxLength = 500;

    public Guid ParentCompanyId { get; internal set; }

    public Guid ChildCompanyId { get; internal set; }

    public Company ParentCompany { get; internal set; } = null!;

    public Company ChildCompany { get; internal set; } = null!;

    /// <summary>Tür anahtarı (<c>CrmModule.RelationTypes</c>).</summary>
    public string Type { get; internal set; } = "Subsidiary";

    public DateOnly StartDate { get; internal set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public DateOnly? EndDate { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public string? Notes { get; internal set; }
}

/// <summary>
/// Birleştirme izi (ADR-0013): kaynak kaydın hangi kayda birleştirildiği. Kaynak kayıt kaldırılır; eski kimlikle gelen istek
/// ve eski bağlantılar bu iz sayesinde hedefe çözülür, tekrarlanan birleştirme isteği buradan tanınır. Yalnızca birleştirme
/// yazar; genel kayıt uçlarında yoktur.
/// </summary>
public sealed class MergedRecord : CrmEntity
{
    public const int EntityKeyMaxLength = 50;

    /// <summary>Birleştirilen kayıt türü (ör. "crm.company").</summary>
    public string EntityKey { get; internal set; } = string.Empty;

    public Guid SourceId { get; internal set; }

    public Guid TargetId { get; internal set; }

    public Guid? MergedBy { get; internal set; }
}
