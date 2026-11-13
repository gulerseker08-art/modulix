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
    public const string OpportunitiesChangeStage = "crm.opportunities.change-stage";
    public const string OpportunitiesClose = "crm.opportunities.close";
    public const string OpportunitiesReopen = "crm.opportunities.reopen";
    public const string OpportunitiesExport = "crm.opportunities.export";
    public const string ActivitiesRead = "crm.activities.read";
    public const string ActivitiesWrite = "crm.activities.write";
    public const string ActivitiesAssign = "crm.activities.assign";
    public const string ActivitiesReopen = "crm.activities.reopen";
    public const string ActivitiesManageCalendar = "crm.activities.manage-calendar";
    public const string ActivitiesViewPrivate = "crm.activities.view-private";

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
    public const string ProductsRead = "crm.products.read";
    public const string ProductsWrite = "crm.products.write";
    public const string ProductCostsRead = "crm.product-costs.read";
    public const string PricesRead = "crm.prices.read";
    public const string PricesWrite = "crm.prices.write";
    public const string QuotesRead = "crm.quotes.read";
    public const string QuotesWrite = "crm.quotes.write";
    public const string QuotesDiscount = "crm.quotes.discount";
    public const string QuotesManualPrice = "crm.quotes.manual-price";
    public const string QuotesApprove = "crm.quotes.approve";
    public const string QuotesPresent = "crm.quotes.present";
    public const string QuotesRecordDecision = "crm.quotes.record-decision";
    public const string QuotesCancel = "crm.quotes.cancel";
    public const string QuotesShare = "crm.quotes.share";
    public const string QuoteRulesManage = "crm.quote-rules.manage";
    public const string QuotesExport = "crm.quotes.export";
    public const string QuoteCostsRead = "crm.quote-costs.read";

    // Satış raporlama ve tahmin (ADR-0035)
    public const string SalesReportsRead = "crm.sales-reports.read";
    public const string SalesTargetsRead = "crm.sales-targets.read";
    public const string SalesTargetsManage = "crm.sales-targets.manage";
    public const string ForecastsRead = "crm.forecasts.read";
    public const string ForecastsWrite = "crm.forecasts.write";
    public const string ForecastsSubmit = "crm.forecasts.submit";
    public const string ForecastsLock = "crm.forecasts.lock";
    public const string ForecastsReopen = "crm.forecasts.reopen";
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
    public const string OpportunityContact = "crm.opportunity-contact";
    public const string OpportunityCompetitor = "crm.opportunity-competitor";
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
    public const string OpportunityType = "crm.opportunity-type";
    public const string OpportunityLossReason = "crm.opportunity-loss-reason";

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

/// <summary>
/// CRM firması. Ad, vergi no, iletişim, adres ve sektör ortak firma kaydındadır (<see cref="PartyId"/>, ADR-0006). <see cref="Industry"/>
/// sektör kataloğundaki koddur (ADR-0042), <see cref="IndustryName"/> katalogdaki adıdır; <see cref="LegacyIndustry"/> katalog öncesi
/// girilmiş, kataloğa eşlenememiş serbest metindir (salt okunur; sektör seçilince temizlenir).
/// </summary>
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
    string? TaxOffice = null,
    string? IndustryName = null,
    string? LegacyIndustry = null);

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
    Guid? SourceLeadId = null,
    string? OpportunityNumber = null,
    string? Description = null,
    string Status = OpportunityStatus.Open,
    string? StatusReason = null,
    string Priority = "Normal",
    string? Type = null,
    string? Source = null,
    decimal? CustomerBudget = null,
    string BudgetStatus = OpportunityBudgetStatus.Unknown,
    string? NeedDescription = null,
    string? CurrentProblem = null,
    string? ExpectedBenefit = null,
    string? Scope = null,
    string? SuccessCriteria = null,
    string? Risks = null,
    string? DecisionProcess = null,
    DateOnly? TargetDecisionDate = null,
    string? NextAction = null,
    DateOnly? NextActionDate = null,
    DateTimeOffset? LastInteractionAt = null,
    DateTimeOffset? StageEnteredAt = null,
    bool ProbabilityManual = false,
    DateOnly? ActualCloseDate = null,
    decimal? WonAmount = null,
    string? WonCurrency = null,
    string? WinReason = null,
    string? HandoverNote = null,
    string? LossReason = null,
    Guid? WinningCompetitorId = null,
    DateOnly? RecontactDate = null,
    DateOnly? HoldReviewDate = null,
    int ClosureCycle = 1,
    int CloseDateChangeCount = 0,
    Guid? PrimaryQuoteId = null,
    List<string>? ServiceScope = null);

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
    JsonObject? Custom = null,
    string? Description = null,
    string? Priority = null,
    string? Type = null,
    string? Source = null,
    decimal? CustomerBudget = null,
    string? BudgetStatus = null,
    string? NeedDescription = null,
    string? CurrentProblem = null,
    string? ExpectedBenefit = null,
    string? Scope = null,
    string? SuccessCriteria = null,
    string? Risks = null,
    string? DecisionProcess = null,
    DateOnly? TargetDecisionDate = null,
    string? NextAction = null,
    DateOnly? NextActionDate = null,
    List<string>? ServiceScope = null);

/// <summary>Aktivite tipinin hazır değerleri.</summary>
public static class ActivityType
{
    public const string Call = "Call";
    public const string Meeting = "Meeting";
    public const string Email = "Email";
    public const string Task = "Task";
    public const string Note = "Note";
    public const string Visit = "Visit";
    public const string FollowUp = "FollowUp";
}

public static class ActivityStatus
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Blocked = "Blocked";
    public const string Completed = "Completed";
    public const string NoAnswer = "NoAnswer";
    public const string NoShow = "NoShow";
    public const string Cancelled = "Cancelled";
    public const string Draft = "Draft";
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";

    public static bool IsOpen(string status) => status is Open or InProgress or Blocked or Draft or Queued or Failed;
}

/// <summary>Aktivite ailesi (tür listesinin kategorisi); durum makinesi ve ekran etiketleri aileye göredir.</summary>
public static class ActivityFamily
{
    public const string Task = "task";
    public const string Call = "call";
    public const string Meeting = "meeting";
    public const string Visit = "visit";
    public const string Email = "email";
}

public static class ActivityPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";
}

/// <summary>
/// Aktivite (ADR-0015). <see cref="Masked"/> doluysa kayıt özeldir ve çağıran ayrıntıyı göremez: yalnızca zaman bilgisi ve sorumlu gelir,
/// konu "Meşgul"dür. Gecikme saklanmaz; <see cref="IsOverdue"/> açık durum ve son tarihten türetilir.
/// </summary>
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
    string? Outcome = null,
    string Status = "Open",
    string? StatusReason = null,
    string Priority = "Normal",
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    bool IsAllDay = false,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? TimeZone = null,
    string? Location = null,
    string? OnlineMeetingUrl = null,
    bool IsPrivate = false,
    int? EstimatedMinutes = null,
    int? ActualMinutes = null,
    int RescheduleCount = 0,
    Guid? PreviousActivityId = null,
    string Source = "User",
    Guid? CreatedBy = null,
    bool Masked = false)
{
    public bool IsOverdue(DateTimeOffset now) => ActivityStatus.IsOpen(Status) && DueAt is { } due && due < now;
}

/// <summary>
/// Aktivite kaydetme isteği (PUT kaydın tamamını yazar). Durum, gerekçe, tamamlanma (güncellemede), sonuç (güncellemede) ve
/// sorumlu (güncellemede) buradan değişmez; bunlar eylem uç noktalarıdır (complete, cancel, reopen, reschedule, reassign...).
/// Oluştururken <c>CompletedAt</c> verilirse geçmiş tarihli, tamamlanmış bir etkileşim kaydı açılır.
/// </summary>
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
    string? Outcome = null,
    string? Priority = null,
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    bool IsAllDay = false,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? TimeZone = null,
    string? Location = null,
    string? OnlineMeetingUrl = null,
    bool IsPrivate = false,
    int? EstimatedMinutes = null,
    int? ActualMinutes = null);

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
