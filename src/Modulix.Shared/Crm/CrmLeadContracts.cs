using System.Text.Json.Nodes;

namespace Modulix.Shared.Crm;

// Lead (potansiyel müşteri / satış talebi) uç noktalarının sözleşmeleri: /api/crm/leads. Liste/detay/oluşturma/güncelleme/silme
// diğer CRM kaynaklarıyla aynı kalıptadır (Version zorunlu). Durum, sorumlu ve dönüşüm alanları genel PUT ile DEĞİŞMEZ; ilgili
// eylem uç noktaları kullanılır (assign, start-contacting, qualify, nurture, disqualify, reopen, convert).

public static class LeadStatus
{
    public const string New = "New";
    public const string Contacting = "Contacting";
    public const string Qualified = "Qualified";
    public const string Nurturing = "Nurturing";
    public const string Disqualified = "Disqualified";
    public const string Converted = "Converted";

    public static IReadOnlyList<string> All { get; } = [New, Contacting, Qualified, Nurturing, Disqualified, Converted];

    public static bool IsOpen(string status) => status is New or Contacting or Qualified or Nurturing;

    public static string Label(string status) => status switch
    {
        New => "Yeni",
        Contacting => "İletişim kuruluyor",
        Qualified => "Nitelikli",
        Nurturing => "Daha sonra takip",
        Disqualified => "Elendi",
        Converted => "Fırsata dönüştürüldü",
        _ => status,
    };
}

public static class LeadPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static IReadOnlyList<string> All { get; } = [Low, Normal, High, Urgent];

    public static string Label(string priority) => priority switch
    {
        Low => "Düşük",
        Normal => "Normal",
        High => "Yüksek",
        Urgent => "Acil",
        _ => priority,
    };
}

public static class CrmLeadLists
{
    public const string Source = "crm.lead-source";
    public const string DisqualificationReason = "crm.lead-disqualification-reason";
}

/// <summary>Aktivite sonucu (Lead'in ilk teması için); sunucudaki <c>CrmModule.ActivityOutcomes</c> ile aynı.</summary>
public static class ActivityOutcome
{
    public const string Reached = "Reached";
    public const string NoAnswer = "NoAnswer";
    public const string Sent = "Sent";

    public static string Label(string outcome) => outcome switch
    {
        Reached => "Ulaşıldı",
        NoAnswer => "Ulaşılamadı",
        Sent => "Gönderildi",
        _ => outcome,
    };
}

public sealed record LeadDto(
    Guid Id,
    long Version,
    string LeadNumber,
    string Subject,
    string? Description,
    string Status,
    string? StatusReason,
    string Priority,
    string? FirstName,
    string? LastName,
    string? CompanyName,
    string? Email,
    string? Phone,
    string? JobTitle,
    string? Website,
    string? Country,
    string? City,
    Guid? CompanyId,
    Guid? ContactId,
    string? ProductInterest,
    string? NeedDescription,
    decimal? EstimatedBudget,
    string Currency,
    DateOnly? TargetPurchaseDate,
    string? Industry,
    string? Source,
    string? SourceDetail,
    string? Referrer,
    string? ExternalSource,
    string? ExternalSubmissionId,
    string? UtmSource,
    string? UtmMedium,
    string? UtmCampaign,
    DateTimeOffset FirstReceivedAt,
    Guid? OwnerId,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? FirstContactAt,
    DateTimeOffset? FollowUpAt,
    DateTimeOffset? QualifiedAt,
    DateTimeOffset? DisqualifiedAt,
    string? DisqualificationReason,
    DateTimeOffset? ConvertedAt,
    Guid? ConvertedBy,
    Guid? ConvertedCompanyId,
    Guid? ConvertedContactId,
    Guid? ConvertedOpportunityId,
    JsonObject? Custom = null,
    List<string>? ServiceScope = null,
    string? IndustryName = null,
    string? LegacyIndustry = null);

/// <summary>
/// Manuel kayıt/güncelleme. Konu zorunludur; talep sahibini tanıtan en az bir bilgi (ad, firma adı, e-posta, telefon ya da mevcut
/// firma/kişi) gerekir. Tahmini bütçe ve hedef tarih bilinmiyorsa boş kalır. <c>OwnerId</c> yalnızca oluştururken ve yalnızca atama
/// izniyle başkasına verilebilir; güncellemede yok sayılır (atama için assign eylemi kullanılır).
/// </summary>
public sealed record SaveLeadRequest(
    string Subject,
    string? Description = null,
    string Priority = "Normal",
    string? FirstName = null,
    string? LastName = null,
    string? CompanyName = null,
    string? Email = null,
    string? Phone = null,
    string? JobTitle = null,
    string? Website = null,
    string? Country = null,
    string? City = null,
    Guid? CompanyId = null,
    Guid? ContactId = null,
    string? ProductInterest = null,
    string? NeedDescription = null,
    decimal? EstimatedBudget = null,
    string Currency = "TRY",
    DateOnly? TargetPurchaseDate = null,
    string? Industry = null,
    string? Source = null,
    string? SourceDetail = null,
    string? Referrer = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    Guid? OwnerId = null,
    long? Version = null,
    JsonObject? Custom = null,
    List<string>? ServiceScope = null);

// ---- Eylemler (hepsi kaydın sürümünü ister; eski sürüm 409)

public sealed record AssignLeadRequest(Guid OwnerId, long Version);

public sealed record LeadVersionRequest(long Version);

public sealed record QualifyLeadRequest(long Version, string? Note = null);

public sealed record DisqualifyLeadRequest(long Version, string Reason, string? Note = null);

public sealed record NurtureLeadRequest(long Version, DateTimeOffset? FollowUpAt = null, string? Note = null);

public sealed record ReopenLeadRequest(long Version, string Reason);

// ---- Dönüşüm

public sealed record LeadConversionPreviewDto(
    Guid LeadId,
    string Status,
    IReadOnlyList<string> Blockers,
    string? CompanyName,
    string CompanyKind,
    string? ContactFirstName,
    string? ContactLastName,
    string OpportunityTitle,
    decimal? Amount,
    string Currency,
    DateOnly? ExpectedCloseDate,
    Guid? ExistingCompanyId,
    Guid? ExistingContactId);

/// <summary>
/// Dönüşüm isteği. Firma: <c>CompanyId</c> (mevcut) ya da <c>CreateCompany</c> (yeni) — ikisinden tam biri. Kişi isteğe bağlı.
/// Mevcut firma/kişi kayıtlarının verisi değiştirilmez.
/// </summary>
public sealed record ConvertLeadRequest(
    long Version,
    Guid? CompanyId = null,
    bool CreateCompany = false,
    string? CompanyName = null,
    string? CompanyKind = null,
    Guid? ContactId = null,
    bool CreateContact = false,
    string? ContactFirstName = null,
    string? ContactLastName = null,
    string? OpportunityTitle = null,
    decimal? Amount = null,
    string? Currency = null,
    DateOnly? ExpectedCloseDate = null,
    string? Stage = null);

public sealed record ConvertLeadResultDto(
    LeadDto Lead, Guid CompanyId, Guid? ContactId, Guid OpportunityId, bool CompanyCreated, bool ContactCreated);

// ---- Olası eşleşmeler (mükerrer incelemesi; hiçbir şey otomatik silinmez/birleştirilmez)

/// <summary>
/// Olası eşleşme. Yalnızca çağıranın görebildiği kayıtlar gelir; ad ve eşleşme nedeni dışında bilgi verilmez. "Aynı e-posta"
/// tek başına mükerrer kanıtı değildir: aynı kişinin farklı ihtiyacı yeni bir Lead olabilir.
/// </summary>
public sealed record LeadMatchDto(string Kind, Guid Id, string Title, string? Status, IReadOnlyList<string> Reasons);

// ---- Ayarlar ve raporlar

public sealed record LeadSettingsDto(int? FirstContactTargetHours);

/// <summary>
/// Lead raporu. Tanımlar:
/// <list type="bullet">
///   <item><b>Dönem kohortu</b>: <c>FirstReceivedAt</c> [From, To) aralığında gelen Lead'ler. Duruma/kaynağa/sorumluya göre
///   dağılım, ilk temas ve dönüşüm oranı bu kohorta aittir; dönemde yapılan ama başka dönemde gelmiş Lead'lerin dönüşümü
///   <see cref="ConvertedInPeriod"/> ile ayrıca gösterilir ve oranın parçası DEĞİLDİR.</item>
///   <item><b>Dönüşüm oranı</b> = kohortta Converted olanlar / kohort sayısı; kohort boşsa <c>null</c> (yüzde uydurulmaz).</item>
///   <item><b>İlk temas süresi</b> = FirstContactAt − FirstReceivedAt (takvim saati); yalnızca teması olan kohort Lead'lerinin
///   ortalama ve medyanı; hiç yoksa <c>null</c>. Hedef aşımı: hedef ayarlıysa, süresi hedefi aşan temaslı Lead'ler ile hedef süre
///   geçtiği halde hâlâ temassız olan açık Lead'ler.</item>
///   <item><b>Dönüşüme kadar geçen süre</b> = ConvertedAt − FirstReceivedAt (takvim saati), dönüşmüş kohort Lead'leri.</item>
///   <item>Tüm sayılar çağıranın erişim kapsamıyla sınırlıdır; <c>Truncated</c> kapsam kayıt üst sınırını aştıysa doğrudur.</item>
/// </list>
/// </summary>
public sealed record LeadReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int CohortCount,
    IReadOnlyList<LeadCount> BySource,
    IReadOnlyList<LeadCount> ByStatus,
    IReadOnlyList<LeadOwnerCount> OpenByOwner,
    int WithFirstContact,
    double? AverageFirstContactHours,
    double? MedianFirstContactHours,
    int? FirstContactTargetHours,
    int? FirstContactTargetBreaches,
    int ConvertedFromCohort,
    double? ConversionRate,
    double? AverageHoursToConvert,
    int ConvertedInPeriod,
    IReadOnlyList<LeadCount> DisqualificationReasons,
    bool Truncated);

public sealed record LeadCount(string Key, int Count);

/// <summary>Sorumluya göre açık Lead sayısı; <c>OwnerId</c> boşsa atanmamıştır.</summary>
public sealed record LeadOwnerCount(Guid? OwnerId, int Count);

// ---- Web formu / entegrasyon (herkese açık uç)

/// <summary>
/// Web formu gönderimi (<c>POST /api/public/lead-forms/{formKey}</c>). Yalnızca bu alanlar kabul edilir; bilinmeyen alan 400
/// döner. Tenant, durum, sorumlu, öncelik, iş birimi ve rol bu istekte BULUNMAZ: sunucu form tanımından atar.
/// <c>SubmissionId</c> istemcinin ürettiği tekil gönderim kimliğidir; aynı kimlikle tekrar gönderim yeni Lead üretmez.
/// <c>Website2</c> bal tuzağıdır (gizli alan): dolu gelirse gönderim sessizce kabul edilmiş gibi yanıtlanır ama kaydedilmez.
/// Gönderim pazarlama izni sayılmaz; bu sürümde izin kaydı tutulmaz.
/// </summary>
public sealed record PublicLeadFormRequest(
    string SubmissionId,
    string? Subject = null,
    string? Message = null,
    string? FirstName = null,
    string? LastName = null,
    string? CompanyName = null,
    string? Email = null,
    string? Phone = null,
    string? JobTitle = null,
    string? Country = null,
    string? City = null,
    string? ProductInterest = null,
    string? Referrer = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? Website2 = null);

/// <summary>Web formu yanıtı: kayıt ayrıntısı (mevcut müşteri/e-posta bilgisi) asla verilmez.</summary>
public sealed record PublicLeadFormResponse(bool Accepted, string? Reference);


/// <summary>Web formu/entegrasyon tanımı. Entegrasyon sırrı hiçbir zaman döndürülmez (yalnızca yenilemede bir kez, <see cref="LeadFormSecretDto"/>).</summary>
public sealed record LeadFormDto(
    Guid Id,
    long Version,
    string Name,
    string FormKey,
    bool IsActive,
    Guid? DefaultOwnerId,
    string? AllowedOrigins,
    bool ServerOnly,
    DateTimeOffset? SecretRotatedAt);

/// <summary>Form oluştur/güncelle. Form kimliği sunucuca üretilir. <c>AllowedOrigins</c>: https://site.com biçiminde, virgül ya da satırla ayrılmış.</summary>
public sealed record SaveLeadFormRequest(
    string Name,
    bool IsActive = true,
    Guid? DefaultOwnerId = null,
    string? AllowedOrigins = null,
    bool ServerOnly = false,
    long? Version = null);

/// <summary>Yeni entegrasyon sırrı: yalnızca bu yanıtta görünür, saklanmaz (özeti saklanır).</summary>
public sealed record LeadFormSecretDto(LeadFormDto Form, string Secret);
