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

[JsonConverter(typeof(JsonStringEnumConverter<CompanyStatus>))]
public enum CompanyStatus
{
    Prospect = 1,
    Customer = 2,
    Inactive = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<CompanySource>))]
public enum CompanySource
{
    Referral = 1,
    Website = 2,
    Event = 3,
    Advertising = 4,
    ColdCall = 5,
    Other = 6,
}

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
    CompanyStatus Status,
    CompanySource? Source,
    string? Tags,
    Guid? OwnerId,
    string? Notes);

/// <summary>Sorumlu boş bırakılırsa kaydı oluşturan kullanıcı atanır.</summary>
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
    CompanyStatus Status = CompanyStatus.Prospect,
    CompanySource? Source = null,
    string? Tags = null,
    Guid? OwnerId = null);

public sealed record ContactDto(
    Guid Id,
    long Version,
    string FirstName,
    string LastName,
    Guid? CompanyId,
    string? Title,
    string? Email,
    string? Phone,
    string? Notes);

public sealed record SaveContactRequest(
    string FirstName,
    string LastName,
    Guid? CompanyId = null,
    string? Title = null,
    string? Email = null,
    string? Phone = null,
    string? Notes = null,
    long? Version = null);

[JsonConverter(typeof(JsonStringEnumConverter<OpportunityStage>))]
public enum OpportunityStage
{
    Lead = 1,
    Qualified = 2,
    Proposal = 3,
    Negotiation = 4,
    Won = 5,
    Lost = 6,
}

public sealed record OpportunityDto(
    Guid Id,
    long Version,
    string Title,
    Guid CompanyId,
    Guid? ContactId,
    OpportunityStage Stage,
    decimal? Amount,
    string Currency,
    DateOnly? ExpectedCloseDate,
    int? Probability,
    string? LostReason,
    Guid? OwnerId,
    string? Notes);

public sealed record SaveOpportunityRequest(
    string Title,
    Guid CompanyId,
    Guid? ContactId = null,
    OpportunityStage Stage = OpportunityStage.Lead,
    decimal? Amount = null,
    string Currency = "TRY",
    DateOnly? ExpectedCloseDate = null,
    string? Notes = null,
    long? Version = null,
    int? Probability = null,
    string? LostReason = null,
    Guid? OwnerId = null);

[JsonConverter(typeof(JsonStringEnumConverter<ActivityType>))]
public enum ActivityType
{
    Call = 1,
    Meeting = 2,
    Email = 3,
    Task = 4,
    Note = 5,
}

public sealed record ActivityDto(
    Guid Id,
    long Version,
    ActivityType Type,
    string Subject,
    DateTimeOffset? DueAt,
    DateTimeOffset? CompletedAt,
    Guid? CompanyId,
    Guid? ContactId,
    Guid? OpportunityId,
    Guid? OwnerId,
    string? Notes);

public sealed record SaveActivityRequest(
    string Subject,
    ActivityType Type = ActivityType.Task,
    DateTimeOffset? DueAt = null,
    DateTimeOffset? CompletedAt = null,
    Guid? CompanyId = null,
    Guid? ContactId = null,
    Guid? OpportunityId = null,
    string? Notes = null,
    long? Version = null,
    Guid? OwnerId = null);
