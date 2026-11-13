using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>
/// Ç23.2 negatif matris, uç nokta kapısı: uygulamadaki her /api uç noktası kayıtlı listeden okunur ve yetkisiz kimliklerle
/// çağrılır. Yeni bir uç nokta korumasız eklenirse (oturum, firma, izin veya platform yöneticisi denetimi unutulursa)
/// test kırılır. Açık bırakılan uç noktalar aşağıdaki listelerde gerekçeyle durur.
/// </summary>
public partial class EndpointProtectionTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    /// <summary>Oturum gerektirmeyenler: giriş ve e-postadaki bağlantılar (token kendi içinde doğrulanır).</summary>
    private static readonly HashSet<string> Anonymous =
    [
        "POST /api/auth/login",
        "POST /api/auth/login/two-factor",
        "GET /api/auth/invitation",
        "POST /api/auth/accept-invitation",
        "POST /api/auth/reset-password",
        "POST /api/auth/forgot-password",
        "POST /api/auth/confirm-email-change",
        // Arayüz metinleri (ADR-0034): giriş ekranı da çevrilir, bu yüzden oturumsuzdur. Yalnızca ürünün sabit metinlerini döner;
        // tenant ya da kullanıcı verisi içermez, bilinmeyen dil 404 verir.
        "GET /api/i18n/{language}",
        // Herkese açık Lead toplama (ADR-0014): oturumsuzdur; tenant URL'den çözülür, kimlik form sırrı/izinli kaynakla, savunma hız sınırı ve alan listesiyledir.
        "POST /api/public/lead-forms/{tenant}/{formKey}/",
        "OPTIONS /api/public/lead-forms/{tenant}/{formKey}/",
        // Müşteri teklif sayfası (ADR-0019): oturumsuzdur; kimlik doğrulama bağlantı belirtecine sahip olmaktır (gövdede, özeti saklanır),
        // tenant URL'den çözülür, hız sınırı ve tek tip 404 uygulanır.
        "POST /api/public/quotes/{tenant}/view",
        "POST /api/public/quotes/{tenant}/pdf",
        "POST /api/public/quotes/{tenant}/decision",
        // Destek memnuniyet anketi (ADR-0024 dilim 4): oturumsuzdur; kimlik doğrulama süreli, tek kullanımlık bağlantı belirtecine sahip olmaktır (gövdede, özeti saklanır),
        // tenant URL'den çözülür, hız sınırı ve tek tip 404 uygulanır.
        "POST /api/public/support-survey/{tenant}/view",
        "POST /api/public/support-survey/{tenant}/answer",
        // Abonelikten çıkma ve tercih merkezi (ADR-0025 dilim 6): oturumsuzdur; kimlik doğrulama kişiye özel, rastgele bağlantı belirtecine sahip olmaktır (yalnızca karması saklanır)
        // ve belirteç yalnızca o adresin pazarlama tercihini değiştirir. Tenant URL'den çözülür, hız ve boyut sınırı ile tek tip 404 uygulanır; uçlar yalnızca POST (tek tıkla çıkış: RFC 8058).
        "POST /api/public/communication/{tenant}/view",
        "POST /api/public/communication/{tenant}/unsubscribe",
        "POST /api/public/communication/{tenant}/resubscribe",
        "POST /api/public/communication/{tenant}/topic",
        "POST /api/public/communication/{tenant}/one-click/{token}",
        // İmza sağlayıcısı webhook'u (ADR-0022 dilim 6): oturumsuzdur; kimlik doğrulama HMAC imzası ve zaman damgasıdır, gizli anahtar yapılandırmadan gelir
        // (yoksa uç nokta kapalı: 404), tenant sağlayıcı referansından çözülür, hız ve boyut sınırı uygulanır.
        "POST /api/public/contracts/signature/{provider}/events",
    ];

    /// <summary>
    /// Oturumu olan herkese (firma seçmeden, izin gerekmeden) açık olanlar: oturumun kendisi. Kişinin kendi hesabı
    /// (<see cref="OwnAccountPrefix"/>) da öyledir; değişiklikler şifre veya doğrulama kodu ister. Bunlar ikinci testte
    /// çağrılmaz (ör. çıkış oturumu kapatır).
    /// </summary>
    private static readonly HashSet<string> SignedIn =
    [
        "POST /api/auth/logout",
        "GET /api/auth/me",
        "POST /api/auth/active-tenant",
    ];

    private const string OwnAccountPrefix = "/api/account";

    /// <summary>
    /// Firmanın her etkin üyesine izin gerekmeden açık okumalar: ekran kendini bunlarla kurar. Firma kaydı içermezler.
    /// </summary>
    private static readonly HashSet<string> AnyMember =
    [
        "GET /api/tenant/current",   // firmanın adı
        "GET /api/tenant/access",    // kişinin kendi izinleri ve kapsamı (menü)
        "GET /api/tenant/modules",   // açık modüller (menü)
        "GET /api/tenant/members",   // sorumlu seçimi için üye adları; e-posta ve rol yok
        "GET /api/settings/",        // firma ayarları: okumak izin istemez (Y6), yazmak core.settings.manage
        "GET /api/custom-fields/",   // özel alan tanımları (Y9a): formlar kurulur; değerler kayıtla ve kayıt izniyle gelir
        "GET /api/choice-lists/",    // seçim listeleri (Y9b): aynı gerekçe
        "GET /api/lookups/",         // lookup ayarları (ADR-0016): formlar davranışı öğrenir; veri değil ayar döner
        "GET /api/form-layouts/",    // form düzenleri (ADR-0021): formlar yerleşimi öğrenir; veri değil düzen döner
        "GET /api/relationship-roles/", // ilişki rolü kataloğu (ADR-0029 K9): ürün standardı, tenant verisi değil
        "GET /api/industries/",         // sektör kataloğu (ADR-0042): ürün standardı, tenant verisi değil
        "GET /api/activity-options/",   // aktif faaliyet ağacı (ADR-0029): yalnızca kod/ad/üst kod; firma formu seçeneği
        "GET /api/notifications/",      // bildirim kutusu (ADR-0027 Dilim 4b): yalnızca kişinin kendi bildirimleri
        "GET /api/notifications/unread-count", // aynı gerekçe
        "POST /api/notifications/read-all",    // kişinin kendi bildirimlerini okundu yapar; firma kaydı değişmez
    ];

    [Fact]
    public async Task Every_api_endpoint_requires_a_session_unless_listed()
    {
        using var client = factory.CreateBrowserClient();
        var open = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => !Anonymous.Contains(e.Name)))
        {
            var status = await CallAsync(client, endpoint);
            if (status != HttpStatusCode.Unauthorized)
            {
                open.Add($"{endpoint.Name} → {(int)status}");
            }
        }

        Assert.True(open.Count == 0, "Oturumsuz çağrılabilen uç nokta:\n" + string.Join("\n", open));
        AssertListed(Anonymous);
    }

    [Fact]
    public async Task Member_without_permissions_gets_nothing_beyond_the_member_list()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        using var client = await LoginAsync(user.Email!);

        var allowed = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => !Anonymous.Contains(e.Name) && !SignedIn.Contains(e.Name) && !e.Route.StartsWith(OwnAccountPrefix, StringComparison.Ordinal)))
        {
            var status = await CallAsync(client, endpoint);
            if ((int)status is >= 200 and < 300 && !AnyMember.Contains(endpoint.Name))
            {
                allowed.Add($"{endpoint.Name} → {(int)status}");
            }
        }

        Assert.True(allowed.Count == 0, "Rolü olmayan üyenin başarıyla çağırabildiği, listede olmayan uç nokta:\n" + string.Join("\n", allowed));
        AssertListed(SignedIn);
        AssertListed(AnyMember);
    }

    [Fact]
    public async Task Platform_endpoints_are_closed_to_tenant_administrators()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [Modulix.Core.Modules.CoreModule.Permissions.ManageUsers], user);
        using var client = await LoginAsync(user.Email!);

        var open = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => e.Route.StartsWith("/api/platform", StringComparison.Ordinal)))
        {
            var status = await CallAsync(client, endpoint);
            if (status != HttpStatusCode.Forbidden)
            {
                open.Add($"{endpoint.Name} → {(int)status}");
            }
        }

        Assert.True(open.Count == 0, "Platform yöneticisi olmayana 403 dönmeyen platform uç noktası:\n" + string.Join("\n", open));
    }

    private sealed record ApiEndpoint(string Method, string Route)
    {
        public string Name => $"{Method} {Route}";
    }

    private IReadOnlyList<ApiEndpoint> Endpoints() =>
    [
        .. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Route: "/" + e.RoutePattern.RawText!.TrimStart('/'), Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(e => e.Route.StartsWith("/api/", StringComparison.Ordinal) && !e.Route.Contains("{**", StringComparison.Ordinal))
            .SelectMany(e => e.Methods.Select(m => new ApiEndpoint(m, e.Route)))
            .Distinct()
            .OrderBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal),
    ];

    // Yol parametreleri örnek değerlerle doldurulur; gövde boş bir JSON nesnesidir.
    private static async Task<HttpStatusCode> CallAsync(HttpClient client, ApiEndpoint endpoint)
    {
        var url = RouteParameter().Replace(endpoint.Route, m => m.Groups["name"].Value switch
        {
            "entityKey" => CrmModule.Entities.Company,
            var name when m.Value.Contains(":guid", StringComparison.Ordinal) || name.EndsWith("Id", StringComparison.Ordinal) || name == "id" =>
                Guid.NewGuid().ToString(),
            _ => "x",
        });
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);
        if (endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private void AssertListed(IEnumerable<string> names)
    {
        var known = Endpoints().Select(e => e.Name).ToHashSet();
        var stale = names.Where(n => !known.Contains(n)).ToList();
        Assert.True(stale.Count == 0, "Listede olup uygulamada olmayan uç nokta: " + string.Join(", ", stale));
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    [GeneratedRegex(@"\{(?<name>\w+)(:[^}]*)?\}")]
    private static partial Regex RouteParameter();
}
