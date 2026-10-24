namespace Modulix.Core.Platform;

/// <summary>Kullanıcı listesindeki veya detayındaki rol.</summary>
public sealed record UserRole(Guid Id, string Name, bool IsSystem);

/// <summary>Aktif tenant'ın bir üyesi; kimlik <see cref="MembershipId"/>'dir (aynı kişi başka tenant'ta başka üyeliktir).</summary>
public sealed record TenantUser(
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    string Email,
    MembershipStatus Status,
    IReadOnlyList<UserRole> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? InvitedAt,
    DateTimeOffset? LastSignInAt,
    bool IsLockedOut,
    bool IsCurrentUser)
{
    public bool IsAdministrator => Roles.Any(r => r.IsSystem);

    /// <summary>İki adımlı doğrulama açık mı (Y7b); hesaba aittir, firmaya değil.</summary>
    public bool TwoFactorEnabled { get; init; }
}

/// <summary>Kullanıcı listesi sorgusu. Arama ad ve e-postada yapılır; sayfa 1'den başlar.</summary>
public sealed record UserQuery(string? Search, MembershipStatus? Status, Guid? RoleId, int Page, int PageSize)
{
    /// <summary>İki adımlı doğrulaması açık (true) ya da kapalı (false) olanlar (Y7b).</summary>
    public bool? TwoFactorEnabled { get; init; }
}

public sealed record UserPage(IReadOnlyList<TenantUser> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Davet. <see cref="PublicUrl"/>: e-postadaki bağlantıların kökü (ör. https://app.modulix.com.tr); API ayardan okur.
/// </summary>
public sealed record UserInvitation(string Email, string DisplayName, IReadOnlyCollection<Guid> RoleIds, string PublicUrl);

/// <summary><see cref="ExistingAccount"/>: e-posta platformda zaten kayıtlıydı; üyelik hemen etkin oldu, bilgi e-postası gitti.</summary>
public sealed record InvitationResult(TenantUser User, bool ExistingAccount);

public enum UserAdministrationError
{
    /// <summary>Üyelik bu tenant'ta yok (başka tenant'ınki de "yok" sayılır). API 404.</summary>
    NotFound = 1,

    /// <summary>İstek üyeliğin durumuyla çelişiyor (ör. kişi zaten üye, kendini devre dışı bırakma). API 409.</summary>
    Conflict = 2,

    /// <summary>Yönetici rolüne veya bir yöneticiye dokunan işlem; yalnızca yönetici yapabilir. API 403.</summary>
    AdministratorRequired = 3,

    /// <summary>Davet sınırı aşıldı. API 429.</summary>
    TooManyRequests = 4,

    /// <summary>İşlemi yapanın gereken izni yok (ör. devredilecek kayıtları düzenleme izni). API 403.</summary>
    PermissionDenied = 5,
}

/// <summary>Kişinin sorumlu olduğu bir kayıt türünün sayısı; işlemi yapan bu kayıtları göremiyorsa <see cref="Count"/> boştur.</summary>
public sealed record OwnedRecordSummary(string EntityKey, string Label, int? Count);

/// <summary>Kişinin bir geçerli izni ve onu veren roller.</summary>
public sealed record PermissionGrant(string Key, IReadOnlyList<UserRole> GrantedBy);

/// <summary>Kişinin aktif tenant'taki geçerli izinleri (açık modüllerinkiler), anahtara göre sıralı.</summary>
public sealed record UserAccess(bool IsAdministrator, IReadOnlyList<PermissionGrant> Permissions)
{
    /// <summary>Kayıt kapsamı (Y8): rollerin en genişi.</summary>
    public Contracts.Authorization.RecordScope Scope { get; init; } = Contracts.Authorization.RecordScope.All;

    /// <summary>Kişinin ekipleri (ada göre); "ekibinin kayıtları" bunların üyelerini kapsar.</summary>
    public IReadOnlyList<string> Teams { get; init; } = [];
}

public sealed class UserAdministrationException(UserAdministrationError error, string message) : Exception(message)
{
    public UserAdministrationError Error { get; } = error;
}

/// <summary>
/// Aktif tenant'ın kullanıcı yönetimi (Y2). Çağıran <c>core.users.manage</c> iznine sahip olmalıdır (API denetler).
///
/// Kurallar:
/// - Yönetici rolünü vermek veya almak ve bir yöneticinin üyeliğinde işlem yapmak (devre dışı bırakma, şifre
///   sıfırlama, kilit açma, rol değiştirme) yalnızca yöneticiye açıktır; böylece kullanıcı yöneticisi kendini veya
///   başkasını yönetici yapamaz.
/// - Son etkin yönetici devre dışı bırakılamaz (<see cref="Authorization.RoleConflictException"/>); kişi kendini
///   devre dışı bırakamaz.
/// - Şifre sıfırlama ve kilit hesabın kendisine uygulanır: kişi başka tenant'larda da üyeyse oralardaki girişini de
///   etkiler. Yönetici şifreyi görmez ve belirlemez; bağlantı kişinin e-postasına gider.
///
/// Değişiklikler <c>core.membership</c> kaydı olarak denetim kaydına, e-postalar aynı işlemde kuyruğa yazılır.
/// Hatalar: <see cref="UserAdministrationException"/>, geçersiz alan için <see cref="ArgumentException"/>
/// (ParamName alan adıdır), son yönetici için <see cref="Authorization.RoleConflictException"/>.
/// </summary>
public interface IUserAdministration
{
    Task<UserPage> ListAsync(UserQuery query, CancellationToken cancellationToken);

    /// <summary>Üyelik bu tenant'ta yoksa <c>null</c>.</summary>
    Task<TenantUser?> GetAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// E-posta kayıtlı değilse şifresiz hesap ve davet bekleyen üyelik oluşturur, davet bağlantısı gönderir.
    /// Kayıtlıysa üyelik hemen etkin olur ve bilgi e-postası gider; şifre değişmez. Zaten üyeyse çakışma.
    /// </summary>
    Task<InvitationResult> InviteAsync(UserInvitation invitation, CancellationToken cancellationToken);

    /// <summary>Davet bağlantısını yeniden gönderir (ör. e-posta kaybolduysa); süre yeniden başlar.</summary>
    Task ResendInvitationAsync(Guid membershipId, string publicUrl, CancellationToken cancellationToken);

    /// <summary>Bekleyen daveti siler; bağlantı üyeliğe bağlı olduğu için artık çalışmaz.</summary>
    Task CancelInvitationAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliği devre dışı bırakır. <paramref name="transferToMembershipId"/> verilirse kişinin sorumlu olduğu kayıtlar
    /// (<see cref="GetOwnedRecordsAsync"/>) önce o üyeye devredilir; devir veya devre dışı bırakma başarısız olursa hiçbiri
    /// yapılmaz. Üyelik zaten devre dışıysa yalnızca devir yapılır.
    /// </summary>
    Task DisableAsync(Guid membershipId, Guid? transferToMembershipId, CancellationToken cancellationToken);

    /// <summary>Kişinin sorumlu olduğu kayıtların türlere göre sayısı (tenant için açık modüllerin <c>OwnedRecords</c>'u).</summary>
    Task<IReadOnlyList<OwnedRecordSummary>> GetOwnedRecordsAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Kişinin bu tenant'taki geçerli izinleri ve her izni veren roller ("bu kullanıcı neye erişebilir"). Hesap,
    /// sunucunun her istekte uyguladığıyla aynıdır.
    /// </summary>
    Task<UserAccess> GetAccessAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Kişinin sorumlu olduğu kayıtları başka bir etkin üyeye devreder ve devredilen kayıt sayısını döner. Her kayıt
    /// kayıt boru hattından geçer (yetki, doğrulama, denetim kaydı, olay); biri başarısız olursa hiçbiri devredilmez.
    /// İşlemi yapanın kayıtları düzenleme izni yoksa <see cref="UserAdministrationError.PermissionDenied"/>.
    /// </summary>
    Task<int> TransferRecordsAsync(Guid membershipId, Guid toMembershipId, CancellationToken cancellationToken);

    Task EnableAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>Kişiye şifre sıfırlama bağlantısı gönderir (24 saat geçerli).</summary>
    Task SendPasswordResetAsync(Guid membershipId, string publicUrl, CancellationToken cancellationToken);

    /// <summary>Hatalı denemeler yüzünden kilitlenen hesabın kilidini açar; kilitli değilse bir şey yapmaz.</summary>
    Task UnlockAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Kişinin tüm açık oturumlarını kapatır (Y7a). Oturum firmaya bağlı olmadığından diğer firmalardaki oturumu da
    /// kapanır. Yönetici rolündeki kişinin oturumlarını yalnızca yönetici kapatabilir.
    /// </summary>
    Task CloseSessionsAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliğin rollerini değiştirir (<see cref="Authorization.IRoleService.SetMembershipRolesAsync"/>); yönetici kuralını
    /// uygular.
    /// </summary>
    Task SetRolesAsync(Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);
}
