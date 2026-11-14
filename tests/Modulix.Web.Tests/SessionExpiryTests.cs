using System.Net;
using System.Reflection;
using Modulix.Shared.Access;
using Modulix.Shared.Auth;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Sunucu 401 döndüğünde oturum temizlenir; ekranlar girişe yönlendirilir (AuthGate, <see cref="SessionState.Changed"/>).</summary>
public class SessionExpiryTests
{
    [Fact]
    public async Task Unauthorized_response_clears_the_session_and_notifies()
    {
        var expiry = new SessionExpiry();
        var api = new ApiClient(Client(HttpStatusCode.Unauthorized), expiry);
        var session = new SessionState(api, expiry: expiry);
        SignIn(session);
        var changed = 0;
        session.Changed += () => changed++;

        var result = await api.GetAsync<object>("/api/crm/companies");

        Assert.False(result.IsSuccess);
        Assert.False(session.IsSignedIn);
        Assert.Null(session.Access);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Other_errors_keep_the_session()
    {
        var expiry = new SessionExpiry();
        var api = new ApiClient(Client(HttpStatusCode.Forbidden), expiry);
        var session = new SessionState(api, expiry: expiry);
        SignIn(session);
        var changed = 0;
        session.Changed += () => changed++;

        await api.GetAsync<object>("/api/crm/companies");

        Assert.True(session.IsSignedIn);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Unauthorized_while_signed_out_does_nothing()
    {
        var expiry = new SessionExpiry();
        var api = new ApiClient(Client(HttpStatusCode.Unauthorized), expiry);
        var session = new SessionState(api, expiry: expiry);
        var changed = 0;
        session.Changed += () => changed++;

        // Hatalı şifre de 401 döner; oturum zaten yokken yönlendirme tetiklenmez.
        await api.PostAsync<object>("/api/auth/login", new LoginRequest("a@example.com", "yanlis"));

        Assert.Equal(0, changed);
    }

    private static void SignIn(SessionState session)
    {
        Set(session, nameof(SessionState.User), new MeResponse(Guid.NewGuid(), "a@example.com", "Test", Guid.NewGuid(), [], false));
        Set(session, nameof(SessionState.Access), new MyAccessResponse([], []));
    }

    private static void Set(SessionState session, string property, object value) =>
        typeof(SessionState).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(session, value);

    private static HttpClient Client(HttpStatusCode status) =>
        new(new StatusHandler(status)) { BaseAddress = new Uri("https://localhost/") };

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
