using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Communication;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 1: ortak iletişim tercihi ve engel. Dört test türü: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki.
/// </summary>
public class CommunicationPreferenceTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Email = "ayse.demir@example.com";

    [Fact]
    public async Task Consent_is_recorded_with_history_and_drives_the_decision()
    {
        var (client, _) = await ManagerAsync();

        Assert.Equal("Unknown", (await CheckAsync(client, "Marketing", "AYSE.Demir@Example.com")).Outcome);

        var granted = await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "web-formu-v3"));
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var preference = (await granted.Content.ReadFromJsonAsync<PreferenceDto>())!;
        Assert.Equal((Email, "Granted", "manual", "web-formu-v3"), (preference.Address, preference.Status, preference.Source, preference.EvidenceRef));

        // Adres büyük/küçük harf ve boşluktan bağımsız aynı iletişim noktasıdır.
        var decision = await CheckAsync(client, "Marketing", "  AYSE.Demir@Example.com ");
        Assert.Equal(("Allowed", "granted"), (decision.Outcome, decision.Code));

        // Aynı kayıt tekrar gelirse olay ve denetim kaydı eklenmez (tekrarlı istek güvenli).
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "web-formu-v3"))).StatusCode);

        var denied = await client.PutAsJsonAsync("/api/communication/preferences", Consent("Denied", reason: "Müşteri aradı"));
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal(("Denied", "denied"), ((await CheckAsync(client, "Marketing", Email)).Outcome, (await CheckAsync(client, "Marketing", Email)).Code));

        var events = (await client.GetFromJsonAsync<List<ConsentEventDto>>($"/api/communication/preferences/{preference.Id}/events"))!;
        Assert.Equal(2, events.Count);
        Assert.Equal(("Granted", "Denied", "Müşteri aradı"), (events[0].FromStatus, events[0].ToStatus, events[0].Reason));
        Assert.Equal(("Unknown", "Granted", "web-formu-v3"), (events[1].FromStatus, events[1].ToStatus, events[1].EvidenceRef));

        var page = (await client.GetFromJsonAsync<PreferencePageDto>("/api/communication/preferences?channel=Email&status=Denied"))!;
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Audit_log_holds_a_masked_address_only()
    {
        var (client, tenantId) = await ManagerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "kanıt-1"))).StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var entries = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == "core.communication-preference").ToListAsync();
        var entry = Assert.Single(entries);
        Assert.Contains("a***@example.com", entry.Changes);
        Assert.DoesNotContain(Email, entry.Changes);
    }

    [Fact]
    public async Task Opt_out_suppression_overrides_consent_for_marketing_only_and_is_idempotent_and_liftable()
    {
        var (client, _) = await ManagerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "kanıt-1"))).StatusCode);

        var first = await SuppressAsync(client, "OptOut");
        var again = await SuppressAsync(client, "OptOut");
        Assert.Equal(first.Id, again.Id);
        Assert.True(first.IsActive);

        Assert.Equal("suppressed:opt-out", (await CheckAsync(client, "Marketing", Email)).Code);
        Assert.True((await CheckAsync(client, "Transactional", Email)).Outcome == "Allowed");

        // Kaldırma gerekçe ister; satır silinmez, geçmişte kalır ve izin kaydı bozulmadan geri gelir.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/communication/suppressions/{first.Id}/lift", new LiftSuppressionRequest(" "))).StatusCode);
        var lifted = await client.PostAsJsonAsync($"/api/communication/suppressions/{first.Id}/lift", new LiftSuppressionRequest("Müşteri yazılı olarak yeniden istedi"));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        var view = (await lifted.Content.ReadFromJsonAsync<SuppressionDto>())!;
        Assert.False(view.IsActive);
        Assert.Equal("Müşteri yazılı olarak yeniden istedi", view.LiftReason);
        Assert.Equal("granted", (await CheckAsync(client, "Marketing", Email)).Code);

        var all = (await client.GetFromJsonAsync<SuppressionPageDto>("/api/communication/suppressions"))!;
        Assert.Single(all.Items);
        Assert.Empty((await client.GetFromJsonAsync<SuppressionPageDto>("/api/communication/suppressions?active=true"))!.Items);
    }

    [Fact]
    public async Task Hard_bounce_blocks_every_purpose()
    {
        var (client, _) = await ManagerAsync();
        await SuppressAsync(client, "HardBounce");

        foreach (var purpose in new[] { "Marketing", "Transactional", "Service" })
        {
            var decision = await CheckAsync(client, purpose, Email);
            Assert.Equal(("Denied", "suppressed:hard-bounce"), (decision.Outcome, decision.Code));
        }
    }

    [Fact]
    public async Task Invalid_input_is_rejected_and_writes_nothing()
    {
        var (client, tenantId) = await ManagerAsync();

        var cases = new[]
        {
            Consent("Granted", evidence: null),                                   // kanıt/gerekçe yok
            Consent("Granted", evidence: "x") with { Address = "ayse" },          // geçersiz adres
            Consent("Granted", evidence: "x") with { Channel = "1" },             // sayı kabul edilmez
            Consent("Granted", evidence: "x") with { Purpose = "Reklam" },        // bilinmeyen amaç
            Consent("Unknown", evidence: "x"),                                    // Unknown kaydedilmez
            Consent("Granted", evidence: "x") with { Source = "system" },         // sistem kaynağı API'den kullanılamaz
            Consent("Granted", evidence: "x") with { Source = "provider" },
        };
        foreach (var request in cases)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/communication/preferences", request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, "Bilinmeyen", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, "Manual", null, DateTimeOffset.UtcNow.AddDays(-1)))).StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.CommunicationPreferences.AnyAsync());
        Assert.False(await db.ConsentEvents.AnyAsync());
        Assert.False(await db.CommunicationSuppressions.AnyAsync());
    }

    [Fact]
    public async Task A_denial_is_not_overridden_by_an_import_or_without_evidence_but_staff_can_reopen_it()
    {
        var (client, _) = await ManagerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", Consent("Denied", reason: "İstemiyor"))).StatusCode);

        var import = await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "liste-2026", source: "import"));
        Assert.Equal(HttpStatusCode.BadRequest, import.StatusCode);
        var noEvidence = await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", reason: "sadece gerekçe"));
        Assert.Equal(HttpStatusCode.BadRequest, noEvidence.StatusCode);
        Assert.Equal("denied", (await CheckAsync(client, "Marketing", Email)).Code);

        var reopened = await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "imzalı-form-118", source: "form"));
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("granted", (await CheckAsync(client, "Marketing", Email)).Code);
    }

    [Fact]
    public async Task Consent_import_never_removes_a_suppression()
    {
        var (client, _) = await ManagerAsync();
        await SuppressAsync(client, "Complaint");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "liste-2026", source: "import"))).StatusCode);

        Assert.Equal("suppressed:complaint", (await CheckAsync(client, "Marketing", Email)).Code);
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_it_and_has_its_own_state_for_the_same_address()
    {
        var (clientA, _) = await ManagerAsync();
        var (clientB, tenantB) = await ManagerAsync();

        var preference = (await (await clientA.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "kanıt-1"))).Content.ReadFromJsonAsync<PreferenceDto>())!;
        var suppression = await SuppressAsync(clientA, "OptOut");

        Assert.Empty((await clientB.GetFromJsonAsync<PreferencePageDto>("/api/communication/preferences"))!.Items);
        Assert.Empty((await clientB.GetFromJsonAsync<SuppressionPageDto>("/api/communication/suppressions"))!.Items);
        Assert.Equal("unknown", (await CheckAsync(clientB, "Marketing", Email)).Code);

        // A'nın kayıt kimliğiyle B'den okuma/kaldırma "bulunamadı" döner; varlığı belli edilmez.
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/communication/preferences/{preference.Id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"/api/communication/suppressions/{suppression.Id}/lift", new LiftSuppressionRequest("deneme"))).StatusCode);
        Assert.True((await clientA.GetFromJsonAsync<SuppressionPageDto>("/api/communication/suppressions?active=true"))!.Items.Single().IsActive);

        // Aynı adres B'de bağımsız kaydedilir (benzersizlik tenant ile birlikte).
        Assert.Equal(HttpStatusCode.OK, (await clientB.PutAsJsonAsync("/api/communication/preferences", Consent("Denied", reason: "B'ye ret"))).StatusCode);
        Assert.Equal("denied", (await CheckAsync(clientB, "Marketing", Email)).Code);
        Assert.Equal("suppressed:opt-out", (await CheckAsync(clientA, "Marketing", Email)).Code);

        // Veritabanı katmanı da: B'nin oturumu A'nın satırını göremez.
        using var scope = factory.OpenScope(tenantB);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(1, await db.CommunicationPreferences.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.CommunicationSuppressions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Permissions_are_enforced_on_every_endpoint()
    {
        var tenant = await factory.CreateTenantAsync();
        var none = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var suppressor = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadCommunication], reader);
        await factory.CreateRoleAsync(tenant.Id, [ManageCommunication], manager);
        await factory.CreateRoleAsync(tenant.Id, [SuppressCommunication], suppressor);
        using var noneClient = await LoginAsync(none);
        using var readerClient = await LoginAsync(reader);
        using var managerClient = await LoginAsync(manager);
        using var suppressorClient = await LoginAsync(suppressor);

        // Hiç izni olmayan okuyamaz, kontrol edemez, yazamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync("/api/communication/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.GetAsync("/api/communication/suppressions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneClient.PostAsJsonAsync("/api/communication/check", new CheckCommunicationRequest("Email", "Marketing", Email))).StatusCode);

        // Okuyucu kontrol eder ama kaydetmez, engellemez.
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/communication/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.PostAsJsonAsync("/api/communication/check", new CheckCommunicationRequest("Email", "Marketing", Email))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, "Manual", null, null))).StatusCode);

        // İzin kaydeden engelleyemez; engelleyen izin kaydedemez (ayrı yetkiler).
        Assert.Equal(HttpStatusCode.OK, (await managerClient.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, "Manual", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await suppressorClient.PutAsJsonAsync("/api/communication/preferences", Consent("Granted", evidence: "x"))).StatusCode);
        var suppression = await suppressorClient.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, "Manual", null, null));
        Assert.Equal(HttpStatusCode.OK, suppression.StatusCode);
        var id = (await suppression.Content.ReadFromJsonAsync<SuppressionDto>())!.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsJsonAsync($"/api/communication/suppressions/{id}/lift", new LiftSuppressionRequest("deneme"))).StatusCode);

        // Giriş yapmamış istemci kimliksiz: 401.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/communication/preferences")).StatusCode);
    }

    private static SaveConsentRequest Consent(string status, string? evidence = null, string? reason = null, string? source = null) =>
        new("Email", Email, "Marketing", status, source, evidence, reason, null, null);

    private static async Task<CommunicationDecisionDto> CheckAsync(HttpClient client, string purpose, string address)
    {
        var response = await client.PostAsJsonAsync("/api/communication/check", new CheckCommunicationRequest("Email", purpose, address));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommunicationDecisionDto>())!;
    }

    private static async Task<SuppressionDto> SuppressAsync(HttpClient client, string reason)
    {
        var response = await client.PostAsJsonAsync("/api/communication/suppressions", new SaveSuppressionRequest("Email", Email, reason, "Test", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SuppressionDto>())!;
    }

    private async Task<(HttpClient Client, Guid TenantId)> ManagerAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadCommunication, ManageCommunication, SuppressCommunication, ReadAudit], manager);
        return (await LoginAsync(manager), tenant.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
