using Modulix.Core.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Platform;

/// <summary>
/// Platform yöneticisinin gördüğü firma: platform tablolarındaki bilgiler ve sayılar; firma verisi değildir.
/// <see cref="AdministratorCount"/> yalnızca firma detayında dolar: roller RLS altındadır ve firma başına bir kapsam
/// gerektirir; liste bu maliyeti taşımaz (ADR-0028 Dilim 2), <c>null</c> gelir.
/// </summary>
public sealed record PlatformTenant(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    DateTimeOffset CreatedAt,
    int ActiveMemberCount,
    int? AdministratorCount,
    IReadOnlyList<string> EnabledModules,
    string? ProfileKey = null,
    DateTimeOffset? ProfileAppliedAt = null);

/// <summary>Firma listesi sorgusu. Arama ad ve kısa adda yapılır; sayfa 1'den başlar.</summary>
public sealed record PlatformTenantQuery(string? Search, TenantStatus? Status, int Page, int PageSize);

public sealed record PlatformTenantPage(IReadOnlyList<PlatformTenant> Items, int Page, int PageSize, int TotalCount);

/// <summary>Firmanın Yönetici rolündeki üyesi; davet bekleyenler de listelenir.</summary>
public sealed record TenantAdministrator(Guid MembershipId, string DisplayName, string Email, MembershipStatus Status);

/// <summary>Firmanın bir modül hakkının koşulları (ADR-0028); kapalı hak da satır olarak gelir.</summary>
public sealed record TenantEntitlement(string ModuleKey, bool IsEnabled, EntitlementSource Source, DateTimeOffset? ValidUntil, string? Note);

public sealed record PlatformTenantDetail(
    PlatformTenant Tenant,
    IReadOnlyList<TenantAdministrator> Administrators,
    IReadOnlyList<TenantEntitlement>? Entitlements = null);

/// <summary>
/// Firma durumlarına göre sayılar ve süresi dolan/yaklaşan haklar (genel bakış). Yaklaşan sayıları birikimlidir (30 ⊂ 60 ⊂ 90).
/// <see cref="ExpiredEntitlements"/> bitişi geçmiş tüm haklardır (ek sürede olanlar ve <see cref="ReadOnlyEntitlements"/> dahil).
/// </summary>
public sealed record PlatformOverview(
    IReadOnlyDictionary<TenantStatus, int> TenantCounts,
    int ExpiredEntitlements,
    int ReadOnlyEntitlements,
    int ExpiringIn30Days,
    int ExpiringIn60Days,
    int ExpiringIn90Days);

/// <summary>
/// Bitiş tarihi olan açık bir modül hakkı (süre takvimi). Geçmiş tarih de listelenir. <see cref="Phase"/>: bugünkü evre;
/// <see cref="GraceEndsAt"/>: ek sürenin bittiği, yani modülün salt okunura geçtiği an.
/// </summary>
public sealed record PlatformExpiration(
    Guid TenantId,
    string TenantName,
    string TenantSlug,
    TenantStatus TenantStatus,
    string ModuleKey,
    EntitlementSource Source,
    DateTimeOffset ValidUntil,
    string? Note,
    EntitlementPhase Phase,
    DateTimeOffset GraceEndsAt);

/// <summary>Platform denetim kaydı sorgusu (ADR-0028); en yeni üstte. <see cref="TenantId"/> boşsa bütün firmalar.</summary>
public sealed record PlatformAuditQuery(Guid? TenantId, string? Action, int Page, int PageSize);

/// <summary>
/// <see cref="ActorName"/>: işlemi yapan platform yöneticisinin adı (hesap silinmişse boş). <see cref="TargetTenantName"/>:
/// firmanın bugünkü adı (hesap düzeyinde işlemde ya da firma silinmişse boş).
/// </summary>
public sealed record PlatformAuditItem(
    Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorName, string Action, Guid? TargetTenantId, string? TargetTenantName,
    string? Reason, string? Details);

public sealed record PlatformAuditPage(IReadOnlyList<PlatformAuditItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Firma açılışı. <see cref="Modules"/>: açılacak modüller (çekirdek her zaman açıktır). <see cref="RoleTemplates"/>:
/// oluşturulacak hazır roller (ADR-0005); seçilen modüllerin şablonlarından olmalıdır. <see cref="PublicUrl"/>:
/// e-postadaki bağlantıların kökü. <see cref="Profile"/>: uygulanacak sektör profili (Y9c); modülleri açmaz, profilin
/// alanları ve liste değerleri seçilen modüllerde eklenir (ekran profilin modüllerini önceden seçer).
/// </summary>
public sealed record TenantCreation(
    string Name,
    string Slug,
    IReadOnlyCollection<string> Modules,
    IReadOnlyCollection<string> RoleTemplates,
    string AdministratorEmail,
    string AdministratorName,
    string PublicUrl,
    string? Profile = null,
    bool Trial = false);

/// <summary><see cref="ExistingAccount"/>: yöneticinin e-postası platformda kayıtlıydı; üyelik hemen etkin oldu.</summary>
public sealed record TenantCreationResult(PlatformTenantDetail Tenant, bool ExistingAccount);

/// <summary>Bir modülün kaç firmada açık olduğu (yalnızca açık firmalar sayılır).</summary>
public sealed record ModuleUsage(string ModuleKey, int EnabledTenantCount);

/// <summary>
/// Platform yönetimi (Y4): firmaları açar, kapatır ve ilk yöneticiyi davet eder. Çağıran platform yöneticisi olmalıdır
/// (API her istekte <see cref="IsPlatformAdministratorAsync"/> ile denetler).
///
/// Platform yöneticisi firma verisini görmez; firmanın rolleri ve denetim kaydı gibi firma verisine yazılan her şey o
/// firmanın bağlamında (RLS altında) yazılır. Firma açılışı, ad değiştirme, kapatma, yeniden açma ve yönetici daveti
/// firmanın denetim kaydına, işlemi yapan platform yöneticisi adına <c>core.tenant</c> kaydı olarak yazılır.
///
/// Hatalar: <see cref="UserAdministrationException"/> (bulunamadı, çakışma), geçersiz alan için
/// <see cref="ArgumentException"/> (ParamName alan adıdır).
/// </summary>
public interface IPlatformAdministration
{
    /// <summary>Bayrak veritabanından okunur; kaldırılınca bir sonraki istekte geçerli olur.</summary>
    Task<bool> IsPlatformAdministratorAsync(Guid userId, CancellationToken cancellationToken);

    Task<PlatformTenantPage> ListTenantsAsync(PlatformTenantQuery query, CancellationToken cancellationToken);

    /// <summary>Firma yoksa <c>null</c>.</summary>
    Task<PlatformTenantDetail?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Firmayı tek işlemde açar: firma, Yönetici sistem rolü, seçilen modüller ve şablon rolleri, ilk yöneticinin daveti
    /// (Y2 davet akışı, Yönetici rolüyle). Biri başarısız olursa hiçbiri oluşmaz ve e-posta gitmez. Kısa ad
    /// kullanılıyorsa çakışma.
    /// </summary>
    Task<TenantCreationResult> CreateTenantAsync(TenantCreation creation, Guid actingUserId, CancellationToken cancellationToken);

    Task RenameTenantAsync(Guid tenantId, string name, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Firmayı kapatır; veri silinmez. Kullanıcılar bir sonraki isteklerinde erişimi kaybeder. Gerekçe zorunludur
    /// (<see cref="ArgumentException"/>, ParamName <c>reason</c>) ve platform denetim kaydına yazılır. Zaten kapalıysa çakışma.
    /// </summary>
    Task CloseTenantAsync(Guid tenantId, string reason, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Kapalı veya askıdaki firmayı açar. Zaten açıksa (Active, Trial) çakışma.</summary>
    Task ReopenTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken, string? reason = null);

    /// <summary>
    /// Açık firmayı (Active, Trial) askıya alır (ADR-0028): erişim durur, veri silinmez. Gerekçe zorunludur
    /// (<see cref="ArgumentException"/>, ParamName <c>reason</c>). Açık olmayan firma için çakışma.
    /// </summary>
    Task SuspendTenantAsync(Guid tenantId, string reason, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Denemedeki firmayı etkinleştirir (Trial → Active). Deneme değilse çakışma.</summary>
    Task ActivateTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken, string? reason = null);

    /// <summary>Platform denetim kaydı (ADR-0028): bütün firmalar veya tek firma; en yeni üstte, sayfa 50'yi aşmaz.</summary>
    Task<PlatformAuditPage> ListAuditAsync(PlatformAuditQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Firmaya Yönetici rolüyle bir kişi davet eder (firmanın yöneticileri ayrıldıysa kurtarma yolu). Kapalı firmaya
    /// davet gönderilemez.
    /// </summary>
    Task<InvitationResult> InviteAdministratorAsync(
        Guid tenantId, string email, string displayName, string publicUrl, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Firmada bir modülü açar veya kapatır (Y4b). Veri silinmez; rollerdeki izinler korunur ve modül yeniden açılınca
    /// geçerli olur. Değişiklik firmanın bir sonraki isteğinde geçerlidir ve firmanın denetim kaydına <c>core.tenant</c>
    /// olarak yazılır. Durum zaten istenen gibiyse ve <paramref name="terms"/> verilmediyse bir şey yapılmaz. Bilinmeyen veya
    /// çekirdek modül için <see cref="ArgumentException"/> (ParamName <c>moduleKey</c>). <paramref name="terms"/> (kaynak,
    /// bitiş, not) yalnızca açarken geçerlidir; bitiş tarihi geçince modül ek sürede çalışır, ek süre de bitince salt okunur olur (<see cref="EntitlementPhase"/>).
    /// <paramref name="reason"/> platform denetim kaydına yazılır; kapatırken ve bitiş tarihini ileri alırken (süre uzatma)
    /// zorunludur (<see cref="ArgumentException"/>, ParamName <c>reason</c>).
    /// </summary>
    Task SetModuleAsync(
        Guid tenantId, string moduleKey, bool enabled, Guid actingUserId, CancellationToken cancellationToken,
        EntitlementTerms? terms = null, string? reason = null);

    /// <summary>Genel bakış: durumlara göre firma sayıları ve süresi dolan/yaklaşan haklar. Yalnızca platform tablolarından okunur.</summary>
    Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Süre takvimi: bitiş tarihi olan açık modül hakları, bugünden <paramref name="days"/> gün sonrasına kadar ve geçmiş
    /// olanlar dahil (evreleriyle); yalnızca erişime açık firmalar (Active, Trial). Bitişi en yakın olan üstte.
    /// </summary>
    Task<IReadOnlyList<PlatformExpiration>> ListExpirationsAsync(int days, CancellationToken cancellationToken);

    /// <summary>
    /// Sektör profilinin firmaya ekleyeceği şeyler (Y9c); platform yöneticisi kapalı modülleri de açar. Tanımsız profil için
    /// null; firma yoksa <see cref="UserAdministrationException"/> (NotFound).
    /// </summary>
    Task<ProfilePlan?> PreviewProfileAsync(Guid tenantId, string profileKey, CancellationToken cancellationToken);

    /// <summary>Profili uygular (yalnızca ekler) ve firmanın denetim kaydına yazar. Tanımsız profil için null.</summary>
    Task<ProfilePlan?> ApplyProfileAsync(Guid tenantId, string profileKey, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Katalogdaki modüllerin (çekirdek hariç) kaç açık firmada açık olduğu, katalog sırasıyla.</summary>
    Task<IReadOnlyList<ModuleUsage>> GetModuleUsageAsync(CancellationToken cancellationToken);
}
