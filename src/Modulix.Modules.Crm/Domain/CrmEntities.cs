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

    /// <summary>
    /// Fırsatın doğduğu Lead (izlenebilirlik). Fırsat aşamalarındaki "Lead/Aday" aşamasından bağımsızdır: o, fırsatın erken
    /// aşamasıdır; bu alan ise bağımsız Lead sürecinin dönüşüm izidir. Mevcut fırsatlar otomatik Lead'e taşınmaz, alan boş kalır.
    /// </summary>
    public Guid? SourceLeadId { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }

    // ---- ADR-0017: yaşam döngüsü, ihtiyaç analizi ve kapanış bilgileri

    public const int NumberMaxLength = 30;
    public const int TextMaxLength = 4000;
    public const int ShortMaxLength = 200;
    public const int StatusMaxLength = 20;

    /// <summary>Tenant içinde benzersiz fırsat numarası (OP-20261029-AB12CD).</summary>
    public string OpportunityNumber { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Yaşam döngüsü durumu (<see cref="OpportunityStatus"/>). Aşama ile tutarlılığı iş akışı korur.</summary>
    public string Status { get; internal set; } = OpportunityStatus.Open;

    /// <summary>Beklemeye alma, iptal ve yeniden açma gerekçesi; durum açık dönünce temizlenir.</summary>
    public string? StatusReason { get; internal set; }

    public string Priority { get; internal set; } = LeadPriority.Normal;

    /// <summary>Fırsat türü (seçim listesi: yeni müşteri, ek satış, yenileme...).</summary>
    public string? Type { get; internal set; }

    /// <summary>Edinim kaynağı (Lead kaynak listesi).</summary>
    public string? Source { get; internal set; }

    public string? NeedDescription { get; internal set; }

    public string? CurrentProblem { get; internal set; }

    public string? ExpectedBenefit { get; internal set; }

    public string? Scope { get; internal set; }

    public string? SuccessCriteria { get; internal set; }

    public string? Risks { get; internal set; }

    public string? DecisionProcess { get; internal set; }

    public DateOnly? TargetDecisionDate { get; internal set; }

    /// <summary>Müşterinin bildirdiği bütçe. null = bilinmiyor; sıfır tutar "bütçe yok" demek değildir, bunu <see cref="BudgetStatus"/> söyler.</summary>
    public decimal? CustomerBudget { get; internal set; }

    public string BudgetStatus { get; internal set; } = OpportunityBudgetStatus.Unknown;

    public DateOnly? ActualCloseDate { get; internal set; }

    /// <summary>Kazanılan gerçekleşen tutar; tahmini <see cref="Amount"/>'tan ayrıdır.</summary>
    public decimal? WonAmount { get; internal set; }

    public string? WonCurrency { get; internal set; }

    public string? WinReason { get; internal set; }

    /// <summary>Operasyon ekibine devir notu (kazanımda).</summary>
    public string? HandoverNote { get; internal set; }

    /// <summary>Yapılandırılmış kaybetme nedeni (seçim listesi). <see cref="LostReason"/> serbest açıklamadır.</summary>
    public string? LossReason { get; internal set; }

    /// <summary>Kaybedilen fırsatta kazanan rakip (bu fırsatın rakiplerinden biri).</summary>
    public Guid? WinningCompetitorId { get; internal set; }

    public DateOnly? RecontactDate { get; internal set; }

    public DateOnly? HoldReviewDate { get; internal set; }

    public string? NextAction { get; internal set; }

    public DateOnly? NextActionDate { get; internal set; }

    /// <summary>Son anlamlı müşteri etkileşimi (gerçekleşen temas). "Son güncelleme" (<c>UpdatedAt</c>) ile aynı bilgi değildir.</summary>
    public DateTimeOffset? LastInteractionAt { get; internal set; }

    /// <summary>Fırsatın bulunduğu aşamaya girdiği an (aşamada bekleme süresi için).</summary>
    public DateTimeOffset StageEnteredAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Olasılığı kullanıcı elle girdiyse true: aşama değişince aşama varsayılanı bunu ezmez.</summary>
    public bool ProbabilityManual { get; internal set; }

    /// <summary>Kapanış döngüsü: 1'den başlar, her yeniden açmada artar. Raporlar yalnızca fırsatın güncel kapanışını sayar.</summary>
    public int ClosureCycle { get; internal set; } = 1;

    public int CloseDateChangeCount { get; internal set; }
}

/// <summary>Fırsat yaşam döngüsü durumları. Aşama kategorisiyle ilişkisi için bkz. ADR-0017.</summary>
public static class OpportunityStatus
{
    public const string Open = "Open";
    public const string OnHold = "OnHold";
    public const string Won = "Won";
    public const string Lost = "Lost";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Open, OnHold, Won, Lost, Cancelled];

    /// <summary>Kapanmış: kazanıldı, kaybedildi ya da iptal edildi. Beklemedeki fırsat kapanmış değildir.</summary>
    public static bool IsClosed(string status) => status is Won or Lost or Cancelled;

    /// <summary>Hâlâ yürüyen (açık ya da beklemede) fırsat; devir ve "açık fırsat" süzgecinde bunlar sayılır.</summary>
    public static bool IsLive(string status) => status is Open or OnHold;
}

public static class OpportunityBudgetStatus
{
    public const string Unknown = "Unknown";
    public const string Unconfirmed = "Unconfirmed";
    public const string Confirmed = "Confirmed";
    public const string None = "None";

    public static IReadOnlyList<string> All { get; } = [Unknown, Unconfirmed, Confirmed, None];
}

/// <summary>Fırsat tarihçesi kayıt türleri.</summary>
public static class OpportunityHistoryKinds
{
    public const string Created = "Created";
    public const string Stage = "Stage";
    public const string Status = "Status";
    public const string CloseDate = "CloseDate";
}

/// <summary>
/// Fırsat–kişi ilişkisi: kişinin karar verici, bütçe sahibi vb. rolü kişi kaydında değil bu fırsatla ilişkisindedir.
/// </summary>
public sealed class OpportunityContact : CrmEntity
{
    public const int NoteMaxLength = 500;
    public const int RolesMaxLength = 200;

    public Guid OpportunityId { get; internal set; }

    public Guid ContactId { get; internal set; }

    public string? Roles { get; internal set; }

    public bool IsPrimary { get; internal set; }

    public string? Note { get; internal set; }

    public IReadOnlyList<string> RoleList =>
        string.IsNullOrEmpty(Roles) ? [] : Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public Contact Contact { get; internal set; } = null!;
}

/// <summary>Fırsattaki rakip: güçlü/zayıf yönler ve biliniyorsa alternatif teklif. Rakip firma kaydı olmak zorunda değildir.</summary>
public sealed class OpportunityCompetitor : CrmEntity
{
    public Guid OpportunityId { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string? Strengths { get; internal set; }

    public string? Weaknesses { get; internal set; }

    public decimal? AlternativeAmount { get; internal set; }

    public string? AlternativeCurrency { get; internal set; }

    public string? Notes { get; internal set; }
}

/// <summary>
/// Fırsat tarihçesi: aşama/durum geçişleri, kapanış ve yeniden açma, kapanış tarihi ertelemeleri. Kayıt boru hattıyla birlikte aynı
/// işlemde yazılır; hiçbir uçtan değiştirilmez ya da silinmez (fırsat silinince birlikte kalkar). Eski kapanış bilgileri burada korunur.
/// </summary>
public sealed class OpportunityHistory : CrmEntity
{
    public const int ReasonMaxLength = 1000;

    public Guid OpportunityId { get; internal set; }

    public string Kind { get; internal set; } = OpportunityHistoryKinds.Stage;

    public DateTimeOffset At { get; internal set; } = DateTimeOffset.UtcNow;

    public Guid? ChangedBy { get; internal set; }

    public int Cycle { get; internal set; } = 1;

    public string? FromStage { get; internal set; }

    public string? ToStage { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public DateOnly? FromCloseDate { get; internal set; }

    public DateOnly? ToCloseDate { get; internal set; }

    public string? Reason { get; internal set; }

    /// <summary>Kapanış ve yeniden açma anındaki tutar/neden bilgisi (JSON). Hassas tutarlar içerir; tutar izni olmayana verilmez.</summary>
    public string? Details { get; internal set; }
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

    /// <summary>Aktivitenin bağlı olduğu Lead (isteğe bağlı); Lead için ayrı bir görev sistemi yoktur, bu aktivite tablosu kullanılır.</summary>
    public Guid? LeadId { get; internal set; }

    /// <summary>
    /// Sonuç (<c>ActivityOutcomes</c>): Lead'in "gerçekleşen ilk teması"nı belirlemek için kullanılır. Boş olabilir.
    /// </summary>
    public string? Outcome { get; internal set; }

    /// <summary>Birincil sorumlu: işi yapmaktan sorumlu tek kişi. Oluşturan (<see cref="CreatedBy"/>), katılımcı ve izleyiciden ayrıdır.</summary>
    public Guid? OwnerId { get; internal set; }

    /// <summary>Açıklama / notlar (aktivitenin açıklama alanı).</summary>
    public string? Notes { get; internal set; }

    // ---- Yaşam döngüsü (ADR-0015). Durum, gerekçe, tamamlanma ve sonuç yalnızca eylem servisiyle değişir.

    public const int ReasonMaxLength = 500;
    public const int LocationMaxLength = 300;
    public const int UrlMaxLength = 500;
    public const int TimeZoneMaxLength = 64;
    public const int ExternalIdMaxLength = 200;
    public const int MaxMinutes = 60 * 24 * 14;

    public string Status { get; internal set; } = Activities.ActivityStatus.Open;

    /// <summary>İptal / bloke / yeniden açma gerekçesi (son durum değişikliğininki).</summary>
    public string? StatusReason { get; internal set; }

    public string Priority { get; internal set; } = Activities.ActivityPriority.Normal;

    // ---- Zaman. Son tarih (DueAt) bir teslim anıdır; zaman bloğu (StartAt/EndAt) takvimde yer tutar. İkisi birbirinden bağımsızdır.
    // Saatli etkinlik UTC anı olarak saklanır; tüm gün etkinliği saat dilimi dönüşümü olmadan takvim günü (DateOnly) olarak.

    public DateTimeOffset? StartAt { get; internal set; }

    public DateTimeOffset? EndAt { get; internal set; }

    public bool IsAllDay { get; internal set; }

    public DateOnly? StartDate { get; internal set; }

    /// <summary>Tüm gün etkinliğinin son günü (dahil); boşsa tek gün.</summary>
    public DateOnly? EndDate { get; internal set; }

    /// <summary>Etkinliğin planlandığı IANA saat dilimi (ör. Europe/Istanbul); tekrar ve yerel saat hesapları bu dilime göre yapılır.</summary>
    public string? TimeZone { get; internal set; }

    public string? Location { get; internal set; }

    public string? OnlineMeetingUrl { get; internal set; }

    /// <summary>Özel aktivite: başlık, açıklama ve ilişkili kayıtlar yalnızca sorumlu, katılımcılar ve özel izin sahibine görünür; diğerleri meşguliyet görür.</summary>
    public bool IsPrivate { get; internal set; }

    /// <summary>Kullanıcı / otomasyon / entegrasyon / içe aktarma (<c>ActivitySource</c>).</summary>
    public string Source { get; internal set; } = Activities.ActivitySource.User;

    /// <summary>Kaydı oluşturan kullanıcı; sorumludan farklı olabilir ve sonradan değişmez.</summary>
    public Guid? CreatedBy { get; internal set; }

    public int? EstimatedMinutes { get; internal set; }

    public int? ActualMinutes { get; internal set; }

    /// <summary>Kaç kez ertelendi; her erteleme ayrıca audit geçmişinde eski/yeni tarih ve gerekçeyle durur.</summary>
    public int RescheduleCount { get; internal set; }

    /// <summary>Bu aktivitenin "sonraki adım" olarak üretildiği önceki aktivite.</summary>
    public Guid? PreviousActivityId { get; internal set; }

    /// <summary>Dış sistem (takvim sağlayıcısı, içe aktarma) ve oradaki kimlik; (kaynak, kimlik) çifti tekrar üretimi veritabanında engeller.</summary>
    public string? ExternalSource { get; internal set; }

    public string? ExternalId { get; internal set; }
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

/// <summary>
/// Lead durumları. Durum yalnızca iş akışı eylemleriyle (<c>LeadWorkflow</c>) değişir; genel kayıt uçları durumu yazamaz.
/// "Atandı" ayrı bir durum değildir: atama <see cref="Lead.OwnerId"/> ile temsil edilir (tek doğruluk kaynağı); atanmamış
/// Lead'ler "atama bekleyen" görünümünde (sorumlusu boş) listelenir.
/// </summary>
public static class LeadStatus
{
    public const string New = "New";
    public const string Contacting = "Contacting";
    public const string Qualified = "Qualified";
    public const string Nurturing = "Nurturing";
    public const string Disqualified = "Disqualified";
    public const string Converted = "Converted";

    public static IReadOnlyList<string> All { get; } = [New, Contacting, Qualified, Nurturing, Disqualified, Converted];

    /// <summary>Açık: henüz sonuçlanmamış (elenmemiş ve dönüştürülmemiş).</summary>
    public static IReadOnlyList<string> Open { get; } = [New, Contacting, Qualified, Nurturing];

    public static bool IsOpen(string status) => status is New or Contacting or Qualified or Nurturing;
}

public static class LeadPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static IReadOnlyList<string> All { get; } = [Low, Normal, High, Urgent];
}

/// <summary>
/// Lead: yeterliliği henüz doğrulanmamış satış ilgisi/talebi. Firma, kişi ve fırsattan ayrı bir kayıttır; açılırken firma ya da
/// kişi oluşturmak gerekmez. Talep sahibinin bilgileri Lead üzerinde ham olarak durur; dönüşümde firma/kişiye kopyalanır.
/// Mevcut bir müşteriden gelen talep <see cref="CompanyId"/> / <see cref="ContactId"/> ile ona bağlanabilir. Aynı e-posta
/// tek başına mükerrer kanıtı değildir. İşletici şirket kapsamı yoktur (CRM ana verisi ortaktır, ADR-0008).
/// </summary>
public sealed class Lead : CrmEntity, IOwnedEntity
{
    public const int SubjectMaxLength = 200;
    public const int NumberMaxLength = 30;
    public const int TextMaxLength = 4000;
    public const int NameMaxLength = 100;
    public const int ShortMaxLength = 200;
    public const int EmailMaxLength = 254;
    public const int PhoneMaxLength = 40;
    public const int UrlMaxLength = 300;
    public const int ReasonMaxLength = 500;
    public const int UtmMaxLength = 100;
    public const int ExternalIdMaxLength = 200;

    /// <summary>Okunabilir tekil numara (ör. "LD-20261028-7K2M4Q"); oluşturmada üretilir.</summary>
    public string LeadNumber { get; internal set; } = string.Empty;

    public string Subject { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public string Status { get; internal set; } = LeadStatus.New;

    /// <summary>Son durum değişikliğinin gerekçesi (eleme/takip/yeniden açma).</summary>
    public string? StatusReason { get; internal set; }

    public string Priority { get; internal set; } = LeadPriority.Normal;

    // Talep sahibi
    public string? FirstName { get; internal set; }

    public string? LastName { get; internal set; }

    public string? CompanyName { get; internal set; }

    public string? Email { get; internal set; }

    /// <summary>Arama/eşleştirme için normalleştirilmiş e-posta (kimlik kanıtı değildir).</summary>
    public string? NormalizedEmail { get; internal set; }

    public string? Phone { get; internal set; }

    public string? NormalizedPhone { get; internal set; }

    public string? JobTitle { get; internal set; }

    public string? Website { get; internal set; }

    /// <summary>Web sitesinden çıkarılan alan adı (mükerrer araması için).</summary>
    public string? WebsiteDomain { get; internal set; }

    public string? Country { get; internal set; }

    public string? City { get; internal set; }

    /// <summary>Talebin geldiği mevcut müşteri firması (isteğe bağlı).</summary>
    public Guid? CompanyId { get; internal set; }

    /// <summary>Talebin geldiği mevcut kişi (isteğe bağlı).</summary>
    public Guid? ContactId { get; internal set; }

    // Talep içeriği
    public string? ProductInterest { get; internal set; }

    public string? NeedDescription { get; internal set; }

    public decimal? EstimatedBudget { get; internal set; }

    public string Currency { get; internal set; } = Opportunity.DefaultCurrency;

    public DateOnly? TargetPurchaseDate { get; internal set; }

    public string? Industry { get; internal set; }

    // Kaynak
    public string? Source { get; internal set; }

    public string? SourceDetail { get; internal set; }

    public string? Referrer { get; internal set; }

    /// <summary>Talebi getiren dış kaynak/entegrasyon adı (ör. "web-form:iletisim").</summary>
    public string? ExternalSource { get; internal set; }

    /// <summary>Dış gönderim kimliği; (ExternalSource, ExternalSubmissionId) çifti teknik tekrarı engeller.</summary>
    public string? ExternalSubmissionId { get; internal set; }

    public string? UtmSource { get; internal set; }

    public string? UtmMedium { get; internal set; }

    public string? UtmCampaign { get; internal set; }

    /// <summary>Talebin ilk geldiği an (içe aktarmada orijinal zaman olabilir).</summary>
    public DateTimeOffset FirstReceivedAt { get; internal set; } = DateTimeOffset.UtcNow;

    // Sahiplik
    public Guid? OwnerId { get; internal set; }

    public DateTimeOffset? AssignedAt { get; internal set; }

    // Takip ve dönüşüm
    public DateTimeOffset? FirstContactAt { get; internal set; }

    public DateTimeOffset? FollowUpAt { get; internal set; }

    public DateTimeOffset? QualifiedAt { get; internal set; }

    public DateTimeOffset? DisqualifiedAt { get; internal set; }

    public string? DisqualificationReason { get; internal set; }

    public DateTimeOffset? ConvertedAt { get; internal set; }

    public Guid? ConvertedBy { get; internal set; }

    public Guid? ConvertedCompanyId { get; internal set; }

    public Guid? ConvertedContactId { get; internal set; }

    public Guid? ConvertedOpportunityId { get; internal set; }
}

/// <summary>
/// Lead toplama formu / entegrasyonu tanımı. Genel (herkese açık) bir <see cref="FormKey"/> ile tenant içinde tanınır; anahtar
/// GİZLİ DEĞİLDİR (sayfa kaynağında görünür) ve tek başına yetki vermez. Sunucudan sunucuya çağrılar ayrıca bir entegrasyon sırrı
/// kullanır; sır yalnızca özeti (<see cref="SecretHash"/>) saklanır, düz değer oluşturulurken bir kez gösterilir ve tarayıcıya gönderilmez.
/// Form yalnızca Lead oluşturur; durum, sorumlu (varsayılan sorumlu dışında), iş birimi ve rolü istemci belirleyemez.
/// </summary>
public sealed class LeadForm : CrmEntity
{
    public const int NameMaxLength = 100;
    public const int KeyLength = 20;
    public const int OriginsMaxLength = 1000;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Genel form kimliği (rastgele, tenant içinde benzersiz).</summary>
    public string FormKey { get; internal set; } = string.Empty;

    public bool IsActive { get; internal set; } = true;

    /// <summary>Gelen Lead'lerin varsayılan sorumlusu; boşsa ya da pasifse Lead atama havuzuna düşer (rastgele atama yapılmaz).</summary>
    public Guid? DefaultOwnerId { get; internal set; }

    /// <summary>İzin verilen tarayıcı kaynakları (boşluk/virgül/satır ayrık, "https://site.com"). Kimlik doğrulama değildir.</summary>
    public string? AllowedOrigins { get; internal set; }

    /// <summary>Doluysa yalnızca geçerli sırrı gönderen sunucu çağrıları kabul edilir; tarayıcıdan doğrudan gönderim reddedilir.</summary>
    public bool ServerOnly { get; internal set; }

    /// <summary>Entegrasyon sırrının SHA-256 özeti (hex); düz sır saklanmaz. Hiçbir yanıtta ya da audit'te yer almaz.</summary>
    public string? SecretHash { get; internal set; }

    public DateTimeOffset? SecretRotatedAt { get; internal set; }
}
