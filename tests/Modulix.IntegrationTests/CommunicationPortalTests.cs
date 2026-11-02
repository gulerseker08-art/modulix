using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Communication;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 6: abonelikten çıkma bağlantısı ve oturumsuz tercih merkezi. Dört test türü: mutlu yol (çıkış, tekrar güvenli, geri alma, tek tıkla çıkış), hata/doğrulama
/// (geçersiz belirteç, firma kaydı ve engel, süre), tenant izolasyonu (belirteç yalnızca kendi tenant'ında çalışır) ve yetki (uçlar yalnızca POST, belirteç başka yetki vermez).
/// </summary>
public class CommunicationPortalTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private const string PublicUrl = "https://app.modulix.test";

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public CommunicationPortalTests(ModulixApiFactory factory)
    {
        this.factory = factory;

        // Bağlantı genel adresle kurulur; testte adres ayarlıdır.
        host = factory.WithWebHostBuilder(builder => builder.UseSetting("App:PublicUrl", PublicUrl));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    // ---- Mutlu yol ----

    [Fact]
    public async Task Unsubscribing_through_the_link_stops_marketing_is_idempotent_and_can_be_undone_by_the_person()
    {
        var tenant = await factory.CreateTenantAsync("Anka Tekstil");
        const string address = "ayse.demir@b.test";
        await GrantAsync(tenant, address);
        var link = await IssueAsync(tenant, address);
        using var client = Anonymous();

        // Sayfayı görmek hiçbir şeyi değiştirmez; adres maskelidir, firma adı görünür, yanıt önbelleğe alınmaz.
        var viewResponse = await PostAsync(client, tenant, "view", TokenOf(link));
        Assert.Equal(HttpStatusCode.OK, viewResponse.StatusCode);
        Assert.Equal("no-store", viewResponse.Headers.CacheControl?.ToString());
        var raw = await viewResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(address, raw);
        var view = (await viewResponse.Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal(("subscribed", "a***@b.test", "Anka Tekstil", true, false), (view.State, view.MaskedAddress, view.TenantName, view.CanUnsubscribe, view.CanResubscribe));
        Assert.True((await DecideAsync(tenant, address)).IsAllowed);

        // Çıkış: pazarlama artık reddedilir.
        var unsubscribed = (await (await PostAsync(client, tenant, "unsubscribe", TokenOf(link))).Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal(("unsubscribed", false, true), (unsubscribed.State, unsubscribed.CanUnsubscribe, unsubscribed.CanResubscribe));
        var decision = await DecideAsync(tenant, address);
        Assert.Equal((CommunicationOutcome.Denied, CommunicationCodes.Denied), (decision.Outcome, decision.Code));

        // Tekrar güvenlidir: yeni olay ve denetim kaydı yazılmaz. Geçmiş: izin verildi, çıkıldı.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, tenant, "unsubscribe", TokenOf(link))).StatusCode);
        var (preference, events) = await HistoryAsync(tenant, address);
        Assert.Equal(2, events.Count);
        Assert.Equal((ConsentStatus.Denied, ConsentSources.Unsubscribe, (Guid?)null), (preference.Status, preference.Source, preference.ChangedBy));

        // Kişi kendi çıkışını geri alabilir; kanıt, bağlantıya referanstır.
        var resubscribed = (await (await PostAsync(client, tenant, "resubscribe", TokenOf(link))).Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal("subscribed", resubscribed.State);
        Assert.True((await DecideAsync(tenant, address)).IsAllowed);
        (preference, events) = await HistoryAsync(tenant, address);
        Assert.Equal(3, events.Count);
        Assert.Equal((ConsentSources.PreferenceCenter, true), (preference.Source, preference.EvidenceRef!.StartsWith("link:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task One_click_unsubscribe_follows_rfc_8058_and_a_plain_get_never_unsubscribes()
    {
        var tenant = await factory.CreateTenantAsync();
        await GrantAsync(tenant, "tek@b.test");
        await GrantAsync(tenant, "ikinci@b.test");
        var link = await IssueAsync(tenant, "tek@b.test");
        var other = await IssueAsync(tenant, "ikinci@b.test");
        using var client = Anonymous();
        var path = new Uri(link.OneClickUrl).PathAndQuery;

        // Bağlantıyı önceden açan güvenlik tarayıcıları GET yapar: kimseyi çıkaramaz.
        NotServed(await client.GetAsync(path));
        NotServed(await client.GetAsync($"/api/public/communication/{tenant.Id:N}/unsubscribe"));
        Assert.True((await DecideAsync(tenant, "tek@b.test")).IsAllowed);

        // Gövdesiz ya da yanlış değerli POST kabul edilmez.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(new Uri(other.OneClickUrl).PathAndQuery, Form("List-Unsubscribe", "Yes"))).StatusCode);
        Assert.True((await DecideAsync(tenant, "tek@b.test")).IsAllowed);
        Assert.True((await DecideAsync(tenant, "ikinci@b.test")).IsAllowed);

        // Posta istemcisinin tek tıkla çıkışı: "List-Unsubscribe=One-Click".
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path, Form("List-Unsubscribe", "One-Click"))).StatusCode);
        Assert.Equal(CommunicationOutcome.Denied, (await DecideAsync(tenant, "tek@b.test")).Outcome);
        Assert.True((await DecideAsync(tenant, "ikinci@b.test")).IsAllowed);
    }

    // ---- Hata / doğrulama ----

    [Fact]
    public async Task Unknown_malformed_and_foreign_tokens_and_tenants_all_look_the_same()
    {
        var tenant = await factory.CreateTenantAsync();
        using var client = Anonymous();

        var responses = new[]
        {
            await PostAsync(client, tenant, "view", LinkTokens.New()),
            await PostAsync(client, tenant, "unsubscribe", "kisa"),
            await PostAsync(client, tenant, "resubscribe", string.Empty),
            await client.PostAsync($"/api/public/communication/{tenant.Id:N}/view", new StringContent("{}", Encoding.UTF8, "application/json")),
            await client.PostAsJsonAsync("/api/public/communication/yok-boyle-bir-firma/view", new PublicCommunicationTokenRequest(LinkTokens.New())),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Single((await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Select(Title).Distinct());
    }

    [Fact]
    public async Task A_person_cannot_undo_a_company_decision_or_get_past_a_suppression()
    {
        var tenant = await factory.CreateTenantAsync();
        using var client = Anonymous();

        // Firmanın elle kaydettiği ret: kişi bağlantıyla geri açamaz.
        const string manual = "elle@b.test";
        await ConsentAsync(tenant, manual, ConsentStatus.Denied, ConsentSources.Manual);
        var manualLink = await IssueAsync(tenant, manual);
        var view = (await (await PostAsync(client, tenant, "view", TokenOf(manualLink))).Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal(("unsubscribed", false), (view.State, view.CanResubscribe));
        var refused = await PostAsync(client, tenant, "resubscribe", TokenOf(manualLink));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("firmayla", Title(await refused.Content.ReadAsStringAsync()));
        Assert.Equal(CommunicationOutcome.Denied, (await DecideAsync(tenant, manual)).Outcome);

        // Aktif engel: durum "engelli"; kişi engeli kaldıramaz, çıkış ise yine de kaydedilir (iletişimi azaltmak güvenlidir).
        const string blocked = "engelli@b.test";
        await GrantAsync(tenant, blocked);
        await SuppressAsync(tenant, blocked);
        var blockedLink = await IssueAsync(tenant, blocked);
        var blockedView = (await (await PostAsync(client, tenant, "view", TokenOf(blockedLink))).Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal(("blocked", false, false), (blockedView.State, blockedView.CanUnsubscribe, blockedView.CanResubscribe));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, tenant, "unsubscribe", TokenOf(blockedLink))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, tenant, "resubscribe", TokenOf(blockedLink))).StatusCode);
        Assert.Contains("suppressed", (await DecideAsync(tenant, blocked)).Code);

        // Zaten abone olan kişi "yeniden aç" ile hiçbir şey değiştiremez.
        const string subscribed = "abone@b.test";
        await GrantAsync(tenant, subscribed);
        var subscribedLink = await IssueAsync(tenant, subscribed);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, tenant, "resubscribe", TokenOf(subscribedLink))).StatusCode);
        Assert.Single((await HistoryAsync(tenant, subscribed)).Events);
    }

    [Fact]
    public async Task An_expired_link_still_unsubscribes_but_can_no_longer_resubscribe()
    {
        var tenant = await factory.CreateTenantAsync();
        const string address = "eski@b.test";
        await GrantAsync(tenant, address);
        var link = await IssueAsync(tenant, address);
        using (var scope = OpenScope(tenant.Id))
        {
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().CommunicationLinkTokens
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }

        using var client = Anonymous();

        // İletişimi azaltmak her zaman güvenlidir: süresi dolmuş bağlantıyla da çıkılır.
        var unsubscribed = (await (await PostAsync(client, tenant, "unsubscribe", TokenOf(link))).Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.Equal(("unsubscribed", false), (unsubscribed.State, unsubscribed.CanResubscribe));
        Assert.Equal(CommunicationOutcome.Denied, (await DecideAsync(tenant, address)).Outcome);

        var refused = await PostAsync(client, tenant, "resubscribe", TokenOf(link));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("süresi dolmuş", Title(await refused.Content.ReadAsStringAsync()));
        Assert.Equal(CommunicationOutcome.Denied, (await DecideAsync(tenant, address)).Outcome);
    }

    [Fact]
    public async Task The_link_issuer_refuses_invalid_addresses_and_stores_only_the_hash()
    {
        var tenant = await factory.CreateTenantAsync();
        using var scope = OpenScope(tenant.Id);
        var issuer = scope.ServiceProvider.GetRequiredService<IUnsubscribeLinks>();

        await Assert.ThrowsAsync<ArgumentException>(() => issuer.IssueAsync(CommunicationChannel.Email, "adres-degil", "ref", CancellationToken.None));

        var link = (await issuer.IssueAsync(CommunicationChannel.Email, "  Kisi@B.Test ", "ref", CancellationToken.None))!;
        var token = TokenOf(link);
        var stored = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().CommunicationLinkTokens.AsNoTracking().SingleAsync();
        Assert.Equal(("kisi@b.test", LinkTokens.Hash(token)), (stored.Address, stored.TokenHash));
        Assert.DoesNotContain(token, stored.TokenHash, StringComparison.Ordinal);
        Assert.StartsWith($"{PublicUrl}/abonelik/{tenant.Id:N}#t=", link.PageUrl);
        Assert.StartsWith($"{PublicUrl}/api/public/communication/{tenant.Id:N}/one-click/", link.OneClickUrl);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task A_token_only_works_in_its_own_tenant_and_changes_only_that_tenants_preference()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        const string address = "ortak@x.test";
        await GrantAsync(a, address);
        await GrantAsync(b, address);
        var link = await IssueAsync(a, address);
        using var client = Anonymous();

        // A'nın belirteci B'nin adresinde bulunamaz; B'nin tercihi değişmez.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, b, "unsubscribe", TokenOf(link))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, b, "view", TokenOf(link))).StatusCode);
        Assert.True((await DecideAsync(b, address)).IsAllowed);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, a, "unsubscribe", TokenOf(link))).StatusCode);
        Assert.Equal(CommunicationOutcome.Denied, (await DecideAsync(a, address)).Outcome);
        Assert.True((await DecideAsync(b, address)).IsAllowed);

        // Belirteç satırları da tenant süzgecinden geçer.
        using var scopeB = OpenScope(b.Id);
        Assert.Equal(0, await scopeB.ServiceProvider.GetRequiredService<ModulixDbContext>().CommunicationLinkTokens.CountAsync());
        using var scopeA = OpenScope(a.Id);
        Assert.Equal(1, await scopeA.ServiceProvider.GetRequiredService<ModulixDbContext>().CommunicationLinkTokens.CountAsync());
    }

    // ---- Yetki ----

    [Fact]
    public async Task The_public_endpoints_are_post_only_size_limited_and_the_token_opens_nothing_else()
    {
        var tenant = await factory.CreateTenantAsync();
        await GrantAsync(tenant, "yetki@b.test");
        var link = await IssueAsync(tenant, "yetki@b.test");
        using var client = Anonymous();

        foreach (var action in new[] { "view", "unsubscribe", "resubscribe" })
        {
            NotServed(await client.GetAsync($"/api/public/communication/{tenant.Id:N}/{action}"));
        }

        // Gövde sınırı.
        var big = await client.PostAsync($"/api/public/communication/{tenant.Id:N}/view", new StringContent($"{{\"token\":\"{new string('a', 8 * 1024)}\"}}", Encoding.UTF8, "application/json"));
        Assert.True(big.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, big.StatusCode.ToString());

        // Belirteç oturum değildir: yönetim uçları ve tenant verisi açılmaz.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenOf(link));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/communication/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/communication/suppressions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/communication/preferences",
            new SaveConsentRequest("Email", "yetki@b.test", "Marketing", "Granted", "manual", "kanit", null, null, null))).StatusCode);
        Assert.True((await DecideAsync(tenant, "yetki@b.test")).IsAllowed);
    }

    // ---- Yardımcılar ----

    private HttpClient Anonymous() =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private IServiceScope OpenScope(Guid tenantId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.NewGuid(), Guid.NewGuid());
        return scope;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Tenant tenant, string action, string token) =>
        client.PostAsJsonAsync($"/api/public/communication/{tenant.Id:N}/{action}", new PublicCommunicationTokenRequest(token));

    /// <summary>Uçlar yalnızca POST kabul eder: GET ne işlem yapar ne sayfa döndürür (yönlendirme 404 ya da 405 verir).</summary>
    private static void NotServed(HttpResponseMessage response) =>
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());

    private static FormUrlEncodedContent Form(string key, string value) => new([new KeyValuePair<string, string>(key, value)]);

    private static string TokenOf(UnsubscribeLink link) => link.PageUrl[(link.PageUrl.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private static string Title(string problem) => System.Text.Json.JsonDocument.Parse(problem).RootElement.GetProperty("title").GetString()!;

    private async Task<UnsubscribeLink> IssueAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        return (await scope.ServiceProvider.GetRequiredService<IUnsubscribeLinks>().IssueAsync(CommunicationChannel.Email, address, "test", CancellationToken.None))!;
    }

    private Task GrantAsync(Tenant tenant, string address) => ConsentAsync(tenant, address, ConsentStatus.Granted, ConsentSources.Manual);

    private async Task ConsentAsync(Tenant tenant, string address, ConsentStatus status, string source)
    {
        using var scope = OpenScope(tenant.Id);
        await scope.ServiceProvider.GetRequiredService<ICommunicationAdministration>().RecordConsentAsync(
            new ConsentChange(CommunicationChannel.Email, address, CommunicationPurpose.Marketing, status, source, "test-kaniti", "test"), CancellationToken.None);
    }

    private async Task SuppressAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        await scope.ServiceProvider.GetRequiredService<ICommunicationAdministration>().SuppressAsync(
            new SuppressionRequest(CommunicationChannel.Email, address, SuppressionReason.HardBounce, ConsentSources.Provider), CancellationToken.None);
    }

    private async Task<CommunicationDecision> DecideAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        return await scope.ServiceProvider.GetRequiredService<ICommunicationPreferences>().EvaluateAsync(
            CommunicationChannel.Email, CommunicationPurpose.Marketing, address, CancellationToken.None);
    }

    private async Task<(PreferenceView Preference, IReadOnlyList<ConsentEventView> Events)> HistoryAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        var admin = scope.ServiceProvider.GetRequiredService<ICommunicationAdministration>();
        var preference = (await admin.ListPreferencesAsync(new PreferenceQuery(CommunicationChannel.Email, CommunicationPurpose.Marketing, null, address), CancellationToken.None)).Items.Single();
        return (preference, (await admin.EventsAsync(preference.Id, CancellationToken.None))!);
    }
}
