using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Modulix.Shared.Crm;

// CRM uç noktalarının (/api/crm/...) sözleşmeleri. Alan adları kayıt boru hattındaki teknik adlarla
// aynıdır (camelCase); sunucu aynı kuralları yeniden doğrular. Güncelleme ve silmede Version zorunludur (yoksa 428);
// değişmiş sürüm 409 ile reddedilir. Listeler sayfalıdır: CrmPage<T>.

/// <summary>
/// Modül ve izin anahtarları; arayüz menüleri ve ekranları buna göre gösterir. Sunucudaki
/// <c>CrmModule</c> ile aynı olmalıdır (entegrasyon testi denetler).
/// </summary>
public static class CrmAccess
{
    public const string ModuleKey = "crm";

    public const string CompaniesRead = "crm.companies.read";
    public const string CompaniesWrite = "crm.companies.write";
    public const string ContactsRead = "crm.contacts.read";
    public const string ContactsWrite = "crm.contacts.write";
    public const string CompaniesMerge = "crm.companies.merge";
    public const string ContactsMerge = "crm.contacts.merge";
    public const string OpportunitiesRead = "crm.opportunities.read";
    public const string OpportunityAmountsRead = "crm.opportunity-amounts.read";
    public const string OpportunitiesWrite = "crm.opportunities.write";
    public const string ActivitiesRead = "crm.activities.read";
    public const string ActivitiesWrite = "crm.activities.write";

    public const string LeadsRead = "crm.leads.read";
    public const string LeadsCreate = "crm.leads.create";
    public const string LeadsUpdate = "crm.leads.update";
    public const string LeadsDelete = "crm.leads.delete";
    public const string LeadsAssign = "crm.leads.assign";
    public const string LeadsQualify = "crm.leads.qualify";
    public const string LeadsDisqualify = "crm.leads.disqualify";
    public const string LeadsConvert = "crm.leads.convert";
    public const string LeadsExport = "crm.leads.export";
    public const string LeadFormsManage = "crm.lead-forms.manage";
}

/// <summary>Kayıt türü anahtarları; kayıt geçmişi (<c>/api/records/{tür}/{id}/history</c>) bunlarla çağrılır.</summary>
public static class CrmEntityKeys
{
    public const string Company = "crm.company";
    public const string Contact = "crm.contact";
    public const string CompanyContact = "crm.company-contact";
    public const string Location = "crm.location";
    public const string Address = "crm.address";
    public const string ContactPoint = "crm.contact-point";
    public const string PersonContactPoint = "crm.person-contact-point";
    public const string CompanyRelation = "crm.company-relation";
    public const string Opportunity = "crm.opportunity";
    public const string Activity = "crm.activity";
    public const string Lead = "crm.lead";
}

/// <summary>CRM'in düzenlenebilir seçim listeleri (Y9b) ve kategorileri; sunucudaki CrmModule ile aynıdır.</summary>
public static class CrmLists
{
    public const string OpportunityStage = "crm.opportunity-stage";
    public const string CompanyStatus = "crm.company-status";
    public const string CompanySource = "crm.company-source";
    public const string ActivityType = "crm.activity-type";

    public static class StageCategories
    {
        public const string Open = "open";
        public const string Won = "won";
        public const string Lost = "lost";
    }
}

public sealed record CrmPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class CrmLimits
{
    public const int NameMaxLength = 200;
    public const int PersonNameMaxLength = 100;
    public const int TaxNumberMaxLength = 20;
    public const int TaxOfficeMaxLength = 100;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 254;
    public const int TextMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int NotesMaxLength = 4000;
    public const int IndustryMaxLength = 100;
    public const int AddressMaxLength = 500;
    public const int TagsMaxLength = 200;
    public const int LostReasonMaxLength = 500;
}

/// <summary>Firma durumunun hazır değerleri (Y9b: firma listeyi düzenleyebilir, değer bir anahtardır).</summary>
public static class CompanyStatus
{
    public const string Prospect = "Prospect";
    public const string Customer = "Customer";
    public const string Inactive = "Inactive";
}

/// <summary>Firma kaynağının hazır değerleri.</summary>
public static class CompanySource
{
    public const string Referral = "Referral";
    public const string Website = "Website";
    public const string Event = "Event";
    public const string Advertising = "Advertising";
    public const string ColdCall = "ColdCall";
    public const string Other = "Other";
}

/// <summary>CRM firması. Ad, vergi no, iletişim ve adres ortak firma kaydındadır (<see cref="PartyId"/>, ADR-0006).</summary>
public sealed record CompanyDto(
    Guid Id,
    long Version,
    string Name,
    string? TaxNumber,
    string? Phone,
    string? Email,
    string? Website,
    string? City,
    string? Address,
    string? Industry,
    string Status,
    string? Source,
    string? Tags,
    Guid? OwnerId,
    string? Notes,
    JsonObject? Custom = null,
    Guid? PartyId = null,
    string? Kind = null,
    string? TaxOffice = null);

/// <summary>
/// Sorumlu boş bırakılırsa kaydı oluşturan kullanıcı atanır. Liste alanları (durum, aşama, tip) boş bırakılırsa yeni
/// kayıtta firmanın varsayılan değeri atanır, güncellemede mevcut değer kalır. Yeni firmanın vergi numarası, başka bir
/// modülde açılmış bir firmanınkiyle aynıysa yeni ortak kayıt açılmaz, o firmaya bağlanır (ADR-0006).
/// </summary>
public sealed record SaveCompanyRequest(
    string Name,
    string? TaxNumber = null,
    string? Phone = null,
    string? Email = null,
    string? Website = null,
    string? City = null,
    string? Notes = null,
    long? Version = null,
    string? Address = null,
    string? Industry = null,
    string? Status = null,
    string? Source = null,
    string? Tags = null,
    Guid? OwnerId = null,
    JsonObject? Custom = null,
    Guid? PartyId = null,
    string? Kind = null,
    string? TaxOffice = null);

public sealed record ContactDto(
    Guid Id,
    long Version,
    string FirstName,
    string LastName,
    Guid? CompanyId,
    string? Title,
    string? Email,
    string? Phone,
    string? Notes,
    JsonObject? Custom = null,
    Guid? PersonId = null,
    Guid? OwnerId = null);

public sealed record SaveContactRequest(
    string FirstName,
    string LastName,
    Guid? CompanyId = null,
    string? Title = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    long? Version = null,
    JsonObject? Custom = null,
    Guid? PersonId = null,
    Guid? OwnerId = null);

/// <summary>Fırsat aşamasının hazır değerleri. Kapalı aşamayı kategoriden anlayın (<see cref="CrmLists.StageCategories"/>).</summary>
public static class OpportunityStage
{
    public const string Lead = "Lead";
    public const string Qualified = "Qualified";
    public const string Proposal = "Proposal";
    public const string Negotiation = "Negotiation";
    public const string Won = "Won";
    public const string Lost = "Lost";
}

public sealed record OpportunityDto(
    Guid Id,
    long Version,
    string Title,
    Guid CompanyId,
    Guid? ContactId,
    string Stage,
    decimal? Amount,
    string Currency,
    DateOnly? ExpectedCloseDate,
    int? Probability,
    string? LostReason,
    Guid? OwnerId,
    string? Notes,
    JsonObject? Custom = null,
    Guid? SourceLeadId = null);

public sealed record SaveOpportunityRequest(
    string Title,
    Guid CompanyId,
    Guid? ContactId = null,
    string? Stage = null,
    decimal? Amount = null,
    string Currency = "TRY",
    DateOnly? ExpectedCloseDate = null,
    string? Notes = null,
    long? Version = null,
    int? Probability = null,
    string? LostReason = null,
    Guid? OwnerId = null,
    JsonObject? Custom = null);

/// <summary>Aktivite tipinin hazır değerleri.</summary>
public static class ActivityType
{
    public const string Call = "Call";
    public const string Meeting = "Meeting";
    public const string Email = "Email";
    public const string Task = "Task";
    public const string Note = "Note";
}

public sealed record ActivityDto(
    Guid Id,
    long Version,
    string Type,
    string Subject,
    DateTimeOffset? DueAt,
    DateTimeOffset? CompletedAt,
    Guid? CompanyId,
    Guid? ContactId,
    Guid? OpportunityId,
    Guid? OwnerId,
    string? Notes,
    JsonObject? Custom = null,
    Guid? LeadId = null,
    string? Outcome = null);

public sealed record SaveActivityRequest(
    string Subject,
    string? Type = null,
    DateTimeOffset? DueAt = null,
    DateTimeOffset? CompletedAt = null,
    Guid? CompanyId = null,
    Guid? ContactId = null,
    Guid? OpportunityId = null,
    string? Notes = null,
    long? Version = null,
    Guid? OwnerId = null,
    JsonObject? Custom = null,
    Guid? LeadId = null,
    string? Outcome = null);

/// <summary>Para birimi bazında fırsat toplamı. Farklı para birimleri hiçbir zaman toplanıp tek tutar yapılmaz.</summary>
public sealed record CurrencyTotal(string Currency, decimal Amount, int Count);

/// <summary>
/// Firma detayının özeti (ADR-0010): kullanıcının erişebildiği bütün ilgili kayıtlar üzerinden sunucuda hesaplanır
/// (liste sayfalarından değil). Kullanıcının göremediği bir bölüm <c>null</c>'dır; erişim olmayan veri sıfır gibi gösterilmez.
/// Tutar toplamları (<see cref="OpenTotals"/>, <see cref="WonTotals"/>) tutar iznine bağlıdır, izin yoksa <c>null</c>.
/// </summary>
public sealed record CompanySummaryDto(
    int? OpenOpportunities,
    int? WonOpportunities,
    IReadOnlyList<CurrencyTotal>? OpenTotals,
    IReadOnlyList<CurrencyTotal>? WonTotals,
    int? OpenActivities,
    int? OverdueActivities);

/// <summary>
/// Firma–kişi ilişki rolleri (ADR-0011). Kişinin müşteri firmadaki iş rolüdür; uygulamadaki güvenlik rolleriyle ilgisi yoktur.
/// Bir ilişkiye birden fazla rol atanabilir.
/// </summary>
public static class CrmRelationshipRoles
{
    public const string Purchasing = "Purchasing";
    public const string Finance = "Finance";
    public const string Technical = "Technical";
    public const string Management = "Management";
    public const string Other = "Other";

    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        [Purchasing] = "Satın alma",
        [Finance] = "Finans",
        [Technical] = "Teknik",
        [Management] = "Yönetim",
        [Other] = "Diğer",
    };
}

public static class CrmRelationshipLimits
{
    public const int DepartmentMaxLength = 100;
    public const int JobTitleMaxLength = 100;
}

/// <summary>
/// Firma–kişi ilişkisi. <see cref="CompanyName"/> ve <see cref="ContactName"/> salt okunurdur. Biten ilişkide
/// <see cref="EndDate"/> doludur ve <see cref="IsActive"/> false'tur.
/// </summary>
public sealed record CompanyContactDto(
    Guid Id,
    long Version,
    Guid CompanyId,
    Guid ContactId,
    string? CompanyName,
    string? ContactName,
    string? Department,
    string? JobTitle,
    IReadOnlyList<string> Roles,
    bool IsPrimary,
    bool IsDecisionMaker,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsActive,
    JsonObject? Custom = null);

/// <summary>
/// Firma ve kişi yalnızca oluştururken verilir (sonra değiştirilemez); ikisine de erişim gerekir. İlişkiyi sonlandırmak için
/// <see cref="EndDate"/> verilir (boş bırakmak aktif yapar). Firmada zaten aktif bir ana irtibat varsa yenisi 422 alır.
/// </summary>
public sealed record SaveCompanyContactRequest(
    Guid CompanyId,
    Guid ContactId,
    string? Department = null,
    string? JobTitle = null,
    IReadOnlyList<string>? Roles = null,
    bool IsPrimary = false,
    bool IsDecisionMaker = false,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    long? Version = null,
    JsonObject? Custom = null);
