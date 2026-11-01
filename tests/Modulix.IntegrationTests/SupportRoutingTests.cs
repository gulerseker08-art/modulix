using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Support;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using Modulix.Shared.Support;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 3: yönetilebilir öncelik matrisi, yönlendirme kuralları (sıra, özgünlük, açık atamanın önceliği, pasif sorumlu), kategoriye bağlı zorunlu alanlar, mükerrer uyarısı,
/// birleştirme (veri korunur, farklı müşteri engeli), engelleyici alt işler, müşteri teyidi ve zaman aşımıyla kapanma. Her özellik mutlu yol, doğrulama, yetki ve tenant izolasyonuyla sınanır.
/// </summary>
public class SupportRoutingTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];
    private static readonly string[] LeadPermissions = [.. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel, PriorityOverride, SlaRecalculate, TicketsMerge];
    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage, SlaManage, "core.settings.manage"];

    private sealed record Setup(Tenant Tenant, Guid PartyId, Guid OtherPartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Agent, ModulixUser Agent2, ModulixUser Viewer, ModulixUser NoAccess);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var lead = await factory.CreateUserAsync(tenant);
        var agent = await factory.CreateUserAsync(tenant);
        var agent2 = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var noAccess = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ManagerPermissions, Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, LeadPermissions, Scope.All, lead);
        await factory.CreateRoleAsync(tenant.Id, AgentPermissions, Scope.Own, agent);
        await factory.CreateRoleAsync(tenant.Id, AgentPermissions, Scope.Own, agent2);
        await factory.CreateRoleAsync(tenant.Id, [TicketsRead], Scope.All, viewer);

        Guid partyId, otherPartyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Yönlendirme Müşterisi A.Ş.", Email = "yonlendirme@musteri.example.test" };
            var other = new Party { Name = "Başka Müşteri Ltd." };
            db.Set<Party>().AddRange(party, other);
            await db.SaveChangesAsync();
            partyId = party.Id;
            otherPartyId = other.Id;
        }

        return new Setup(tenant, partyId, otherPartyId, manager, lead, agent, agent2, viewer, noAccess);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> CreateAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<TicketDto> TicketAsync(HttpClient client, Setup s, string subject = "Talep", Guid? party = null, string? impact = null, string? urgency = null, Guid? categoryId = null,
        string? channel = null, string? description = null, DateOnly? eventDate = null, string? clientKey = null) =>
        CreateAsync<TicketDto>(client, "tickets", new SaveTicketRequest(Subject: subject, PartyId: party ?? s.PartyId, Impact: impact, Urgency: urgency, CategoryId: categoryId, Channel: channel,
            Description: description, EventDate: eventDate, ClientKey: clientKey));

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid id, string action, TicketActionRequest request) => client.PostAsJsonAsync($"/api/support/tickets/{id}/{action}", request);

    private static async Task<TicketDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> TakeAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "take", new TicketActionRequest(t.Version)));

    private static async Task<TicketDto> StartAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "start", new TicketActionRequest((await GetAsync(client, t.Id)).Version)));

    private static async Task<TicketDto> ResolveAsync(HttpClient client, TicketDto t) =>
        await OkAsync(await ActAsync(client, t.Id, "resolve", new TicketActionRequest((await GetAsync(client, t.Id)).Version, ResolutionCode: "Resolved", ResolutionNote: "Çözüldü.")));

    private static async Task<List<TicketHistoryDto>> HistoryAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{id}/history"))!;

    private static async Task<List<TicketMessageDto>> MessagesAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketMessageDto>>($"/api/support/tickets/{id}/messages"))!;

    private static async Task<HttpResponseMessage> RuleAsync(HttpClient client, SaveRoutingRuleRequest request) => await client.PostAsJsonAsync("/api/support/routing-rules", request);

    private static async Task<QueueDto> QueueAsync(HttpClient client, string name, bool isDefault = false) =>
        await CreateAsync<QueueDto>(client, "queues", new SaveQueueRequest(Name: name, IsDefault: isDefault));

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<SlaSchedulerRun> RunSchedulerAsync(Guid tenantId)
    {
        var (run, skipped) = await factory.Services.GetRequiredService<SupportJobsRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return run!;
    }

    // ---- Öncelik matrisi

    [Fact]
    public async Task The_priority_matrix_is_managed_per_tenant_and_only_affects_new_or_edited_tickets()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var foreign = await LoginAsync(other.Manager);

        // Tanımlı hücre yokken yerleşik matris görünür (9 hücre); okuma talep okuma iziniyle, yazma yönetim iziniyle.
        var defaults = (await agent.GetFromJsonAsync<List<PriorityCellDto>>("/api/support/priority-matrix"))!;
        Assert.Equal(9, defaults.Count);
        Assert.Equal("Critical", defaults.Single(c => c.Impact == "High" && c.Urgency == "High").Priority);
        Assert.Equal("Low", defaults.Single(c => c.Impact == "Low" && c.Urgency == "Low").Priority);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Low", "Low", "High")]))).StatusCode);

        var before = await TicketAsync(agent, s, "Önce", impact: "Low", urgency: "Low");
        Assert.Equal("Low", before.Priority);

        // Doğrulama: bilinmeyen düzey/öncelik ve yinelenen hücre reddedilir; hiçbir şey yazılmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Huge", "Low", "High")]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Low", "Low", "Urgent")]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Low", "Low", "High"), new("Low", "Low", "Low")]))).StatusCode);
        Assert.Equal("Low", (await agent.GetFromJsonAsync<List<PriorityCellDto>>("/api/support/priority-matrix"))!.Single(c => c.Impact == "Low" && c.Urgency == "Low").Priority);

        // Yönetici matrisi değiştirir: Düşük × Düşük → Kritik (VIP politikası). Tekrar kaydetmek idempotenttir.
        var saved = await manager.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Low", "Low", "Critical")]));
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/support/priority-matrix", new SavePriorityMatrixRequest([new("Low", "Low", "Critical")]))).StatusCode);

        // Yeni talep yeni matrisle hesaplanır; mevcut talebin önceliği değişmez, etki/aciliyeti düzenlenince yeni matris uygulanır; başka tenant etkilenmez.
        var after = await TicketAsync(agent, s, "Sonra", impact: "Low", urgency: "Low");
        Assert.Equal("Critical", after.Priority);
        Assert.Equal("Low", (await GetAsync(agent, before.Id)).Priority);
        var edited = await OkAsync(await agent.PutAsJsonAsync($"/api/support/tickets/{before.Id}", new SaveTicketRequest((await GetAsync(agent, before.Id)).Version, before.Subject, PartyId: s.PartyId, Impact: "Low", Urgency: "Low", TypeKey: before.TypeKey, Channel: before.Channel)));
        Assert.Equal("Critical", edited.Priority);
        var foreignTicket = await TicketAsync(foreign, other, "Yabancı", impact: "Low", urgency: "Low");
        Assert.Equal("Low", foreignTicket.Priority);
        Assert.Equal("Low", (await foreign.GetFromJsonAsync<List<PriorityCellDto>>("/api/support/priority-matrix"))!.Single(c => c.Impact == "Low" && c.Urgency == "Low").Priority);
    }

    // ---- Yönlendirme kuralları

    [Fact]
    public async Task Routing_rules_are_validated_managed_by_permission_and_cannot_reference_other_tenants()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var foreign = await LoginAsync(other.Manager);
        var queue = await QueueAsync(manager, "Telefon", isDefault: true);
        var foreignQueue = await QueueAsync(foreign, "Yabancı", isDefault: true);
        var foreignCategory = await CreateAsync<CategoryDto>(foreign, "categories", new SaveCategoryRequest(Name: "Yabancı kategori"));
        var inactive = await QueueAsync(manager, "Pasif");
        await manager.PutAsJsonAsync($"/api/support/queues/{inactive.Id}", new SaveQueueRequest(inactive.Version, inactive.Name, null, false, false));

        Assert.Equal(HttpStatusCode.Forbidden, (await RuleAsync(agent, new SaveRoutingRuleRequest(Name: "Yetkisiz", QueueId: queue.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/support/routing-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/support/routing/preview", new RoutingPreviewRequest("Web", "Support", "Normal"))).StatusCode);

        async Task<HttpStatusCode> Status(SaveRoutingRuleRequest r) => (await RuleAsync(manager, r)).StatusCode;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Eylemsiz")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "", QueueId: queue.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Pasif kuyruk", QueueId: inactive.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Yabancı kuyruk", QueueId: foreignQueue.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Yabancı kategori", QueueId: queue.Id, CategoryId: foreignCategory.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Yabancı müşteri", QueueId: queue.Id, PartyId: other.PartyId)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Yabancı sorumlu", OwnerId: other.Agent.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Bozuk kanal", QueueId: queue.Id, Channel: "Fax")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Bozuk öncelik", QueueId: queue.Id, Priority: "Urgent")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Status(new SaveRoutingRuleRequest(Name: "Bozuk tür", QueueId: queue.Id, TypeKey: "Olmayan")));
        Assert.Equal(HttpStatusCode.Created, await Status(new SaveRoutingRuleRequest(Name: "Geçerli", QueueId: queue.Id, Channel: "Phone")));
        Assert.Equal(HttpStatusCode.Created, await Status(new SaveRoutingRuleRequest(Name: "Yalnız sorumlu", OwnerId: s.Agent.Id)));

        // Başka tenant kuralı görmez ve değiştiremez; deneme yabancı kimlikleri reddeder.
        Assert.Empty((await foreign.GetFromJsonAsync<PagedResult<RoutingRuleDto>>("/api/support/routing-rules"))!.Items);
        var rule = (await manager.GetFromJsonAsync<PagedResult<RoutingRuleDto>>("/api/support/routing-rules"))!.Items.First();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/routing-rules/{rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.DeleteAsync($"/api/support/routing-rules/{rule.Id}?version={rule.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await foreign.PostAsJsonAsync("/api/support/routing/preview", new RoutingPreviewRequest("Web", "Support", "Normal", PartyId: s.PartyId))).StatusCode);
    }

    [Fact]
    public async Task A_new_ticket_follows_the_first_matching_rule_by_order_and_specificity_and_explicit_assignment_wins()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);
        var general = await QueueAsync(manager, "Genel", isDefault: true);
        var phone = await QueueAsync(manager, "Telefon masası");
        var vip = await QueueAsync(manager, "VIP");
        var billing = await QueueAsync(manager, "Faturalama");
        var invoice = await CreateAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Fatura", DefaultQueueId: billing.Id));
        var refund = await CreateAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "İade", ParentId: invoice.Id));

        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "Telefon", Order: 20, Channel: "Phone", QueueId: phone.Id));
        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "VIP müşteri", Order: 10, PartyId: s.PartyId, QueueId: vip.Id, OwnerId: s.Agent.Id));
        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "Pasif kural", Order: 1, IsActive: false, QueueId: general.Id));

        // En küçük sıra kazanır: VIP müşteri (10) telefondan (20) önce; kural kuyruğu ve sorumluyu birlikte atar, durum Atandı olur ve tarihçeye yazılır.
        var routed = await TicketAsync(lead, s, "VIP", channel: "Phone");
        Assert.Equal((vip.Id, s.Agent.Id, "Assigned"), (routed.QueueId, routed.OwnerId, routed.Status));
        var history = await HistoryAsync(lead, routed.Id);
        Assert.Equal("Yönlendirme kuralı: VIP müşteri", history.Single(h => h.Kind == "RoutedByRule").Reason);

        // Kural eşleşmeyen müşteri telefonla gelirse ikinci kural; sorumlu atanmaz (kural yalnızca kuyruk verir), talep atanmamış kalır.
        var phoneTicket = await TicketAsync(lead, s, "Telefonlu", party: s.OtherPartyId, channel: "Phone");
        Assert.Equal((phone.Id, null, "New"), (phoneTicket.QueueId, phoneTicket.OwnerId, phoneTicket.Status));

        // Hiçbir kural uymazsa kategori / üst kategori kuyruğu, o da yoksa varsayılan kuyruk.
        Assert.Equal(billing.Id, (await TicketAsync(lead, s, "Alt kategori", party: s.OtherPartyId, categoryId: refund.Id, channel: "Web")).QueueId);
        Assert.Equal(general.Id, (await TicketAsync(lead, s, "Hiçbiri", party: s.OtherPartyId, channel: "Web")).QueueId);

        // Atama yetkisiyle verilmiş açık kuyruk/sorumlu kuralı ezer.
        var explicitQueue = await CreateAsync<TicketDto>(lead, "tickets", new SaveTicketRequest(Subject: "Açık kuyruk", PartyId: s.PartyId, QueueId: general.Id));
        Assert.Equal((general.Id, null), (explicitQueue.QueueId, explicitQueue.OwnerId));
        Assert.DoesNotContain(await HistoryAsync(lead, explicitQueue.Id), h => h.Kind == "RoutedByRule");

        // Kural değişince mevcut talep yeniden yönlendirilmez; deneme aynı kod yolunu gösterir.
        var rules = (await manager.GetFromJsonAsync<PagedResult<RoutingRuleDto>>("/api/support/routing-rules"))!.Items;
        var vipRule = rules.Single(r => r.Name == "VIP müşteri");
        await manager.PutAsJsonAsync($"/api/support/routing-rules/{vipRule.Id}", new SaveRoutingRuleRequest(vipRule.Version, vipRule.Name, vipRule.Order, false, null, null, null, null, s.PartyId, vip.Id, null));
        Assert.Equal(vip.Id, (await GetAsync(lead, routed.Id)).QueueId);
        var preview = (await (await manager.PostAsJsonAsync("/api/support/routing/preview", new RoutingPreviewRequest("Phone", "Support", "Normal", PartyId: s.PartyId))).Content.ReadFromJsonAsync<RoutingPreviewDto>())!;
        Assert.Equal(("Telefon", phone.Id), (preview.RuleName, preview.QueueId));
        var none = (await (await manager.PostAsJsonAsync("/api/support/routing/preview", new RoutingPreviewRequest("Web", "Support", "Normal", CategoryId: invoice.Id))).Content.ReadFromJsonAsync<RoutingPreviewDto>())!;
        Assert.False(none.RuleMatched);
        Assert.Equal(billing.Id, none.QueueId);
        Assert.NotNull(agent);
    }

    [Fact]
    public async Task Rules_with_equal_order_resolve_by_specificity_and_a_departed_owner_is_not_assigned()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        var general = await QueueAsync(manager, "Genel", isDefault: true);
        var email = await QueueAsync(manager, "E-posta");
        var special = await QueueAsync(manager, "E-posta VIP");

        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "Z geniş", Order: 10, Channel: "Email", QueueId: email.Id));
        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "A özel", Order: 10, Channel: "Email", PartyId: s.PartyId, QueueId: special.Id));

        // Eşit sırada daha çok kısıtlı (özel) kural kazanır; sonuç tekrarlanabilir.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(special.Id, (await TicketAsync(lead, s, $"Özel {i}", channel: "Email")).QueueId);
        }

        Assert.Equal(email.Id, (await TicketAsync(lead, s, "Geniş", party: s.OtherPartyId, channel: "Email")).QueueId);

        // Üyeliği kapanan sorumlu atanmaz: kural kuyruk eylemiyle sürer, talep atanmamış kalır.
        await RuleAsync(manager, new SaveRoutingRuleRequest(Name: "Eski çalışan", Order: 1, Channel: "Web", QueueId: general.Id, OwnerId: s.Agent2.Id));
        await factory.DisableMembershipAsync(s.Agent2.Id, s.Tenant.Id);
        var departed = await TicketAsync(lead, s, "Ayrılan sorumlu", channel: "Web");
        Assert.Equal((general.Id, null, "New"), (departed.QueueId, departed.OwnerId, departed.Status));
    }

    // ---- Kategoriye bağlı zorunlu alanlar

    [Fact]
    public async Task Category_field_rules_require_fields_on_create_and_edit_inherit_from_the_parent_and_never_block_workflow_actions()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);

        // Yalnızca bilinen alanlar zorunlu yapılabilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/support/categories", new SaveCategoryRequest(Name: "Bozuk", RequiredFields: "partyId,uydurma"))).StatusCode);
        var damage = await CreateAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Hasar", RequiredFields: "description,eventDate"));
        var transport = await CreateAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Taşıma hasarı", ParentId: damage.Id, RequiredFields: "expectedOutcome"));
        var plain = await CreateAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Genel soru"));

        async Task<HttpResponseMessage> Create(Guid? category, string? description = null, DateOnly? eventDate = null, string? outcome = null) =>
            await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Hasar", PartyId: s.PartyId, CategoryId: category, Description: description, EventDate: eventDate, ExpectedOutcome: outcome));

        var missing = await Create(damage.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var body = await missing.Content.ReadAsStringAsync();
        Assert.Contains("description", body);
        Assert.Contains("eventDate", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create(damage.Id, description: "Koli ezilmiş")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(damage.Id, "Koli ezilmiş", new DateOnly(2026, 10, 30))).StatusCode);

        // Alt kategori kendi alanlarına ek olarak üst kategorinin alanlarını da ister.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create(transport.Id, "Koli ezilmiş", new DateOnly(2026, 10, 30))).StatusCode);
        var ok = await Create(transport.Id, "Koli ezilmiş", new DateOnly(2026, 10, 30), "Tazminat");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(plain.Id)).StatusCode);

        // Düzenlemede de uygulanır: zorunlu alan boşaltılamaz; iş akışı eylemleri alan kuralına takılmaz (kural sonradan eklense bile).
        var ticket = (await ok.Content.ReadFromJsonAsync<TicketDto>())!;
        var cleared = await agent.PutAsJsonAsync($"/api/support/tickets/{ticket.Id}", new SaveTicketRequest(ticket.Version, ticket.Subject, null, ticket.TypeKey, ticket.Channel, ticket.PartyId, CategoryId: ticket.CategoryId, EventDate: ticket.EventDate, ExpectedOutcome: ticket.ExpectedOutcome));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cleared.StatusCode);
        var untouched = await TicketAsync(agent, s, "Kuralsız önce", categoryId: plain.Id);
        var plainNow = (await manager.GetFromJsonAsync<CategoryDto>($"/api/support/categories/{plain.Id}"))!;
        await manager.PutAsJsonAsync($"/api/support/categories/{plain.Id}", new SaveCategoryRequest(plainNow.Version, plainNow.Name, null, null, true, 0, "description"));
        Assert.Equal("Assigned", (await TakeAsync(agent, untouched)).Status);

        // Başka tenant'ın kategorisiyle talep açılamaz.
        var foreign = await CreateAsync<CategoryDto>(await LoginAsync(other.Manager), "categories", new SaveCategoryRequest(Name: "Yabancı"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create(foreign.Id)).StatusCode);
    }

    // ---- Mükerrer uyarısı

    [Fact]
    public async Task Duplicate_warnings_find_similar_open_tickets_of_the_same_applicant_inside_the_callers_visibility()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);
        using var lead = await LoginAsync(s.Lead);
        using var viewer = await LoginAsync(s.Viewer);
        using var noAccess = await LoginAsync(s.NoAccess);
        using var foreign = await LoginAsync(other.Lead);

        var original = await TakeAsync(agent, await TicketAsync(agent, s, "Kargo teslim edilmedi"));
        var anonymous = await CreateAsync<TicketDto>(agent, "tickets", new SaveTicketRequest(Subject: "Fatura tutarı hatalı", ReporterName: "Ali", ReporterEmail: "ali@musteri.example.test", ReporterPhone: "05320000000"));
        await TakeAsync(agent, anonymous);

        async Task<List<TicketDuplicateDto>> Find(HttpClient client, string query) => (await client.GetFromJsonAsync<List<TicketDuplicateDto>>($"/api/support/tickets/duplicates?{query}"))!;

        var found = await Find(lead, $"partyId={s.PartyId}&subject=Kargo%20teslim%20edilmedi%20h%C3%A2l%C3%A2");
        var candidate = Assert.Single(found);
        Assert.Equal((original.Id, "Aynı müşteri"), (candidate.Id, candidate.Reason));
        Assert.True(candidate.Score >= 0.5);

        // Tanımsız başvuran e-posta ya da telefondan eşleşir; farklı konu, farklı müşteri ve kendi talebi (exclude) aday değildir.
        Assert.Single(await Find(lead, "reporterEmail=ali@musteri.example.test&subject=Fatura%20tutar%C4%B1%20hatal%C4%B1%20yine"));
        Assert.Single(await Find(lead, "reporterPhone=05320000000&subject=Fatura%20tutar%C4%B1%20hatal%C4%B1"));
        Assert.Empty(await Find(lead, $"partyId={s.PartyId}&subject=Tamamen%20ba%C5%9Fka%20bir%20konu"));
        Assert.Empty(await Find(lead, $"partyId={s.OtherPartyId}&subject=Kargo%20teslim%20edilmedi"));
        Assert.Empty(await Find(lead, $"partyId={s.PartyId}&subject=Kargo%20teslim%20edilmedi&excludeId={original.Id}"));

        // Kapanan talep aday olmaz; uyarı kayıt açmayı engellemez.
        var closed = await TakeAsync(agent, await TicketAsync(agent, s, "Sipariş iptali talebi"));
        await ResolveAsync(agent, await StartAsync(agent, closed));
        Assert.Single(await Find(lead, $"partyId={s.PartyId}&subject=Sipari%C5%9F%20iptali%20talebi"));
        await OkAsync(await ActAsync(lead, closed.Id, "close", new TicketActionRequest((await GetAsync(lead, closed.Id)).Version)));
        Assert.Empty(await Find(lead, $"partyId={s.PartyId}&subject=Sipari%C5%9F%20iptali%20talebi"));
        Assert.Equal(HttpStatusCode.Created, (await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Kargo teslim edilmedi", PartyId: s.PartyId))).StatusCode);

        // Görünürlük: kapsamı "kendi" olan başka temsilci başkasının sahipli talebini aday olarak göremez; izleyici görür; yetkisiz 403; başka tenant boş.
        Assert.DoesNotContain(await Find(agent2, $"partyId={s.PartyId}&subject=Kargo%20teslim%20edilmedi%20h%C3%A2l%C3%A2"), c => c.Id == original.Id);
        Assert.Contains(await Find(viewer, $"partyId={s.PartyId}&subject=Kargo%20teslim%20edilmedi"), c => c.Id == original.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync("/api/support/tickets/duplicates?reporterEmail=a@b.test")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lead.GetAsync("/api/support/tickets/duplicates?subject=Kargo")).StatusCode);
        Assert.Empty(await Find(foreign, $"partyId={s.PartyId}&subject=Kargo%20teslim%20edilmedi"));
    }

    // ---- Birleştirme

    [Fact]
    public async Task Merging_keeps_every_record_marks_the_source_final_and_refuses_unsafe_merges()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);
        using var viewer = await LoginAsync(s.Viewer);
        using var foreign = await LoginAsync(other.Lead);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await CreateAsync<SlaPolicyDto>(manager, "sla-policies", new SaveSlaPolicyRequest(Name: "Varsayılan", IsDefault: true, FirstResponseMinutes: 60, ResolutionMinutes: 480, CalendarId: always.Id));

        var target = await TakeAsync(agent, await TicketAsync(agent, s, "Ana talep"));
        var source = await TakeAsync(agent, await TicketAsync(agent, s, "Tekrar talep", clientKey: "form-777"));
        await agent.PostAsJsonAsync($"/api/support/tickets/{source.Id}/messages", new AddTicketMessageRequest("customer-message", "Hâlâ gelmedi."));
        await agent.PostAsJsonAsync($"/api/support/tickets/{source.Id}/messages", new AddTicketMessageRequest("note", "ÖZEL-NOT müşteri sinirli"));
        await agent.PostAsJsonAsync($"/api/support/tickets/{source.Id}/messages", new AddTicketMessageRequest("reply", "İnceliyoruz."));
        var originalMessages = await MessagesAsync(agent, source.Id);
        Assert.Equal(3, originalMessages.Count);
        Assert.NotEmpty((await agent.GetFromJsonAsync<List<SlaInstanceDto>>($"/api/support/tickets/{source.Id}/sla"))!);

        // Yetki, gerekçe, hedef ve kural denetimleri (hiçbiri yazmaz).
        var current = await GetAsync(lead, source.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version, "Mükerrer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version, "Mükerrer"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(Guid.NewGuid(), current.Version, "Mükerrer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(source.Id, current.Version, "Kendisi"))).StatusCode);
        var different = await TicketAsync(lead, s, "Başka müşteri", party: s.OtherPartyId);
        var differentResponse = await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(different.Id, current.Version, "Yanlış"));
        Assert.Equal(HttpStatusCode.Conflict, differentResponse.StatusCode);
        Assert.Contains("Farklı müşterilerin", await differentResponse.Content.ReadAsStringAsync());
        var closedTarget = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Kapanacak"))));
        await OkAsync(await ActAsync(lead, closedTarget.Id, "close", new TicketActionRequest((await GetAsync(lead, closedTarget.Id)).Version)));
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(closedTarget.Id, current.Version, "Kapalı"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version, "Kapsam dışı"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version, "Sızma"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version - 1, "Eski sürüm"))).StatusCode);
        Assert.Equal("Assigned", (await GetAsync(lead, source.Id)).Status);

        // Birleştirme: kaynak son durum, hedefe işaret eder; geçmiş, mesajlar, dış anahtar ve SLA kaydı kaynakta kalır.
        var merged = await OkAsync(await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, current.Version, "Aynı kargo")));
        Assert.Equal(("Merged", target.Id), (merged.Status, merged.MergedIntoId));
        Assert.Equal(3, (await MessagesAsync(lead, source.Id)).Count);
        Assert.Equal("form-777", (await GetAsync(lead, source.Id)).ClientKey);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Aynı form", PartyId: s.PartyId, ClientKey: "form-777"))).StatusCode);
        // Kaynağın açık sayacı iptal edilir; zaten tamamlanmış (yanıtlanmış) ilk yanıt örneği geçmiş olarak olduğu gibi kalır.
        var sourceSla = (await agent.GetFromJsonAsync<List<SlaInstanceDto>>($"/api/support/tickets/{source.Id}/sla"))!;
        Assert.Equal("Cancelled", sourceSla.Single(i => i.Metric == "Resolution").State);
        Assert.Equal("Met", sourceSla.Single(i => i.Metric == "FirstResponse").State);
        Assert.All(await agent.GetFromJsonAsync<List<SlaInstanceDto>>($"/api/support/tickets/{target.Id}/sla") ?? [], i => Assert.Equal("Running", i.State));
        Assert.Contains(await HistoryAsync(lead, source.Id), h => h.Kind == "MergedInto" && h.Reason!.Contains("Aynı kargo"));

        // Hedefte mesajlar özgün zamanlarıyla ve kaynak işaretiyle görünür; iç not görünürlüğü korunur; izleyici iç notu görmez.
        var copied = (await MessagesAsync(agent, target.Id)).Where(m => m.SourceTicketId == source.Id).OrderBy(m => m.CreatedAt).ToList();
        Assert.Equal(originalMessages.Select(m => m.Body), copied.Select(m => m.Body));
        Assert.Equal(originalMessages.Select(m => m.CreatedAt), copied.Select(m => m.CreatedAt));
        Assert.Equal(originalMessages.Select(m => m.Visibility), copied.Select(m => m.Visibility));
        Assert.DoesNotContain(await MessagesAsync(viewer, target.Id), m => m.Body.Contains("ÖZEL-NOT"));
        Assert.Contains(await HistoryAsync(lead, target.Id), h => h.Kind == "MergeReceived" && h.Reason!.Contains(source.Number));

        // Birleştirilen talep son durumdadır: ne atanır, ne çözülür, ne yeniden açılır, ne mesaj alır; açık listelerde yer almaz; ikinci birleştirme olmaz.
        var final = await GetAsync(lead, source.Id);
        foreach (var action in new[] { "take", "assign", "start", "resolve", "close", "reopen", "cancel", "set-priority", "wait-customer" })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(lead, source.Id, action, new TicketActionRequest(final.Version, "x", OwnerId: s.Agent.Id, Priority: "High", ResolutionCode: "Resolved", ResolutionNote: "x"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/messages", new AddTicketMessageRequest("note", "Geç"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{source.Id}/merge", new MergeTicketRequest(target.Id, final.Version, "Tekrar"))).StatusCode);
        var open = (await lead.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets?open=true&pageSize=50"))!.Items;
        Assert.DoesNotContain(open, t => t.Id == source.Id);
        Assert.Equal(source.Id, Assert.Single((await lead.GetFromJsonAsync<PagedResult<TicketDto>>($"/api/support/tickets?mergedIntoId={target.Id}"))!.Items).Id);
    }

    // ---- Alt işler

    [Fact]
    public async Task Blocking_tasks_stop_resolution_until_they_are_done_and_tasks_follow_permissions_and_visibility()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);
        using var viewer = await LoginAsync(s.Viewer);
        using var foreign = await LoginAsync(other.Lead);

        var ticket = await StartAsync(agent, await TakeAsync(agent, await TicketAsync(agent, s, "Alt işli")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("Görev", other.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest(new string('x', 201)))).StatusCode);

        var added = await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("Kargo firmasından konşimento iste", s.Agent.Id, IsBlocking: true));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var blocking = (await added.Content.ReadFromJsonAsync<List<TicketTaskDto>>())!.Single();
        await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("Müşteriyi bilgilendir"));

        // Engelleyici açık alt iş varken çözme reddedilir (sunucuda) ve eylem listesi nedeni söyler; engelleyici olmayan iş engellemez.
        var resolve = await ActAsync(agent, ticket.Id, "resolve", new TicketActionRequest((await GetAsync(agent, ticket.Id)).Version, ResolutionCode: "Resolved", ResolutionNote: "Bitti"));
        Assert.Equal(HttpStatusCode.Conflict, resolve.StatusCode);
        Assert.Contains("engelleyici alt iş", await resolve.Content.ReadAsStringAsync());
        var actions = (await agent.GetFromJsonAsync<List<TicketActionDto>>($"/api/support/tickets/{ticket.Id}/actions"))!;
        Assert.Contains(actions, a => a.Action == "resolve" && !a.Allowed && a.Code == "blocking-tasks");

        // Yetki ve görünürlük: izleyici okur ama yazamaz; başka temsilci (kapsam dışı) ve başka tenant 404; olmayan alt iş 404.
        Assert.Equal(2, (await viewer.GetFromJsonAsync<List<TicketTaskDto>>($"/api/support/tickets/{ticket.Id}/tasks"))!.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("İzinsiz"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{blocking.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{ticket.Id}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{blocking.Id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{ticket.Id}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("Sızma"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{Guid.NewGuid()}/complete", null)).StatusCode);

        // Tamamlayınca çözme geçer; tamamlanan iş tekrar tamamlanamaz; çözülmüş talepte yeniden açılamaz; tarihçeye düşer.
        var done = await agent.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{blocking.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var finished = (await done.Content.ReadFromJsonAsync<List<TicketTaskDto>>())!.Single(t => t.Id == blocking.Id);
        Assert.Equal((true, s.Agent.Id), (finished.IsDone, finished.DoneBy));
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{blocking.Id}/complete", null)).StatusCode);
        var resolved = await ResolveAsync(agent, ticket);
        Assert.Equal("Resolved", resolved.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsync($"/api/support/tickets/{ticket.Id}/tasks/{blocking.Id}/reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/tasks", new AddTicketTaskRequest("Çözülmüşe"))).StatusCode);
        var kinds = (await HistoryAsync(agent, ticket.Id)).Select(h => h.Kind).ToList();
        Assert.Equal(2, kinds.Count(k => k == "TaskAdded"));
        Assert.Contains("TaskCompleted", kinds);
    }

    // ---- Müşteri teyidi ve zaman aşımıyla kapanma

    [Fact]
    public async Task Customer_confirmation_closes_a_resolved_ticket_and_auto_close_closes_stale_ones_exactly_once()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);

        // Teyit yalnızca çözülmüş talepte kaydedilir ve hemen kapatır.
        var confirmed = await StartAsync(agent, await TakeAsync(agent, await TicketAsync(agent, s, "Teyitli")));
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync($"/api/support/tickets/{confirmed.Id}/messages", new AddTicketMessageRequest("customer-confirmation", "Tamam"))).StatusCode);
        await ResolveAsync(agent, confirmed);
        Assert.Equal(HttpStatusCode.Created, (await agent.PostAsJsonAsync($"/api/support/tickets/{confirmed.Id}/messages", new AddTicketMessageRequest("customer-confirmation", "Çözüm yeterli, teşekkürler."))).StatusCode);
        var closed = await GetAsync(agent, confirmed.Id);
        Assert.Equal("Closed", closed.Status);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal("Resolved", closed.ResolutionCode);
        Assert.Contains(await HistoryAsync(agent, confirmed.Id), h => h.Kind == "CustomerConfirmed");

        // Zaman aşımı varsayılan olarak kapalıdır: eski çözülmüş talep dokunulmadan kalır.
        var stale = await ResolveAsync(agent, await StartAsync(agent, await TakeAsync(agent, await TicketAsync(agent, s, "Eski çözüm"))));
        var recent = await ResolveAsync(agent, await StartAsync(agent, await TakeAsync(agent, await TicketAsync(agent, s, "Yeni çözüm"))));
        var objected = await ResolveAsync(agent, await StartAsync(agent, await TakeAsync(agent, await TicketAsync(agent, s, "İtirazlı"))));
        await SqlAsync(s.Tenant.Id, "UPDATE support.Tickets SET ResolvedAt = DATEADD(day, -10, SYSDATETIMEOFFSET()) WHERE Id IN ({0}, {1})", stale.Id, objected.Id);
        Assert.Equal(new SlaSchedulerRun(0, 0), await RunSchedulerAsync(s.Tenant.Id));
        Assert.Equal("Resolved", (await GetAsync(agent, stale.Id)).Status);

        // Müşteri itirazı (mesaj) çözülmüş talebi yeniden açar; zaman aşımı yalnızca itirazsız talebi kapatır.
        await agent.PostAsJsonAsync($"/api/support/tickets/{objected.Id}/messages", new AddTicketMessageRequest("customer-message", "Sorun devam ediyor."));
        await SetSettingAsync(manager, "support.auto-close-days", "7");
        var run = await RunSchedulerAsync(s.Tenant.Id);
        Assert.Equal(1, run.AutoClosed);
        var autoClosed = await GetAsync(lead, stale.Id);
        Assert.Equal(("Closed", "Resolved"), (autoClosed.Status, autoClosed.ResolutionCode));
        Assert.NotNull(autoClosed.ClosedAt);
        Assert.Equal("Resolved", (await GetAsync(lead, recent.Id)).Status);
        Assert.Equal("Reopened", (await GetAsync(lead, objected.Id)).Status);
        var history = await HistoryAsync(lead, stale.Id);
        Assert.Contains(history, h => h.Kind == "AutoClosed" && h.Reason!.Contains("7 gün"));

        // Tekrar ve paralel çalıştırma kapanışı çoğaltmaz.
        Assert.Equal(0, (await RunSchedulerAsync(s.Tenant.Id)).AutoClosed);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => factory.Services.GetRequiredService<SupportJobsRunner>().RunTenantAsync(s.Tenant.Id, CancellationToken.None)));
        Assert.All(parallel, r => Assert.Equal(0, r.Run?.AutoClosed ?? 0));
        Assert.Equal(1, (await HistoryAsync(lead, stale.Id)).Count(h => h.Kind == "AutoClosed"));

        // Kapalı talep yeniden açma penceresi içinde gerekçeyle açılabilir (kapatma geri alınabilir).
        Assert.Equal("Reopened", (await OkAsync(await ActAsync(lead, stale.Id, "reopen", new TicketActionRequest(autoClosed.Version, "Müşteri döndü")))).Status);
    }
}
