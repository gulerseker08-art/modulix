using Microsoft.JSInterop;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Shared.Localization;
using Modulix.Shared.Platform;
using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Oturumdaki kullanıcı, aktif tenant ve erişimi. Menü ve ekranlar buna göre gösterilir; asıl denetim
/// her istekte sunucudadır. Giriş, çıkış ve tenant değişiminde yeniden yüklenir.
/// </summary>
public sealed class SessionState(ApiClient api, IJSRuntime? js = null)
{
    private Task? loading;
    private bool languageDetected;
    private string? detectedLanguage;

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
                ? Texts.Get("auth.login.invalid")
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
        UiLanguage.Apply(null, null, await DetectLanguageAsync());
        await LoadTextsAsync();
        loading = Task.CompletedTask;
        Changed?.Invoke();
    }

    private async Task ReloadAsync()
    {
        var me = await api.GetAsync<MeResponse>("/api/auth/me");
        User = me.IsSuccess ? me.Value : null;
        Access = null;
        var fallbackLanguage = await DetectLanguageAsync();

        if (User?.ActiveTenantId is not null && !TwoFactorRequired)
        {
            // Erişim ile bölgesel ayarlar birbirinden bağımsızdır; birlikte istenir.
            var accessTask = api.GetAsync<MyAccessResponse>("/api/tenant/access");
            var settingsTask = api.GetAsync<SettingsResponse>("/api/settings");
            var access = await accessTask;
            Access = access.IsSuccess ? access.Value : null;

            // Bölgesel ayarlar ve logo: ekranlar saatleri, biçimleri ve para birimini buradan alır.
            var settings = await settingsTask;
            Regional.Apply(settings.Value?.Settings);
            UiLanguage.Apply(User.PreferredLanguage, settings.Value?.Settings, fallbackLanguage);
            Logo = settings.Value?.Logo;
        }
        else
        {
            Regional.Apply(null);
            UiLanguage.Apply(User?.PreferredLanguage, null, fallbackLanguage);
            Logo = null;
        }

        await RememberLanguageAsync();
        await LoadTextsAsync();
        IsLoaded = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Giriş öncesi ekranların dili (ADR-0034): tarayıcıda hatırlanan dil, yoksa tarayıcının dili; ikisi de desteklenmiyorsa
    /// <c>null</c> (Türkçe). Bir kez okunur. Depolama kapalıysa ya da çağrı başarısızsa sessizce Türkçeye döner.
    /// </summary>
    private async Task<string?> DetectLanguageAsync()
    {
        if (languageDetected || js is null)
        {
            return detectedLanguage;
        }

        languageDetected = true;
        try
        {
            detectedLanguage = LanguageCodes.Normalize(await js.InvokeAsync<string?>("modulixShell.getLanguage"))
                ?? LanguageCodes.FromCulture(await js.InvokeAsync<string?>("modulixShell.browserLanguage"));
        }
        catch (Exception)
        {
            // JS yüklenmemiş ya da depolama kapalı: dil algılanamaz, Türkçe kullanılır.
        }

        return detectedLanguage;
    }

    /// <summary>Kişi dilini açıkça seçtiyse giriş ekranının bir sonraki açılışında da o dil kullanılsın diye tarayıcıda saklar.</summary>
    private async Task RememberLanguageAsync()
    {
        if (js is null || LanguageCodes.Normalize(User?.PreferredLanguage) is not { } language || language == detectedLanguage)
        {
            return;
        }

        detectedLanguage = language;
        try
        {
            await js.InvokeVoidAsync("modulixShell.setLanguage", language);
        }
        catch (Exception)
        {
            // Depolama kapalıysa tercih yalnızca bu oturumda hatırlanır.
        }
    }

    /// <summary>Etkin dilin metinlerini yükler (ADR-0034); dil değişmediyse yeniden indirmez. Yüklenemezse eski sözlük kalır.</summary>
    private async Task LoadTextsAsync()
    {
        if (Texts.Language == UiLanguage.Code)
        {
            return;
        }

        var bundle = await api.GetAsync<TextBundleResponse>($"/api/i18n/{UiLanguage.Code}");
        if (bundle.Value is { } loaded)
        {
            Texts.Apply(loaded);
        }
    }
}
