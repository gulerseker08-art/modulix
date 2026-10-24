namespace Modulix.Core.Platform;

/// <summary>Platform yöneticisinin gördüğü firma: platform tablolarındaki bilgiler ve sayılar; firma verisi değildir.</summary>
public sealed record PlatformTenant(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    DateTimeOffset CreatedAt,
    int ActiveMemberCount,
    int AdministratorCount,
    IReadOnlyList<string> EnabledModules);

/// <summary>Firma listesi sorgusu. Arama ad ve kısa adda yapılır; sayfa 1'den başlar.</summary>
public sealed record PlatformTenantQuery(string? Search, TenantStatus? Status, int Page, int PageSize);

public sealed record PlatformTenantPage(IReadOnlyList<PlatformTenant> Items, int Page, int PageSize, int TotalCount);

/// <summary>Firmanın Yönetici rolündeki üyesi; davet bekleyenler de listelenir.</summary>
public sealed record TenantAdministrator(Guid MembershipId, string DisplayName, string Email, MembershipStatus Status);

public sealed record PlatformTenantDetail(PlatformTenant Tenant, IReadOnlyList<TenantAdministrator> Administrators);

/// <summary>
/// Firma açılışı. <see cref="Modules"/>: açılacak modüller (çekirdek her zaman açıktır). <see cref="RoleTemplates"/>:
/// oluşturulacak hazır roller (ADR-0005); seçilen modüllerin şablonlarından olmalıdır. <see cref="PublicUrl"/>:
/// e-postadaki bağlantıların kökü.
/// </summary>
public sealed record TenantCreation(
    string Name,
    string Slug,
    IReadOnlyCollection<string> Modules,
    IReadOnlyCollection<string> RoleTemplates,
    string AdministratorEmail,
    string AdministratorName,
    string PublicUrl);

/// <summary><see cref="ExistingAccount"/>: yöneticinin e-postası platformda kayıtlıydı; üyelik hemen etkin oldu.</summary>
public sealed record TenantCreationResult(PlatformTenantDetail Tenant, bool ExistingAccount);

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

    /// <summary>Firmayı kapatır; veri silinmez. Kullanıcılar bir sonraki isteklerinde erişimi kaybeder.</summary>
    Task CloseTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken);

    Task ReopenTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Firmaya Yönetici rolüyle bir kişi davet eder (firmanın yöneticileri ayrıldıysa kurtarma yolu). Kapalı firmaya
    /// davet gönderilemez.
    /// </summary>
    Task<InvitationResult> InviteAdministratorAsync(
        Guid tenantId, string email, string displayName, string publicUrl, Guid actingUserId, CancellationToken cancellationToken);
}
