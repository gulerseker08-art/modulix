namespace Modulix.Shared.Access;

// API ile arayüz (Modulix.Web) arasında paylaşılan modül, rol ve izin sözleşmeleri.

/// <summary>Kayıtlı bir modül ve aktif tenant için açık olup olmadığı.</summary>
public sealed record ModuleDto(string Key, string DisplayName, bool IsEnabled);

/// <summary>
/// Atanabilir izin. Rol ekranı izinleri modüle (<see cref="ModuleName"/>) ve kaynağa (<see cref="ResourceName"/>, ör.
/// "Firmalar") göre gruplar; aynı kaynağın okuma ve yazma izinleri matriste aynı satırdadır.
/// </summary>
public sealed record PermissionDto(string Key, string DisplayName, string ModuleKey, string ModuleName = "", string ResourceName = "");

/// <summary>
/// Oturumdaki kullanıcının aktif tenant'taki erişimi; arayüz menüleri buna göre gösterir.
/// <see cref="IsAdministrator"/>: Yönetici (sistem) rolü var; yönetici rolünü vermek gibi işlemler yalnızca ona açıktır.
/// <see cref="Companies"/> ve <see cref="ActiveCompanyId"/>: erişilebilir işletici şirketler ve aktif olanı (ADR-0008);
/// şirket seçici yalnızca birden fazla şirket erişilebilirse gösterilir. Erişim yoksa liste boş, aktif şirket <c>null</c>'dır.
/// </summary>
public sealed record MyAccessResponse(
    IReadOnlyList<string> EnabledModules,
    IReadOnlyList<string> Permissions,
    bool IsAdministrator = false,
    RecordScope Scope = RecordScope.All,
    IReadOnlyList<OperatingCompanyDto>? Companies = null,
    Guid? ActiveCompanyId = null,
    IReadOnlyList<ModuleLicenseDto>? Licenses = null)
{
    public IReadOnlyList<OperatingCompanyDto> Companies { get; init; } = Companies ?? [];

    /// <summary>Süresi dolmuş, ek sürede ya da yakında dolacak modül hakları (ADR-0028); yöneticiye uyarı için.</summary>
    public IReadOnlyList<ModuleLicenseDto> Licenses { get; init; } = Licenses ?? [];
}

/// <summary>Modül hakkının süre evresi: ek sürede yazma açık, salt okunurda kapalıdır.</summary>
public enum LicensePhase
{
    Active = 1,
    Grace = 2,
    ReadOnly = 3,
}

/// <summary>Bir modül hakkının bitişi ve evresi. <see cref="GraceEndsAt"/>: ek sürenin bittiği, yani salt okunura geçilen an.</summary>
public sealed record ModuleLicenseDto(string ModuleKey, string DisplayName, LicensePhase Phase, DateTimeOffset ValidUntil, DateTimeOffset GraceEndsAt);

/// <summary>
/// Erişilebilir bir işletici şirket (ADR-0008); müşteri firma (CRM <c>CompanyDto</c>, Taraf) değildir.
/// <see cref="IsDefault"/>: tenant'ın varsayılan şirketi.
/// </summary>
public sealed record OperatingCompanyDto(Guid Id, string Name, bool IsDefault);

/// <summary>İşletici şirket oluşturma veya yeniden adlandırma isteği (ADR-0008); CRM müşteri firmasından (Taraf) ayrıdır.</summary>
public sealed record SaveOperatingCompanyRequest(string Name);

/// <summary>
/// Bir üyeliğin bir şirkete görevi (ADR-0008, adım 5). <see cref="ValidFrom"/>/<see cref="ValidUntil"/> boşsa süresiz
/// kalıcı görev; <see cref="ValidUntil"/> dolu ise geçici vekâlettir ve o ana gelince erişim kendiliğinden kapanır.
/// <see cref="BranchId"/>: görevin idari varsayılan şubesi (kapsam sınırı değildir); boş olabilir.
/// </summary>
public sealed record CompanyAssignment(
    Guid CompanyId,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null,
    Guid? BranchId = null);

/// <summary>Üyeliğin şirket görevlerini verilen kümeye eşitler (ADR-0008); her görev tarih ve varsayılan şube taşıyabilir.</summary>
public sealed record SetMembershipCompaniesRequest(IReadOnlyList<CompanyAssignment> Assignments);

/// <summary>Bir işletici şirketin şubesi (ADR-0008); idari birimdir, kapsam sınırı değildir.</summary>
public sealed record BranchDto(Guid Id, Guid CompanyId, string Name);

/// <summary>Şube oluşturma veya yeniden adlandırma isteği (ADR-0008).</summary>
public sealed record SaveBranchRequest(string Name);

/// <summary>
/// Rolün kayıt kapsamı (Y8): izinler "hangi tür kaydı", kapsam "hangi kayıtları" söyler. Sorumlusu olan kayıtlara
/// uygulanır; birden fazla rolde en geniş kapsam geçerlidir. Değerler sunucudakiyle aynıdır.
/// </summary>
public enum RecordScope
{
    All = 1,
    Team = 2,
    Own = 3,
    Unit = 4,
    UnitAndBelow = 5,
}

public static class RecordScopeLabels
{
    public static string Of(RecordScope scope) => scope switch
    {
        RecordScope.All => "Tüm kayıtlar",
        RecordScope.UnitAndBelow => "Departmanın ve alt departmanların kayıtları",
        RecordScope.Unit => "Departmanın kayıtları",
        RecordScope.Team => "Ekibinin kayıtları",
        RecordScope.Own => "Yalnızca kendi kayıtları",
        _ => scope.ToString(),
    };

    public static string Describe(RecordScope scope) => scope switch
    {
        RecordScope.All => "Firmadaki bütün kayıtlar.",
        RecordScope.UnitAndBelow => "Kendi departmanındaki ve alt departmanlardaki kişilerin sorumlu olduğu kayıtlar.",
        RecordScope.Unit => "Kendi departmanındaki kişilerin sorumlu olduğu kayıtlar.",
        RecordScope.Team => "Kendisinin ve ekip arkadaşlarının sorumlu olduğu kayıtlar.",
        RecordScope.Own => "Yalnızca sorumlusu kendisi olan kayıtlar.",
        _ => string.Empty,
    };
}

/// <summary>
/// Kayıt kapsamlarının genişlik ve kapsama kuralları (sunucudaki <c>RecordScopeRules</c> ile aynı); sayısal değere bakılmaz.
/// Ekip ile departman birbirini kapsamaz. Karar sunucuda verilir; ekran yalnızca seçenekleri önceden süzmek için kullanır.
/// </summary>
public static class RecordScopeRules
{
    public static int Breadth(RecordScope scope) => scope switch
    {
        RecordScope.All => 5,
        RecordScope.UnitAndBelow => 4,
        RecordScope.Unit => 3,
        RecordScope.Team => 2,
        _ => 1,
    };

    public static bool Covers(RecordScope holder, RecordScope wanted) =>
        holder == RecordScope.All
        || holder == wanted
        || wanted == RecordScope.Own
        || (holder == RecordScope.UnitAndBelow && wanted == RecordScope.Unit);
}

/// <summary>Rolün hangi düzeyde atanabileceği (ADR-0026 K6): kullanıcıya, departmana ya da ikisine.</summary>
public enum RoleAssignLevel
{
    User = 1,
    Unit = 2,
    Both = 3,
}

public static class RoleAssignLevelLabels
{
    public static string Of(RoleAssignLevel level) => level switch
    {
        RoleAssignLevel.User => "Yalnızca kullanıcıya",
        RoleAssignLevel.Unit => "Yalnızca departmana",
        RoleAssignLevel.Both => "Kullanıcıya ve departmana",
        _ => level.ToString(),
    };
}

/// <summary>
/// Sistem rolünde (<see cref="IsSystem"/>, "Yönetici") izin listesi boştur: rol tüm izinleri verir ve düzenlenemez.
/// <see cref="MemberCount"/> rol listesinde doldurulur.
/// </summary>
public sealed record RoleDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> Permissions,
    bool IsSystem = false,
    int MemberCount = 0,
    RecordScope Scope = RecordScope.All,
    RoleAssignLevel AssignLevel = RoleAssignLevel.User,
    int UnitCount = 0,
    bool HideOutOfScopeShared = false,
    IReadOnlyDictionary<string, RecordScope>? ScopeOverrides = null);

/// <summary>
/// Rolde kapsamı ayarlanabilen kayıt türü; <see cref="MaxScope"/> platformun o tür için koyduğu tavandır (yoksa <c>null</c>).
/// Rol ekranı tavanı aşan kapsam seçeneklerini kapalı gösterir; sunucu yine de aşan rolü reddeder (409).
/// </summary>
public sealed record RoleScopeTypeDto(string EntityKey, string ModuleKey, string Label, RecordScope? MaxScope);

/// <summary>Hazır rol önerisi; "Şablondan başla" ile yeni rol formunu doldurur.</summary>
public sealed record RoleTemplateDto(
    string Key, string Name, string Description, IReadOnlyList<string> Permissions, RecordScope Scope = RecordScope.All);

/// <summary>
/// <see cref="Scope"/> verilmezse yeni rol tüm kayıtları görür, mevcut rolün kapsamı değişmez. <see cref="ScopeOverrides"/>
/// (kayıt türü → kapsam, ADR-0030) verilmezse mevcut istisnalar değişmez; boş küme hepsini kaldırır.
/// </summary>
public sealed record SaveRoleRequest(
    string Name,
    IReadOnlyList<string> Permissions,
    RecordScope? Scope = null,
    RoleAssignLevel? AssignLevel = null,
    bool? HideOutOfScopeShared = null,
    IReadOnlyDictionary<string, RecordScope>? ScopeOverrides = null);

public sealed record SetMembershipRolesRequest(IReadOnlyList<Guid> RoleIds);

/// <summary>Çekirdek (yönetim) izinleri; arayüz menüsü bunlara bakar. <c>CoreModule.Permissions</c> ile aynıdır (test denetler).</summary>
public static class CoreAccess
{
    public const string ModuleKey = "core";

    public const string ManageUsers = "core.users.manage";
    public const string ManageRoles = "core.roles.manage";
    public const string ManageSettings = "core.settings.manage";
    public const string ReadAudit = "core.audit.read";

    /// <summary>Özelleştirme (Y9): özel alanlar, listeler ve sektör profili.</summary>
    public const string ManageCustomization = "core.customization.manage";

    /// <summary>İşletici şirketler ve kullanıcıların şirket görevleri (ADR-0008).</summary>
    public const string ManageCompanies = "core.companies.manage";

    /// <summary>İletişim tercihlerini, izin geçmişini ve engelleri görüntüler (ADR-0025).</summary>
    public const string ReadCommunication = "core.communication.read";

    /// <summary>Kanıtla izin/ret kaydeder (ADR-0025).</summary>
    public const string ManageCommunication = "core.communication.manage";

    /// <summary>İletişim noktasını engeller ve engeli gerekçeyle kaldırır (ADR-0025).</summary>
    public const string SuppressCommunication = "core.communication.suppress";

    /// <summary>Otomasyon kuralları (ADR-0027): görüntüleme, taslak/önizleme, yayın, elle çalıştırma, geçmiş ve onay.</summary>
    public const string ReadAutomation = "core.automation.read";
    public const string ManageAutomation = "core.automation.manage";
    public const string PublishAutomation = "core.automation.publish";
    public const string RunAutomation = "core.automation.run";
    public const string ReadAutomationLogs = "core.automation-logs.read";
    public const string ApproveAutomation = "core.automation.approve";

    // Rapor merkezi (ADR-0026): yönetim değil, kullanıcı özelliğidir; bu izinler "Yönetim" bölümünü açmaz (AdministrationPermissions dışında).
    public const string ReadReports = "core.reports.read";
    public const string ManageReports = "core.reports.manage";
    public const string ShareReports = "core.reports.share";
    public const string ExportReports = "core.reports.export";
    public const string ScheduleReports = "core.reports.schedule";

    /// <summary>Bunlardan biri olan kullanıcı menüde "Yönetim" bölümünü görür.</summary>
    public static IReadOnlyList<string> AdministrationPermissions { get; } =
        [ManageUsers, ManageRoles, ManageSettings, ReadAudit, ManageCustomization, ManageCompanies, ReadCommunication, ManageCommunication, SuppressCommunication,
         ReadAutomation, ManageAutomation, PublishAutomation, RunAutomation, ReadAutomationLogs, ApproveAutomation];
}
