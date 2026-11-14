namespace Modulix.Web.Services;

/// <summary>
/// Sunucu bir isteğe 401 döndüğünde (oturum süresi doldu, başka cihazdan kapatıldı) haber verir.
/// <see cref="ApiClient"/> yayınlar, <see cref="SessionState"/> dinler ve oturumu temizler; <c>AuthGate</c> girişe yönlendirir.
/// </summary>
public sealed class SessionExpiry
{
    public event Action? Expired;

    public void Notify() => Expired?.Invoke();
}
