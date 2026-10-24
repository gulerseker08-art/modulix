using Modulix.Shared.Access;
using Modulix.Shared.Auth;

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

    public string? ActiveTenantName =>
        User?.Memberships.FirstOrDefault(m => m.TenantId == User.ActiveTenantId)?.TenantName;

    public bool IsModuleEnabled(string moduleKey) => Access?.EnabledModules.Contains(moduleKey) == true;

    public bool Can(string permission) => Access?.Permissions.Contains(permission) == true;

    /// <summary>İzinlerden en az biri var mı?</summary>
    public bool CanAny(IEnumerable<string> permissions) => permissions.Any(Can);

    /// <summary>İlk çağrıda oturumu sunucudan okur; sonrakiler aynı sonucu bekler.</summary>
    public Task EnsureLoadedAsync() => loading ??= ReloadAsync();

    public async Task<string?> LoginAsync(string email, string password)
    {
        var result = await api.PostAsync<MeResponse>("/api/auth/login", new LoginRequest(email, password));
        if (!result.IsSuccess)
        {
            return result.Status == System.Net.HttpStatusCode.Unauthorized
                ? "E-posta veya şifre hatalı."
                : result.Error;
        }

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

    public async Task LogoutAsync()
    {
        await api.PostAsync<bool>("/api/auth/logout", new { });
        User = null;
        Access = null;
        loading = Task.CompletedTask;
        Changed?.Invoke();
    }

    private async Task ReloadAsync()
    {
        var me = await api.GetAsync<MeResponse>("/api/auth/me");
        User = me.IsSuccess ? me.Value : null;
        Access = null;

        if (User?.ActiveTenantId is not null)
        {
            var access = await api.GetAsync<MyAccessResponse>("/api/tenant/access");
            Access = access.IsSuccess ? access.Value : null;
        }

        IsLoaded = true;
        Changed?.Invoke();
    }
}
