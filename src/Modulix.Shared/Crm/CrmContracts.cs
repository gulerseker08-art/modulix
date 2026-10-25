using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Modulix.Shared.Crm;

// CRM uç noktalarının (/api/crm/...) sözleşmeleri. Alan adları kayıt boru hattındaki teknik adlarla
// aynıdır (camelCase); sunucu aynı kuralları yeniden doğrular. Kaydetme isteklerinde Version verilirse
// eşzamanlı değişiklik 409 ile reddedilir. Listeler sayfalıdır: CrmPage<T>.

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
    public const string OpportunitiesRead = "crm.opportunities.read";
    public const string OpportunityAmountsRead = "crm.opportunity-amounts.read";
    public const string OpportunitiesWrite = "crm.opportunities.write";
    public const string ActivitiesRead = "crm.activities.read";
    public const string ActivitiesWrite = "crm.activities.write";
}

/// <summary>Kayıt türü anahtarları; kayıt geçmişi (<c>/api/records/{tür}/{id}/history</c>) bunlarla çağrılır.</summary>
public static class CrmEntityKeys
{
    public const string Company = "crm.company";
    public const string Contact = "crm.contact";
    public const string Opportunity = "crm.opportunity";
    public const string Activity = "crm.activity";
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
    Guid? PartyId = null);

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
    JsonObject? Custom = null);

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
    Guid? PersonId = null);

public sealed record SaveContactRequest(
    string FirstName,
    string LastName,
    Guid? CompanyId = null,
    string? Title = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    long? Version = null,
    JsonObject? Custom = null);

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
    JsonObject? Custom = null);

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
    JsonObject? Custom = null);

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
    JsonObject? Custom = null);
