using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;
using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Oturumdaki kullanıcı, aktif tenant ve erişimi. Menü ve ekranlar buna göre gösterilir; asıl denetim
/// her istekte sunucudadır. Giriş, çıkış ve tenant değişiminde yeniden yüklenir.
/// </summary>
public sealed class SessionState(ApiClient api)
{
    private Task? loading;

    public event Action? Changed;

    public MeResponse? User { get; private set; }

    public MyAccessResponse? Access { get; private set; }

    public bool IsLoaded { get; private set; }

    public bool IsSignedIn => User is not null;

    public bool HasActiveTenant => User?.ActiveTenantId is not null;

    /// <summary>Platform yöneticisi (Y4): "Platform" menüsü görünür. Sunucu her istekte yeniden denetler.</summary>
    public bool IsPlatformAdministrator => User?.IsPlatformAdministrator == true;

    /// <summary>Platform ekibini yönetebilir (Owner); yalnızca menüyü ve düğmeleri gizlemek içindir, sunucu her istekte denetler.</summary>
    public bool IsPlatformOwner => User?.PlatformRoles?.Contains(PlatformRoleNames.Owner) == true;

    /// <summary>Platform hesabı iki adımlı doğrulama açmadan platform uçlarını kullanamaz (sunucuda zorunlu).</summary>
    public bool PlatformNeedsTwoFactor => IsPlatformAdministrator && User?.TwoFactorEnabled != true;

    /// <summary>
    /// Tenant gerektirmeyen ilk ekran: hiçbir firmaya üye olmayan platform yöneticisi platform alanına, diğerleri
    /// firma seçimine gider.
    /// </summary>
    public string NoTenantHome => IsPlatformAdministrator && User?.Memberships.Count == 0 ? "platform" : "tenant";

    /// <summary>Firmanın görünen adı (ayarlanmışsa, Y6b), yoksa kayıtlı adı.</summary>
    public string? ActiveTenantName =>
        User?.Memberships.FirstOrDefault(m => m.TenantId == User.ActiveTenantId)?.ShownName;

    /// <summary>Aktif firmanın logosu (Y6b); yoksa <c>null</c>.</summary>
    public TenantLogoDto? Logo { get; private set; }

    public bool IsModuleEnabled(string moduleKey) => Access?.EnabledModules.Contains(moduleKey) == true;

    public bool Can(string permission) => Access?.Permissions.Contains(permission) == true;

    /// <summary>Yönetici (sistem) rolü var mı? Yönetici rolünü vermek ve yöneticileri yönetmek yalnızca ona açıktır.</summary>
    public bool IsAdministrator => Access?.IsAdministrator == true;

    /// <summary>Erişilebilir işletici şirketler (ADR-0008); yönetici için tenant'ın hepsidir.</summary>
    public IReadOnlyList<OperatingCompanyDto> Companies => Access?.Companies ?? [];

    /// <summary>Aktif işletici şirket; erişim yoksa <c>null</c> (yalnızca ortak veri).</summary>
    public Guid? ActiveCompanyId => Access?.ActiveCompanyId;

    /// <summary>Aktif şirketin adı; yoksa <c>null</c>.</summary>
    public string? ActiveCompanyName => Companies.FirstOrDefault(c => c.Id == ActiveCompanyId)?.Name;

    /// <summary>Şirket seçici yalnızca birden fazla şirkete erişim varsa gösterilir (ADR-0008; tek şirketli tenant'ta görünmez).</summary>
    public bool HasMultipleCompanies => Companies.Count > 1;

    /// <summary>Kayıt kapsamı (Y8); erişim yüklenmediyse en dar kapsam.</summary>
    public RecordScope Scope => Access?.Scope ?? RecordScope.Own;

    /// <summary>İzinlerden en az biri var mı?</summary>
    public bool CanAny(IEnumerable<string> permissions) => permissions.Any(Can);

    /// <summary>İlk çağrıda oturumu sunucudan okur; sonrakiler aynı sonucu bekler.</summary>
    public Task EnsureLoadedAsync() => loading ??= ReloadAsync();

    /// <summary>Şifre doğru, iki adımlı doğrulama açık (Y7b): giriş <see cref="CompleteTwoFactorAsync"/> ile tamamlanır.</summary>
    public bool TwoFactorPending { get; private set; }

    /// <summary>Kişinin firması iki adımlı doğrulama istiyor ve kendisininki kapalı: firma ekranları açılmaz, önce kurulum yapılır.</summary>
    public bool TwoFactorRequired => User?.TwoFactorRequired == true;

    public async Task<string?> LoginAsync(string email, string password)
    {
        // 200'de gövde oturumdur (TwoFactorRequired yok, false okunur); 202'de ikinci adım istenir.
        var result = await api.PostAsync<LoginChallengeResponse>("/api/auth/login", new LoginRequest(email, password));
        if (!result.IsSuccess)
        {
            return result.Status == System.Net.HttpStatusCode.Unauthorized
                ? "E-posta veya şifre hatalı."
                : result.Error;
        }

        TwoFactorPending = result.Value?.TwoFactorRequired == true;
        if (!TwoFactorPending)
        {
            loading = ReloadAsync();
            await loading;
        }

        return null;
    }

    public async Task<string?> CompleteTwoFactorAsync(string code)
    {
        var result = await api.PostAsync<MeResponse>("/api/auth/login/two-factor", new TwoFactorLoginRequest(code));
        if (!result.IsSuccess)
        {
            if (result.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                TwoFactorPending = false;
            }

            return result.Error;
        }

        TwoFactorPending = false;
        loading = ReloadAsync();
        await loading;
        return null;
    }

    public async Task<string?> SelectTenantAsync(Guid tenantId)
    {
        var result = await api.PostAsync<MeResponse>("/api/auth/active-tenant", new SelectTenantRequest(tenantId));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        loading = ReloadAsync();
        await loading;
        return null;
    }

    /// <summary>Aktif işletici şirketi değiştirir (ADR-0008); sunucu erişilebilir olduğunu doğrular, sonra oturum yeniden okunur.</summary>
    public async Task<string?> SelectCompanyAsync(Guid companyId)
    {
        var result = await api.PostAsync<bool>("/api/auth/active-company", new SelectCompanyRequest(companyId));
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        await RefreshAsync();
        return null;
    }

    /// <summary>Oturumu ve erişimi sunucudan yeniden okur (ör. kendi rolleri değişti, davet kabul edilip oturum açıldı).</summary>
    public async Task RefreshAsync()
    {
        loading = ReloadAsync();
        await loading;
    }

    public async Task LogoutAsync()
    {
        await api.PostAsync<bool>("/api/auth/logout", new { });
        User = null;
        Access = null;
        Logo = null;
        Regional.Apply(null);
        loading = Task.CompletedTask;
        Changed?.Invoke();
    }

    private async Task ReloadAsync()
    {
        var me = await api.GetAsync<MeResponse>("/api/auth/me");
        User = me.IsSuccess ? me.Value : null;
        Access = null;

        if (User?.ActiveTenantId is not null && !TwoFactorRequired)
        {
            var access = await api.GetAsync<MyAccessResponse>("/api/tenant/access");
            Access = access.IsSuccess ? access.Value : null;

            // Bölgesel ayarlar ve logo: ekranlar saatleri, biçimleri ve para birimini buradan alır.
            var settings = await api.GetAsync<SettingsResponse>("/api/settings");
            Regional.Apply(settings.Value?.Settings);
            Logo = settings.Value?.Logo;
        }
        else
        {
            Regional.Apply(null);
            Logo = null;
        }

        IsLoaded = true;
        Changed?.Invoke();
    }
}
