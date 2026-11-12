using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Notifications;
using Modulix.Shared.Records;
using static Modulix.Modules.Fleet.FleetModule.Permissions;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 2b: süreli belgenin bitişine kala uyarısı. Mutlu yol ("bitişine 30 gün kala" kuralı taramayla yalnızca o güne düşen geçerli
/// belgeyi bulur, "Bildirim gönder" alıcının kutusuna kayıt değişkenli iletiyi bir kez yazar), doğrulama (desteklenmeyen tarih alanı, boş ileti,
/// alıcısız adım yayınlanmaz), tenant izolasyonu (tarama başka tenant'ın belgesini bulmaz) ve yetki (alıcı kaydı göremiyorsa bildirim gitmez;
/// belge listesinde sahip adı yalnızca okunabilen türlerde).
/// </summary>
public class CredentialExpiryTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private const string Credentials = "/api/records/core.credential";
    private const string Vehicles = "/api/records/fleet.vehicle";
    private const string Drivers = "/api/records/logistics.driver";
    private const string People = "/api/records/core.person";

    private DateOnly Today => DateOnly.FromDateTime(fixture.Time.GetUtcNow().UtcDateTime);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Expiry_rule_notifies_once_for_credentials_ending_on_the_target_day()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.FleetManager);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34UYR01", ["kindKey"] = "Truck" });
        var due = await PostAsync(admin, Credentials, Credential(truck.Id, "Inspection", "MUA-30", Today.AddDays(30)));
        await PostAsync(admin, Credentials, Credential(truck.Id, "TrafficInsurance", "POL-31", Today.AddDays(31)));
        var revoked = await PostAsync(admin, Credentials, Credential(truck.Id, "Casco", "KSK-30", Today.AddDays(30)));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Credentials}/{revoked.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "revoked" }, revoked.Version))).StatusCode);

        var rule = await PublishRuleAsync(admin, Notify(s.FleetManager.Id, "{{core.credential.documentNo}} belgesinin bitişine 30 gün kaldı"));

        await RunAsync(s.TenantId);
        var runs = (await admin.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?ruleId={rule}"))!;
        Assert.Equal([due.Id], runs.Items.Select(r => r.RecordId)); // iptal edilen ve 31 gün kalan belge taranmaz

        var inbox = (await manager.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!;
        var detail = (await admin.GetFromJsonAsync<AutomationRunDetailDto>($"/api/automation/runs/{runs.Items[0].Id}"))!;
        Assert.True(inbox.Items.Count == 1, string.Join(" | ", detail.Steps.Select(st => $"{st.Status}: {st.Summary}")) + " run=" + detail.Run.Status + " note=" + detail.Run.Note);
        var notification = Assert.Single(inbox.Items);
        Assert.Equal("MUA-30 belgesinin bitişine 30 gün kaldı", notification.Title);

        // Aynı gün yeniden tarama ikinci bildirim yazmaz.
        await RunAsync(s.TenantId);
        Assert.Single((await manager.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items);

        // Listede sahip adı: araç plakası, sürücü adı soyadı.
        var person = (await PostAsync(admin, People, new JsonObject { ["firstName"] = "Nuri", ["lastName"] = "Ay" })).Id;
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.ToString() });
        await PostAsync(admin, Credentials, new JsonObject
        {
            ["ownerEntityKey"] = "logistics.driver", ["ownerId"] = driver.Id.ToString(), ["typeKey"] = "Src",
        });
        var labels = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Credentials}?pageSize=50"))!.Items
            .Select(c => c.Values["ownerLabel"]?.GetValue<string>()).Distinct().Order().ToList();
        Assert.Equal(["34UYR01", "Nuri Ay"], labels);
    }

    [Fact]
    public async Task Rules_with_unsupported_dates_or_empty_messages_are_not_published()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        await AssertNotPublishableAsync(admin, "metin alanına tarih tetikleyicisi", new AutomationDefinition(
            new DateRelativeTrigger("core.credential.documentNo", -30), null,
            [new AutomationActionStep("bildir", NotifyAutomation.NotifyAction, new JsonObject { ["recipientId"] = s.FleetManager.Id.ToString(), ["title"] = "x" })]));
        await AssertNotPublishableAsync(admin, "boş ileti", Notify(s.FleetManager.Id, ""));
        await AssertNotPublishableAsync(admin, "alıcısız adım", new AutomationDefinition(
            new DateRelativeTrigger("core.credential.validTo", -30), null,
            [new AutomationActionStep("bildir", NotifyAutomation.NotifyAction, new JsonObject { ["title"] = "Belge bitiyor" })]));
    }

    /// <summary>Kural ya oluşturulurken ya da yayınlanırken reddedilmeli.</summary>
    private static async Task AssertNotPublishableAsync(HttpClient client, string label, AutomationDefinition definition)
    {
        var create = await CreateRuleAsync(client, definition);
        if (!create.IsSuccessStatusCode)
        {
            return;
        }

        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.False(publish.IsSuccessStatusCode, $"{label} yayınlandı: {await publish.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task The_scan_finds_only_the_tenants_own_credentials()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var truckA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "06UYR01", ["kindKey"] = "Truck" });
        var truckB = await PostAsync(adminB, Vehicles, new JsonObject { ["plate"] = "06UYR02", ["kindKey"] = "Truck" });
        var credentialA = await PostAsync(adminA, Credentials, Credential(truckA.Id, "Inspection", "A-1", Today.AddDays(30)));
        await PostAsync(adminB, Credentials, Credential(truckB.Id, "Inspection", "B-1", Today.AddDays(30)));

        var rule = await PublishRuleAsync(adminA, Notify(a.FleetManager.Id, "Muayene bitiyor"));
        await RunAsync(a.TenantId);

        var runs = (await adminA.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?ruleId={rule}"))!;
        Assert.Equal([credentialA.Id], runs.Items.Select(r => r.RecordId));
        using var managerB = await LoginAsync(b.FleetManager);
        Assert.Empty((await managerB.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items);
    }

    [Fact]
    public async Task Recipients_who_cannot_see_the_credential_are_not_notified()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var driverClerk = await LoginAsync(s.DriverClerk);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34UYR09", ["kindKey"] = "Truck" });
        await PostAsync(admin, Credentials, Credential(truck.Id, "Inspection", "GZL-1", Today.AddDays(30)));

        // Sürücü izni olan ama araç göremeyen kişiye araç belgesinin iletisi gitmez (başlıktaki belge no sızmaz).
        await PublishRuleAsync(admin, Notify(s.DriverClerk.Id, "{{core.credential.documentNo}} bitiyor"));
        await RunAsync(s.TenantId);
        Assert.Empty((await driverClerk.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items);

        // Aynı kişi belge listesinde araç belgelerini görmez.
        Assert.Empty((await driverClerk.GetFromJsonAsync<RecordPageResponse>(Credentials))!.Items);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser FleetManager, ModulixUser DriverClerk);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await fixture.Factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        var manager = await fixture.Factory.CreateUserAsync(tenant);
        var clerk = await fixture.Factory.CreateUserAsync(tenant);
        using (var scope = fixture.Factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await fixture.Factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await fixture.Factory.CreateRoleAsync(tenant.Id, [VehiclesRead, VehiclesWrite], Scope.All, manager);
        await fixture.Factory.CreateRoleAsync(tenant.Id, [DriversRead], Scope.All, clerk);
        return new Setup(tenant.Id, admin, manager, clerk);
    }

    private static AutomationDefinition Notify(Guid recipient, string title) => new(
        new DateRelativeTrigger("core.credential.validTo", -30), null,
        [new AutomationActionStep("bildir", NotifyAutomation.NotifyAction, new JsonObject { ["recipientId"] = recipient.ToString(), ["title"] = title })]);

    private static JsonObject Credential(Guid vehicleId, string type, string no, DateOnly validTo) => new()
    {
        ["ownerEntityKey"] = "fleet.vehicle",
        ["ownerId"] = vehicleId.ToString(),
        ["typeKey"] = type,
        ["documentNo"] = no,
        ["validTo"] = Iso(validTo),
    };

    private async Task RunAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.NotNull(round);
    }

    private static Task<HttpResponseMessage> CreateRuleAsync(HttpClient client, AutomationDefinition definition) =>
        client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            "core", "core.credential", $"Belge {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));

    private static async Task<Guid> PublishRuleAsync(HttpClient client, AutomationDefinition definition)
    {
        var create = await CreateRuleAsync(client, definition);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        return created.Rule.Id;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
