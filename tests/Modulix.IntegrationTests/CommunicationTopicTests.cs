using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Communication;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 14: iletişim konuları. Konu yalnızca daraltır (pazarlama izni vermez). Dört test türü: mutlu yol (tanım, tercih merkezinde çıkış/dönüş, konulu karar),
/// hata/doğrulama (anahtar, bilinmeyen ve kapalı konu, izin vermeme, firma kaydı, süresi dolmuş bağlantı), tenant izolasyonu ve yetki.
/// </summary>
public class CommunicationTopicTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private const string Address = "ayse.demir@b.test";

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public CommunicationTopicTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        host = factory.WithWebHostBuilder(builder => builder.UseSetting("App:PublicUrl", "https://app.modulix.test"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    // ---- Mutlu yol ----

    [Fact]
    public async Task A_person_leaves_and_returns_to_a_topic_through_the_link_and_only_that_topic_is_affected()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        await SaveTopicAsync(manager, "kampanya", "Kampanya duyuruları");
        await GrantAsync(tenant, Address);
        var link = await IssueAsync(tenant, Address);
        using var client = Anonymous();

        // Abone görünümü açık konuları listeler; varsayılan: hepsi açık.
        var view = await ViewAsync(client, tenant, link);
        Assert.Equal("subscribed", view.State);
        Assert.Equal(["Haber bülteni", "Kampanya duyuruları"], view.Topics!.Select(t => t.Name).Order());
        Assert.All(view.Topics!, t => Assert.True(t.Subscribed && t.CanChange));

        // Bir konudan çıkış: yalnızca o konu reddedilir; konusuz karar ve diğer konu etkilenmez.
        var left = await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: false);
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        var afterLeaving = (await left.Content.ReadFromJsonAsync<PublicCommunicationView>())!;
        Assert.False(afterLeaving.Topics!.Single(t => t.Key == "haber-bulteni").Subscribed);
        Assert.True(afterLeaving.Topics!.Single(t => t.Key == "kampanya").Subscribed);
        var denied = await DecideAsync(tenant, Address, "haber-bulteni");
        Assert.Equal((CommunicationOutcome.Denied, CommunicationCodes.TopicOptOut), (denied.Outcome, denied.Code));
        Assert.True((await DecideAsync(tenant, Address, "kampanya")).IsAllowed);
        Assert.True((await DecideAsync(tenant, Address, null)).IsAllowed);

        // Tekrar güvenlidir: yeni denetim kaydı yazılmaz. Geri dönüş kişinin kendi çıkışını geri alır.
        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: false)).StatusCode);
        Assert.Equal(1, await AuditCountAsync(tenant, "core.communication-topic-preference"));
        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: true)).StatusCode);
        Assert.True((await DecideAsync(tenant, Address, "haber-bulteni")).IsAllowed);
        Assert.Equal(2, await AuditCountAsync(tenant, "core.communication-topic-preference"));

        // Yönetici konudan çıkışı kaydedebilir ve listede görür; adres denetim kaydında maskelidir.
        var recorded = await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "kampanya", true, null));
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        var page = (await manager.GetFromJsonAsync<TopicPreferencePageDto>("/api/communication/topic-preferences?optedOut=true"))!;
        Assert.Equal(("kampanya", "manual"), (page.Items.Single().TopicKey, page.Items.Single().Source));
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "kampanya")).Code);
        Assert.DoesNotContain(Address, await AuditTextAsync(tenant));
    }

    [Fact]
    public async Task A_topic_opt_out_never_affects_transactional_messages_and_group_decisions_are_per_address()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        await GrantAsync(tenant, Address);
        await GrantAsync(tenant, "baska@b.test");
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, null))).StatusCode);

        using var scope = OpenScope(tenant.Id);
        var preferences = scope.ServiceProvider.GetRequiredService<ICommunicationPreferences>();
        var marketing = await preferences.EvaluateManyAsync(
            CommunicationChannel.Email, CommunicationPurpose.Marketing, [Address, "BASKA@b.test"], "haber-bulteni", CancellationToken.None);
        Assert.Equal(CommunicationCodes.TopicOptOut, marketing[Address].Code);
        Assert.True(marketing["BASKA@b.test"].IsAllowed);

        var transactional = await preferences.EvaluateManyAsync(
            CommunicationChannel.Email, CommunicationPurpose.Transactional, [Address], "haber-bulteni", CancellationToken.None);
        Assert.True(transactional[Address].IsAllowed);
    }

    // ---- Hata / doğrulama ----

    [Fact]
    public async Task Invalid_topic_definitions_and_preferences_are_rejected_and_the_key_cannot_change()
    {
        var (tenant, manager) = await ManagerAsync();

        foreach (var (key, name) in new[] { ("Haber", "Ad"), ("haber bulteni", "Ad"), ("-haber", "Ad"), ("", "Ad"), ("haber", " "), (new string('a', 41), "Ad") })
        {
            var response = await manager.PutAsJsonAsync("/api/communication/topics", new SaveTopicRequest(key, name, true));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Aynı değerle yeniden kayıt denetim kaydı yazmaz; ad değişince güncellenir; anahtar sabittir (aynı anahtar aynı satırı günceller).
        var first = await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        var again = await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        var renamed = await SaveTopicAsync(manager, "haber-bulteni", "Aylık bülten");
        Assert.Equal((first.Id, first.Id), (again.Id, renamed.Id));
        Assert.Equal(2, await AuditCountAsync(tenant, "core.communication-topic"));
        Assert.Single((await manager.GetFromJsonAsync<List<TopicDto>>("/api/communication/topics"))!);

        // Bilinmeyen konu, geçersiz adres ve geçersiz kaynak.
        foreach (var request in new[]
        {
            new SaveTopicPreferenceRequest("Email", Address, "yok-boyle-konu", true, null),
            new SaveTopicPreferenceRequest("Email", "adres-degil", "haber-bulteni", true, null),
            new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, "system"),
            new SaveTopicPreferenceRequest("Fax", Address, "haber-bulteni", true, null),
        })
        {
            var response = await manager.PutAsJsonAsync("/api/communication/topic-preferences", request);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{request}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_topic_never_grants_consent_and_a_company_recorded_opt_out_cannot_be_reversed_by_the_person()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        using var client = Anonymous();

        // Pazarlama izni yokken konuya dönmek izin vermez: karar "bilinmiyor" kalır ve tercih merkezi konu göstermez.
        var noConsentLink = await IssueAsync(tenant, "izinsiz@b.test");
        var recorded = await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", "izinsiz@b.test", "haber-bulteni", false, null));
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        Assert.Equal(CommunicationOutcome.Unknown, (await DecideAsync(tenant, "izinsiz@b.test", "haber-bulteni")).Outcome);
        Assert.Null((await ViewAsync(client, tenant, noConsentLink)).Topics);

        // Firmanın kaydettiği çıkışı kişi bağlantıyla geri açamaz; yetkili kullanıcı açabilir.
        await GrantAsync(tenant, Address);
        var link = await IssueAsync(tenant, Address);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, null))).StatusCode);
        var view = await ViewAsync(client, tenant, link);
        Assert.Equal((false, false), (view.Topics!.Single().Subscribed, view.Topics!.Single().CanChange));
        var refused = await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: true);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "haber-bulteni")).Code);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", false, null))).StatusCode);
        Assert.True((await DecideAsync(tenant, Address, "haber-bulteni")).IsAllowed);

        // İçe aktarma, çıkışı geri açamaz (çıkış her kaynakla kaydedilir, geri açma yalnızca kişi ya da yetkili kullanıcıdır).
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, "import"))).StatusCode);
        var reopen = await manager.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", false, "import"));
        Assert.Equal(HttpStatusCode.BadRequest, reopen.StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "haber-bulteni")).Code);
    }

    [Fact]
    public async Task Unknown_and_closed_topics_are_refused_in_the_portal_and_closed_topics_keep_their_opt_outs()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        await GrantAsync(tenant, Address);
        var link = await IssueAsync(tenant, Address);
        using var client = Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetTopicAsync(client, tenant, link, "yok-boyle-konu", subscribed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetTopicAsync(client, tenant, link, "Bozuk Anahtar", subscribed: false)).StatusCode);

        // Kapalı konu portalda görünmez ve değiştirilemez; mevcut çıkış korunur, yeniden açılınca geri gelir.
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni", isActive: false);
        Assert.Empty((await ViewAsync(client, tenant, link)).Topics!);
        Assert.Equal(HttpStatusCode.Conflict, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: true)).StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "haber-bulteni")).Code);
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni", isActive: true);
        Assert.False((await ViewAsync(client, tenant, link)).Topics!.Single().Subscribed);
    }

    [Fact]
    public async Task An_expired_link_can_still_leave_a_topic_but_cannot_return()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        await GrantAsync(tenant, Address);
        var link = await IssueAsync(tenant, Address);
        using (var scope = OpenScope(tenant.Id))
        {
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().CommunicationLinkTokens
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
        }

        using var client = Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: false)).StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "haber-bulteni")).Code);
        var view = await ViewAsync(client, tenant, link);
        Assert.False(view.Topics!.Single().CanChange);
        var refused = await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: true);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenant, Address, "haber-bulteni")).Code);
    }

    [Fact]
    public async Task A_blocked_address_gets_no_topic_controls_and_cannot_return_to_a_topic()
    {
        var (tenant, manager) = await ManagerAsync();
        await SaveTopicAsync(manager, "haber-bulteni", "Haber bülteni");
        await GrantAsync(tenant, Address);
        var link = await IssueAsync(tenant, Address);
        using var client = Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: false)).StatusCode);

        using (var scope = OpenScope(tenant.Id))
        {
            await scope.ServiceProvider.GetRequiredService<ICommunicationAdministration>().SuppressAsync(
                new SuppressionRequest(CommunicationChannel.Email, Address, SuppressionReason.HardBounce, ConsentSources.Provider), CancellationToken.None);
        }

        Assert.Null((await ViewAsync(client, tenant, link)).Topics);
        Assert.Equal(HttpStatusCode.Conflict, (await SetTopicAsync(client, tenant, link, "haber-bulteni", subscribed: true)).StatusCode);
    }

    // ---- Tenant izolasyonu ----

    [Fact]
    public async Task Topics_and_opt_outs_never_cross_tenants()
    {
        var (tenantA, managerA) = await ManagerAsync();
        var (tenantB, managerB) = await ManagerAsync();
        await SaveTopicAsync(managerA, "haber-bulteni", "A bülteni");
        await GrantAsync(tenantA, Address);
        await GrantAsync(tenantB, Address);
        var linkA = await IssueAsync(tenantA, Address);
        var linkB = await IssueAsync(tenantB, Address);
        using var client = Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await SetTopicAsync(client, tenantA, linkA, "haber-bulteni", subscribed: false)).StatusCode);

        // B'nin yöneticisi A'nın konusunu ve çıkışlarını görmez; aynı adres B'de etkilenmez.
        Assert.Empty((await managerB.GetFromJsonAsync<List<TopicDto>>("/api/communication/topics"))!);
        Assert.Empty((await managerB.GetFromJsonAsync<TopicPreferencePageDto>("/api/communication/topic-preferences"))!.Items);
        Assert.Single((await managerA.GetFromJsonAsync<TopicPreferencePageDto>("/api/communication/topic-preferences"))!.Items);
        Assert.True((await DecideAsync(tenantB, Address, "haber-bulteni")).IsAllowed);

        // B'de aynı anahtarlı konu tanımlı değilken B'nin tercih merkezi ve yöneticisi A'nın konusunu değiştiremez.
        Assert.Equal(HttpStatusCode.Conflict, (await SetTopicAsync(client, tenantB, linkB, "haber-bulteni", subscribed: false)).StatusCode);
        var crossAdmin = await managerB.PutAsJsonAsync("/api/communication/topic-preferences", new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, null));
        Assert.Equal(HttpStatusCode.BadRequest, crossAdmin.StatusCode);

        // A'nın belirteci B'nin adresinde çalışmaz.
        Assert.Equal(HttpStatusCode.NotFound, (await SetTopicAsync(client, tenantB, linkA, "haber-bulteni", subscribed: false)).StatusCode);
        Assert.Equal(CommunicationCodes.TopicOptOut, (await DecideAsync(tenantA, Address, "haber-bulteni")).Code);

        // Aynı anahtar iki tenant'ta ayrı tanımlanabilir (anahtar tenant başına benzersizdir).
        await SaveTopicAsync(managerB, "haber-bulteni", "B bülteni");
        Assert.Equal("B bülteni", (await managerB.GetFromJsonAsync<List<TopicDto>>("/api/communication/topics"))!.Single().Name);
        Assert.Equal("A bülteni", (await managerA.GetFromJsonAsync<List<TopicDto>>("/api/communication/topics"))!.Single().Name);
    }

    // ---- Yetki ----

    [Fact]
    public async Task Topic_endpoints_require_the_communication_permissions_and_the_public_endpoint_needs_a_token()
    {
        var tenant = await factory.CreateTenantAsync();
        var none = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadCommunication], reader);
        await factory.CreateRoleAsync(tenant.Id, [ManageCommunication], manager);
        using var noneClient = await LoginAsync(none);
        using var readerClient = await LoginAsync(reader);
        using var managerClient = await LoginAsync(manager);
        var topic = new SaveTopicRequest("haber-bulteni", "Haber bülteni", true);
        var pref = new SaveTopicPreferenceRequest("Email", Address, "haber-bulteni", true, null);

        // Hiç izni olmayan okuyamaz, yazamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync("/api/communication/topics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync("/api/communication/topic-preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.PutAsJsonAsync("/api/communication/topics", topic)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.PutAsJsonAsync("/api/communication/topic-preferences", pref)).StatusCode);

        // Okuyucu okur ama konu tanımlayamaz, tercih kaydedemez.
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/communication/topics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/communication/topic-preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync("/api/communication/topics", topic)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync("/api/communication/topic-preferences", pref)).StatusCode);

        // Yazma izni okuma izni vermez (ayrı yetkiler); yazan konu tanımlar ve tercih kaydeder.
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.GetAsync("/api/communication/topics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.PutAsJsonAsync("/api/communication/topics", topic)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.PutAsJsonAsync("/api/communication/topic-preferences", pref)).StatusCode);

        // Giriş yapmamış istemci kimliksiz: 401. Herkese açık uç yalnızca POST'tur ve geçerli belirteç ister; belirteç başka yetki vermez.
        using var anonymousBrowser = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousBrowser.GetAsync("/api/communication/topics")).StatusCode);
        using var anonymous = Anonymous();
        var get = await anonymous.GetAsync($"/api/public/communication/{tenant.Id:N}/topic");
        Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, get.StatusCode.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await SetTopicAsync(anonymous, tenant, LinkTokens.New(), "haber-bulteni", subscribed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetTopicAsync(anonymous, tenant, "kisa", "haber-bulteni", subscribed: false)).StatusCode);
    }

    // ---- Yardımcılar ----

    private async Task<(Tenant Tenant, HttpClient Manager)> ManagerAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadCommunication, ManageCommunication, SuppressCommunication, ReadAudit], manager);
        return (tenant, await LoginAsync(manager));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<TopicDto> SaveTopicAsync(HttpClient client, string key, string name, bool isActive = true)
    {
        var response = await client.PutAsJsonAsync("/api/communication/topics", new SaveTopicRequest(key, name, isActive));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TopicDto>())!;
    }

    private HttpClient Anonymous() =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private static string TokenOf(UnsubscribeLink link) => link.PageUrl[(link.PageUrl.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private static Task<HttpResponseMessage> SetTopicAsync(HttpClient client, Tenant tenant, UnsubscribeLink link, string topic, bool subscribed) =>
        SetTopicAsync(client, tenant, TokenOf(link), topic, subscribed);

    private static Task<HttpResponseMessage> SetTopicAsync(HttpClient client, Tenant tenant, string token, string topic, bool subscribed) =>
        client.PostAsJsonAsync($"/api/public/communication/{tenant.Id:N}/topic", new PublicTopicRequest(token, topic, subscribed));

    private static async Task<PublicCommunicationView> ViewAsync(HttpClient client, Tenant tenant, UnsubscribeLink link)
    {
        var response = await client.PostAsJsonAsync($"/api/public/communication/{tenant.Id:N}/view", new PublicCommunicationTokenRequest(TokenOf(link)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicCommunicationView>())!;
    }

    private async Task<UnsubscribeLink> IssueAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        return (await scope.ServiceProvider.GetRequiredService<IUnsubscribeLinks>().IssueAsync(CommunicationChannel.Email, address, "test", CancellationToken.None))!;
    }

    private async Task GrantAsync(Tenant tenant, string address)
    {
        using var scope = OpenScope(tenant.Id);
        await scope.ServiceProvider.GetRequiredService<ICommunicationAdministration>().RecordConsentAsync(
            new ConsentChange(CommunicationChannel.Email, address, CommunicationPurpose.Marketing, ConsentStatus.Granted, ConsentSources.Manual, "test-kaniti", "test"), CancellationToken.None);
    }

    private async Task<CommunicationDecision> DecideAsync(Tenant tenant, string address, string? topic)
    {
        using var scope = OpenScope(tenant.Id);
        var decisions = await scope.ServiceProvider.GetRequiredService<ICommunicationPreferences>().EvaluateManyAsync(
            CommunicationChannel.Email, CommunicationPurpose.Marketing, [address], topic, CancellationToken.None);
        return decisions[address];
    }

    private async Task<int> AuditCountAsync(Tenant tenant, string entityKey)
    {
        using var scope = OpenScope(tenant.Id);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking().CountAsync(a => a.EntityKey == entityKey);
    }

    private async Task<string> AuditTextAsync(Tenant tenant)
    {
        using var scope = OpenScope(tenant.Id);
        var rows = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == "core.communication-topic-preference").ToListAsync();
        return string.Join('\n', rows.Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
    }

    private IServiceScope OpenScope(Guid tenantId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.NewGuid(), Guid.NewGuid());
        return scope;
    }
}
