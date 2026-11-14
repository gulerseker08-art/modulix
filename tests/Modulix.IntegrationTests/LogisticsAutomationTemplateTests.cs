using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Automation;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Notifications;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 bölüm 23: Lojistik başlangıç şablonları uçtan uca. Mutlu yol (şablon taslak açar, alıcı seçilince yayınlanır, tarama bildirimi yazar),
/// doğrulama (alıcı seçilmeden yayınlanmaz), tenant izolasyonu (bir firmada kullanılan şablon diğerinde kural açmaz) ve yetki (yönetme izni ya da
/// kayıt türünü okuma izni olmayan şablonu kullanamaz). Gerçek veritabanı, kayıt boru hattı ve Row-Level Security altında.
/// </summary>
public class LogisticsAutomationTemplateTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Parties = "/api/records/core.party";

    private DateOnly Today => TenantToday.At(fixture.Time.GetUtcNow());

    [Fact]
    public async Task A_template_opens_a_draft_that_publishes_only_after_the_recipient_is_chosen_and_then_notifies()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var dispatcher = await LoginAsync(s.Dispatcher);

        var templates = (await admin.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!
            .Where(t => t.ModuleKey == LogisticsModule.ModuleKey).ToList();
        Assert.Equal(LogisticsAutomationTemplates.All.Select(t => t.Key), templates.Select(t => t.Key));
        Assert.All(templates, t => Assert.True(t.Available, string.Join(";", t.Issues.Select(i => i.Message))));

        var draft = await UseAsync(admin, LogisticsAutomationTemplates.RequestPendingBeforePickup);
        Assert.Equal("Draft", draft.Rule.Status);
        Assert.Equal(LogisticsModule.Entities.TransportRequest, draft.Rule.EntityKey);
        Assert.Equal(["actions[0].parameters.recipientId"], draft.DraftIssues.Select(i => i.Path));

        // Alıcı seçilmeden kural yayınlanamaz.
        var early = await admin.PostAsJsonAsync($"/api/automation/rules/{draft.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.False(early.IsSuccessStatusCode, await early.Content.ReadAsStringAsync());

        var ready = await ChooseRecipientAsync(admin, draft, s.Dispatcher.Id);
        Assert.Empty(ready.DraftIssues);
        var publish = await admin.PostAsJsonAsync($"/api/automation/rules/{draft.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());

        // Yükleme günü yarın olan, hâlâ onay bekleyen talep: tarama bulur, bildirim alıcının kutusuna kayıt değişkeniyle yazılır.
        var request = await PostAsync(admin, Requests, Request(s.CustomerId, "24 palet gıda", Today.AddDays(1)));
        await PostAsync(admin, Requests, Request(s.CustomerId, "Sonraki haftanın yükü", Today.AddDays(7)));
        await RunAsync(s.TenantId);

        var runs = (await admin.GetFromJsonAsync<AutomationRunPageDto>($"/api/automation/runs?ruleId={draft.Rule.Id}"))!;
        Assert.Equal([request.Id], runs.Items.Select(r => r.RecordId));
        var notification = Assert.Single((await dispatcher.GetFromJsonAsync<NotificationPageDto>("/api/notifications"))!.Items);
        Assert.Equal("Yükleme günü yarın, talep hâlâ onay bekliyor: 24 palet gıda", notification.Title);
    }

    [Fact]
    public async Task Every_logistics_template_passes_real_publish_validation_once_the_recipient_is_chosen()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        foreach (var template in LogisticsAutomationTemplates.All)
        {
            var draft = await UseAsync(admin, template.Key);
            await ChooseRecipientAsync(admin, draft, s.Dispatcher.Id);

            // Yayın tam doğrulamayı çalıştırır; kapalı yayınlanır, böylece sonraki şablonlar etkilenmez.
            var publish = await admin.PostAsJsonAsync($"/api/automation/rules/{draft.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));
            Assert.True(publish.StatusCode == HttpStatusCode.OK, $"{template.Key}: {await publish.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task A_template_used_in_one_tenant_creates_no_rule_in_another()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var rule = await UseAsync(adminA, LogisticsAutomationTemplates.ShipmentOverdue);

        Assert.Single(await RulesAsync(a.TenantId));
        Assert.Empty(await RulesAsync(b.TenantId));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/automation/rules/{rule.Rule.Id}")).StatusCode);
    }

    [Fact]
    public async Task Using_needs_manage_and_the_entity_read_permission_and_an_open_module()
    {
        var s = await SetUpAsync();
        var use = $"/api/automation/templates/{LogisticsAutomationTemplates.RequestCreated}/use";

        // Yalnızca okuma izni: listeler, kullanamaz.
        using var reader = await MemberAsync(s, CoreModule.Permissions.ReadAutomation, RequestsRead);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/automation/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(use, null)).StatusCode);

        // Hiç otomasyon izni yok: listeleyemez.
        using var outsider = await MemberAsync(s, RequestsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/automation/templates")).StatusCode);

        // Taşıma talebini okuma izni yok: talep şablonu "kullanılamıyor" görünür ve taslağı açılmaz (boş bırakılan alıcı bunu gizlemez).
        using var manager = await MemberAsync(s, CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, TripsRead);
        var templates = (await manager.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!;
        Assert.False(templates.Single(t => t.Key == LogisticsAutomationTemplates.RequestCreated).Available);
        Assert.True(templates.Single(t => t.Key == LogisticsAutomationTemplates.TripNotReady).Available);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync(use, null)).StatusCode);
        Assert.Empty(await RulesAsync(s.TenantId));

        // Lojistik kapalıyken şablon listede yoktur ve bulunamaz.
        using var admin = await LoginAsync(s.Admin);
        await fixture.Factory.SetModuleAsync(s.TenantId, LogisticsModule.ModuleKey, enabled: false);
        Assert.DoesNotContain(
            (await admin.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!, t => t.ModuleKey == LogisticsModule.ModuleKey);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(use, null)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, Guid CustomerId, ModulixUser Admin, ModulixUser Dispatcher)
    {
        public Guid TenantId => Tenant.Id;
    }

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, LogisticsModule.ModuleKey, enabled: true);
        await fixture.Factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // müşteri firmayı kayıt ucundan açmak için
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        var dispatcher = await fixture.Factory.CreateUserAsync(tenant);
        using (var scope = fixture.Factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await fixture.Factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        // Bildirimin alıcısı kaydı kendi yetkisiyle görebilmeli (Core kuralı).
        await fixture.Factory.CreateRoleAsync(tenant.Id, [RequestsRead, ShipmentsRead, TripsRead, ExpensesRead], Scope.All, dispatcher);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(
            adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant, customer.Id, admin, dispatcher);
    }

    private async Task<HttpClient> MemberAsync(Setup setup, params string[] permissions)
    {
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.TenantId, permissions, Scope.All, member);
        return await LoginAsync(member);
    }

    private static JsonObject Request(Guid customer, string cargo, DateOnly pickup) => new()
    {
        ["customerPartyId"] = customer.ToString(),
        ["pickupCity"] = "Ankara",
        ["deliveryCity"] = "İzmir",
        ["pickupDate"] = pickup.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["cargoDescription"] = cargo,
    };

    private static async Task<AutomationRuleDetailDto> UseAsync(HttpClient client, string templateKey)
    {
        var use = await client.PostAsync($"/api/automation/templates/{templateKey}/use", null);
        Assert.True(use.StatusCode == HttpStatusCode.Created, $"{templateKey}: {await use.Content.ReadAsStringAsync()}");
        return (await use.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
    }

    /// <summary>Firmanın taslakta yaptığı iş: bildirim adımının alıcısını seçip taslağı kaydeder.</summary>
    private static async Task<AutomationRuleDetailDto> ChooseRecipientAsync(HttpClient client, AutomationRuleDetailDto rule, Guid recipient)
    {
        var draft = rule.Draft!;
        var definition = draft.Definition.Deserialize<AutomationDefinition>(AutomationJson.Options)!;
        definition.Actions[0].Parameters[LogisticsAutomationTemplates.RecipientParameter] = recipient.ToString();

        var update = await client.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}", new UpdateAutomationRuleRequest(
            draft.Name, draft.Description, draft.Priority, draft.StopProcessing, draft.ActiveFrom, draft.ActiveUntil,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options), rule.Rule.Version));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        return (await update.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
    }

    private async Task RunAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.NotNull(round);
    }

    private async Task<IReadOnlyList<AutomationRule>> RulesAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<AutomationRule>().AsNoTracking().ToListAsync();
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
