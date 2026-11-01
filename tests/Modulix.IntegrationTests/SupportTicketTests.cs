using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using Modulix.Shared.Support;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Crm = Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 1: destek talebi. CRM kapalıyken müşteri kimliğiyle talep açma/atama/çözme, doğrulama, etki × aciliyet önceliği ve gerekçeli elle
/// değişiklik, kategori/kuyruk yönlendirmesi, durum makinesinin sunucuda uygulanması (genel güncellemeyle atlanamaz), yetki ve veri kapsamı, iç not
/// ile müşteriye görünen yanıtın ayrımı, tenant izolasyonu, eşzamanlı üzerine alma ve tekrar koruması.
/// </summary>
public class SupportTicketTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];

    private static readonly string[] LeadPermissions =
    [
        .. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel, PriorityOverride,
    ];

    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage];

    private sealed record Setup(Tenant Tenant, Guid PartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Agent, ModulixUser Agent2, ModulixUser Viewer, ModulixUser NoAccess);

    /// <summary>Yalnızca destek modülü açık (CRM kapalı) tenant: müşteri ortak kayıttır, talep CRM'e bağlı değildir.</summary>
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

        Guid partyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Acme Lojistik A.Ş.", Email = "musteri@acme.example.test" };
            db.Set<Party>().Add(party);
            await db.SaveChangesAsync();
            partyId = party.Id;
        }

        return new Setup(tenant, partyId, manager, lead, agent, agent2, viewer, noAccess);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<TicketDto> CreateAsync(HttpClient client, SaveTicketRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/support/tickets", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static Task<TicketDto> CreateForAsync(HttpClient client, Setup s, string subject = "Teslimat gecikti", string? impact = null, string? urgency = null, Guid? categoryId = null, string? typeKey = null) =>
        CreateAsync(client, new SaveTicketRequest(Subject: subject, PartyId: s.PartyId, Impact: impact, Urgency: urgency, CategoryId: categoryId, TypeKey: typeKey));

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid id, string action, TicketActionRequest request) =>
        client.PostAsJsonAsync($"/api/support/tickets/{id}/{action}", request);

    private static async Task<TicketDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> TakeAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "take", new TicketActionRequest(t.Version)));

    private static async Task<TicketDto> ResolveAsync(HttpClient client, TicketDto t, string? rootCause = null, string? corrective = null) =>
        await OkAsync(await ActAsync(client, t.Id, "resolve", new TicketActionRequest(t.Version, ResolutionCode: "Resolved", ResolutionNote: "Sorun giderildi.", RootCause: rootCause, CorrectiveAction: corrective)));

    private static async Task<List<TicketMessageDto>> MessagesAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketMessageDto>>($"/api/support/tickets/{id}/messages"))!;

    private static async Task<TicketMessageDto> AddMessageAsync(HttpClient client, Guid id, string kind, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/support/tickets/{id}/messages", new AddTicketMessageRequest(kind, body));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketMessageDto>())!;
    }

    private static async Task<T> CreateRecordAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static SaveTicketRequest SaveFrom(TicketDto t) => new(
        t.Version, t.Subject, t.Description, t.TypeKey, t.Channel, t.PartyId, t.PersonId, t.ReporterName, t.ReporterEmail, t.ReporterPhone,
        t.CategoryId, t.Impact, t.Urgency, EventDate: t.EventDate, ExpectedOutcome: t.ExpectedOutcome);

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    private static async Task<int> CountAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<PagedResult<TicketDto>>($"/api/support/tickets?pageSize=1{query}"))!.TotalCount;

    // ---- Mutlu yol: CRM kapalıyken müşteri kimliğiyle talep açılır, atanır, yanıtlanır, çözülür ve kapanır

    [Fact]
    public async Task Ticket_is_created_assigned_answered_resolved_and_closed_without_the_crm_module()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);

        var created = await CreateForAsync(agent, s);
        Assert.Matches("^TK-\\d{8}-[A-HJ-NP-Z2-9]{6}$", created.Number);
        Assert.Equal(("New", "Normal", "Support", "User"), (created.Status, created.Priority, created.TypeKey, created.Channel));
        Assert.Equal(s.PartyId, created.PartyId);
        Assert.Null(created.OwnerId);
        Assert.Null(created.FirstResponseAt);

        // Atanmamış talep kuyrukta görünür; gösterge listeyle aynı süzgeçten gelir.
        var summary = (await agent.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!;
        Assert.Equal((1, 1, 1, 0), (summary.Open, summary.Unassigned, summary.AwaitingFirstResponse, summary.Mine));
        Assert.Equal(summary.Unassigned, await CountAsync(agent, "&unassigned=true"));

        var taken = await TakeAsync(agent, created);
        Assert.Equal(("Assigned", s.Agent.Id), (taken.Status, taken.OwnerId));
        Assert.Equal(1, (await agent.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!.Mine);

        var started = await OkAsync(await ActAsync(agent, taken.Id, "start", new TicketActionRequest(taken.Version)));
        Assert.Equal("InProgress", started.Status);

        // İç not ilk yanıt sayılmaz; müşteriye görünen yanıt sayılır ve e-posta kuyruğa alınır (müşterinin e-postası var).
        await AddMessageAsync(agent, created.Id, "note", "Müşteri üçüncü kez arıyor.");
        Assert.Null((await GetAsync(agent, created.Id)).FirstResponseAt);
        var reply = await AddMessageAsync(agent, created.Id, "reply", "Talebinizi aldık, inceliyoruz.");
        Assert.Equal(("Public", "Outbound", "Queued"), (reply.Visibility, reply.Direction, reply.DeliveryStatus));
        Assert.NotNull((await GetAsync(agent, created.Id)).FirstResponseAt);
        Assert.Equal(0, (await agent.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!.AwaitingFirstResponse);

        var current = await GetAsync(agent, created.Id);
        var resolved = await ResolveAsync(agent, current);
        Assert.Equal(("Resolved", "Resolved"), (resolved.Status, resolved.ResolutionCode));
        Assert.Equal(s.Agent.Id, resolved.ResolvedBy);
        Assert.NotNull(resolved.ResolvedAt);

        // Çözüldü ile kapandı aynı şey değildir: kapatma ayrı yetki ve ayrı eylemdir.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(agent, resolved.Id, "close", new TicketActionRequest(resolved.Version))).StatusCode);
        var closed = await OkAsync(await ActAsync(lead, resolved.Id, "close", new TicketActionRequest(resolved.Version)));
        Assert.Equal("Closed", closed.Status);
        Assert.NotNull(closed.ClosedAt);

        var history = (await agent.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{created.Id}/history"))!;
        Assert.Equal(["Created", "Taken", "Started", "InternalNote", "PublicReply", "Resolved", "Closed"], history.Select(h => h.Kind));
        Assert.Equal(("New", "Assigned"), (history[1].FromStatus, history[1].ToStatus));

        // Kapanmış talebe mesaj eklenemez.
        var late = await agent.PostAsJsonAsync($"/api/support/tickets/{created.Id}/messages", new AddTicketMessageRequest("note", "Geç not"));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        // CRM tablosu olmadan müşteri kopyalanmadı: aynı ortak taraf kaydı.
        Assert.Equal(1, await factory.ScalarAsync<int>(s.Tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", s.Tenant.Id)));
    }

    // ---- Hata / doğrulama

    [Fact]
    public async Task Creation_validates_the_applicant_type_and_fields_and_unidentified_customers_are_linked_later()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);

        async Task<HttpStatusCode> StatusOf(SaveTicketRequest r) => (await agent.PostAsJsonAsync("/api/support/tickets", r)).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: "Konu yok", ReporterName: null)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(PartyId: s.PartyId)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: "Tür", PartyId: s.PartyId, TypeKey: "Olmayan")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: "Etki", PartyId: s.PartyId, Impact: "Huge")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: "Kanal", PartyId: s.PartyId, Channel: "Fax")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: "Müşteri", PartyId: Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusOf(new SaveTicketRequest(Subject: new string('x', 201), PartyId: s.PartyId)));
        Assert.Equal(0, await CountAsync(agent));

        // Müşterisi tanımsız başvuru müşteri kaydı açmadan kaydedilir; sonradan mevcut firmaya bağlanır ve tarihçeye düşer.
        var partiesBefore = await factory.ScalarAsync<int>(s.Tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", s.Tenant.Id));
        var anonymous = await CreateAsync(agent, new SaveTicketRequest(Subject: "Telefonla aradı", ReporterName: "Ali Veli", ReporterPhone: "05320000000", Channel: "Phone"));
        Assert.Null(anonymous.PartyId);
        Assert.Equal("Phone", anonymous.Channel);
        Assert.Equal(partiesBefore, await factory.ScalarAsync<int>(s.Tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", s.Tenant.Id)));

        var linked = await agent.PutAsJsonAsync($"/api/support/tickets/{anonymous.Id}", SaveFrom(anonymous) with { PartyId = s.PartyId });
        Assert.Equal(s.PartyId, (await OkAsync(linked)).PartyId);
        var history = (await agent.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{anonymous.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "CustomerLinked");
        Assert.Equal(partiesBefore, await factory.ScalarAsync<int>(s.Tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", s.Tenant.Id)));
    }

    [Fact]
    public async Task Priority_follows_impact_and_urgency_and_a_manual_change_needs_permission_and_a_reason()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);

        var created = await CreateForAsync(agent, s, impact: "High", urgency: "High");
        Assert.Equal(("Critical", false), (created.Priority, created.PriorityOverridden));

        // Etki/aciliyet değişince öncelik yeniden hesaplanır.
        var lowered = await OkAsync(await agent.PutAsJsonAsync($"/api/support/tickets/{created.Id}", SaveFrom(created) with { Impact = "Low", Urgency = "Low" }));
        Assert.Equal("Low", lowered.Priority);

        // Öncelik genel güncellemeyle yazılamaz; elle değişiklik ayrı yetki ve gerekçe ister.
        var snapshot = await GetAsync(agent, created.Id);
        var viaPut = await agent.PutAsJsonAsync($"/api/support/tickets/{created.Id}", SaveFrom(snapshot) with { Version = snapshot.Version });
        Assert.Equal("Low", (await OkAsync(viaPut)).Priority);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(agent, created.Id, "set-priority", new TicketActionRequest(snapshot.Version + 1, "Müşteri VIP", Priority: "High"))).StatusCode);

        var current = await GetAsync(lead, created.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, created.Id, "set-priority", new TicketActionRequest(current.Version, Priority: "High"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, created.Id, "set-priority", new TicketActionRequest(current.Version, "Gerekçe", Priority: "Urgent"))).StatusCode);
        var overridden = await OkAsync(await ActAsync(lead, created.Id, "set-priority", new TicketActionRequest(current.Version, "Müşteri VIP", Priority: "High")));
        Assert.Equal(("High", true, "Müşteri VIP"), (overridden.Priority, overridden.PriorityOverridden, overridden.PriorityReason));

        // Elle değiştirilen öncelik sonraki etki/aciliyet düzenlemesiyle sessizce ezilmez.
        var edited = await OkAsync(await agent.PutAsJsonAsync($"/api/support/tickets/{created.Id}", SaveFrom(overridden) with { Impact = "Medium", Urgency = "Medium" }));
        Assert.Equal("High", edited.Priority);
        var history = (await lead.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{created.Id}/history"))!;
        Assert.Equal("Müşteri VIP", history.Single(h => h.Kind == "PriorityChanged").Reason);
    }

    [Fact]
    public async Task Routing_uses_the_category_queue_then_the_parent_category_then_the_default_queue_deterministically()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);

        // Kuyruk yönetimi ayrı izinlidir.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/support/queues", new SaveQueueRequest(Name: "Yetkisiz"))).StatusCode);

        // Kuyruk tanımı yokken talep kuyruksuz (atanmamış) kalır; kaybolmaz.
        var unrouted = await CreateForAsync(manager, s, "Kuyruksuz");
        Assert.Null(unrouted.QueueId);

        var general = await CreateRecordAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Genel", IsDefault: true));
        var billing = await CreateRecordAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Faturalama"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/support/queues", new SaveQueueRequest(Name: "İkinci varsayılan", IsDefault: true))).StatusCode);

        var invoice = await CreateRecordAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Fatura", DefaultQueueId: billing.Id));
        var refund = await CreateRecordAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "İade", ParentId: invoice.Id));
        var other = await CreateRecordAsync<CategoryDto>(manager, "categories", new SaveCategoryRequest(Name: "Diğer"));

        // En çok iki düzey.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/support/categories", new SaveCategoryRequest(Name: "Çok derin", ParentId: refund.Id))).StatusCode);

        Assert.Equal(billing.Id, (await CreateForAsync(agent, s, "Alt kategori", categoryId: refund.Id)).QueueId);
        Assert.Equal(billing.Id, (await CreateForAsync(agent, s, "Ana kategori", categoryId: invoice.Id)).QueueId);
        Assert.Equal(general.Id, (await CreateForAsync(agent, s, "Kuyruksuz kategori", categoryId: other.Id)).QueueId);
        Assert.Equal(general.Id, (await CreateForAsync(agent, s, "Kategorisiz")).QueueId);

        // Pasif kuyruk atlanır; pasif kategoriye yeni talep açılamaz.
        var billingNow = (await manager.GetFromJsonAsync<QueueDto>($"/api/support/queues/{billing.Id}"))!;
        await OkRecordAsync(await manager.PutAsJsonAsync($"/api/support/queues/{billing.Id}", new SaveQueueRequest(billingNow.Version, billingNow.Name, billingNow.Description, false, false)));
        Assert.Equal(general.Id, (await CreateForAsync(agent, s, "Pasif kuyruk", categoryId: invoice.Id)).QueueId);
        var otherNow = (await manager.GetFromJsonAsync<CategoryDto>($"/api/support/categories/{other.Id}"))!;
        await OkRecordAsync(await manager.PutAsJsonAsync($"/api/support/categories/{other.Id}", new SaveCategoryRequest(otherNow.Version, otherNow.Name, null, null, false, 0)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Pasif kategori", PartyId: s.PartyId, CategoryId: other.Id))).StatusCode);

        // Kullanılan kuyruk ve kategori silinemez.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/support/queues/{general.Id}?version={general.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/support/categories/{invoice.Id}?version={invoice.Version}")).StatusCode);
    }

    private static async Task OkRecordAsync(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Assignment_take_and_transfer_follow_permissions_reasons_and_queue_rules()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);

        var general = await CreateRecordAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Genel", IsDefault: true));
        var billing = await CreateRecordAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Faturalama"));
        var ticket = await CreateForAsync(agent, s);

        // Yetkisiz atama ve sorumluyla oluşturma reddedilir; sorumlu yalnızca etkin üye olabilir.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(agent, ticket.Id, "assign", new TicketActionRequest(ticket.Version, OwnerId: s.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Atamalı", PartyId: s.PartyId, OwnerId: s.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "assign", new TicketActionRequest(ticket.Version, OwnerId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "assign", new TicketActionRequest(ticket.Version))).StatusCode);

        var assigned = await OkAsync(await ActAsync(lead, ticket.Id, "assign", new TicketActionRequest(ticket.Version, OwnerId: s.Agent.Id)));
        Assert.Equal(("Assigned", s.Agent.Id), (assigned.Status, assigned.OwnerId));

        // Sorumlusu olan talep başkasına gerekçesiz atanamaz; üzerine alma sahipli talepte reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "assign", new TicketActionRequest(assigned.Version, OwnerId: s.Agent2.Id))).StatusCode);
        var asLead = await GetAsync(lead, ticket.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(lead, ticket.Id, "take", new TicketActionRequest(asLead.Version))).StatusCode);

        // Ekipler arası devir: gerekçe ve kuyruk zorunlu; sorumlu sıfırlanır, talep yeniden sınıflandırma bekler.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "transfer", new TicketActionRequest(asLead.Version, QueueId: billing.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "transfer", new TicketActionRequest(asLead.Version, "Fatura konusu", QueueId: Guid.NewGuid()))).StatusCode);
        var transferred = await OkAsync(await ActAsync(lead, ticket.Id, "transfer", new TicketActionRequest(asLead.Version, "Fatura konusu", QueueId: billing.Id)));
        Assert.Equal((billing.Id, "Triage"), (transferred.QueueId, transferred.Status));
        Assert.Null(transferred.OwnerId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "transfer", new TicketActionRequest(transferred.Version, "Aynı kuyruk", QueueId: billing.Id))).StatusCode);

        // Eski sahip artık görmez ama başka bir temsilci atanmamış talebi kuyrukta görüp üzerine alabilir.
        var mine = await TakeAsync(agent2, await GetAsync(agent2, ticket.Id));
        Assert.Equal(s.Agent2.Id, mine.OwnerId);
        var history = (await lead.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{ticket.Id}/history"))!;
        var transfer = history.Single(h => h.Kind == "Transferred");
        Assert.Equal(("Fatura konusu", general.Id, billing.Id), (transfer.Reason, transfer.FromQueueId, transfer.ToQueueId));
        Assert.Equal((s.Agent.Id, (Guid?)null), (transfer.FromOwnerId, transfer.ToOwnerId));
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/support/tickets/{ticket.Id}")).StatusCode);
    }

    // ---- Durum makinesi sunucuda uygulanır

    [Fact]
    public async Task The_state_machine_is_enforced_on_the_server_and_generic_updates_cannot_bypass_it()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);

        var ticket = await CreateForAsync(agent, s);

        // Desteklenmeyen geçişler 409; eksik gerekçe/çözüm alanı 422.
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(agent, ticket.Id, "resolve", new TicketActionRequest(ticket.Version, ResolutionCode: "Resolved", ResolutionNote: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(agent, ticket.Id, "start", new TicketActionRequest(ticket.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(lead, ticket.Id, "close", new TicketActionRequest(ticket.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, ticket.Id, "cancel", new TicketActionRequest(ticket.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(lead, Guid.NewGuid(), "cancel", new TicketActionRequest(1, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(lead, ticket.Id, "bilinmeyen", new TicketActionRequest(1))).StatusCode);

        // Genel güncelleme durum, sorumlu, kuyruk, öncelik ve çözüm alanlarını değiştiremez (istekte olsa bile yok sayılır).
        var raw = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = ticket.Version, ["subject"] = "Yeni konu", ["partyId"] = s.PartyId.ToString(), ["status"] = "Closed", ["ownerId"] = s.Lead.Id.ToString(),
            ["priority"] = "Critical", ["resolutionCode"] = "Resolved", ["resolutionNote"] = "Hile", ["queueId"] = Guid.NewGuid().ToString(), ["firstResponseAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        var put = await agent.PutAsJsonAsync($"/api/support/tickets/{ticket.Id}", raw);
        var after = await OkAsync(put);
        Assert.Equal(("Yeni konu", "New", "Normal"), (after.Subject, after.Status, after.Priority));
        Assert.Null(after.OwnerId);
        Assert.Null(after.ResolutionCode);
        Assert.Null(after.FirstResponseAt);

        // Silme yoktur; geçmiş korunur.
        Assert.Equal(HttpStatusCode.Conflict, (await agent.DeleteAsync($"/api/support/tickets/{ticket.Id}?version={ticket.Version}")).StatusCode);

        // Bekleme: gerekçe ister, müşteri yanıtı talebi işleme döndürür.
        var taken = await TakeAsync(agent, await GetAsync(agent, ticket.Id));
        var started = await OkAsync(await ActAsync(agent, taken.Id, "start", new TicketActionRequest(taken.Version)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(agent, started.Id, "wait-customer", new TicketActionRequest(started.Version))).StatusCode);
        var waiting = await OkAsync(await ActAsync(agent, started.Id, "wait-customer", new TicketActionRequest(started.Version, "Fatura numarası bekleniyor")));
        Assert.Equal(("WaitingCustomer", "Fatura numarası bekleniyor"), (waiting.Status, waiting.StatusReason));
        await AddMessageAsync(agent, ticket.Id, "customer-message", "Fatura no: 12345");
        Assert.Equal(("InProgress", null), ((await GetAsync(agent, ticket.Id)) is var back ? (back.Status, back.StatusReason) : default));

        // İç ekip beklemesi ayrı durumdur; sürdürme işleme döner.
        back = await GetAsync(agent, ticket.Id);
        var internalWait = await OkAsync(await ActAsync(agent, back.Id, "wait-internal", new TicketActionRequest(back.Version, "Tedarikçi dönüşü")));
        Assert.Equal("WaitingInternal", internalWait.Status);
        var resumed = await OkAsync(await ActAsync(agent, back.Id, "resume", new TicketActionRequest(internalWait.Version)));
        Assert.Equal("InProgress", resumed.Status);

        // Çözüm kodu ve açıklaması zorunlu; listede olmayan kod reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(agent, resumed.Id, "resolve", new TicketActionRequest(resumed.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(agent, resumed.Id, "resolve", new TicketActionRequest(resumed.Version, ResolutionCode: "Uydurma", ResolutionNote: "x"))).StatusCode);
        var resolved = await ResolveAsync(agent, resumed);

        // İptal edilen/kapanan talep değişmez: çözülmüşse düzenleme ve iptal kapalıdır.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PutAsJsonAsync($"/api/support/tickets/{ticket.Id}", SaveFrom(resolved) with { Subject = "Çözüldükten sonra" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActAsync(lead, ticket.Id, "cancel", new TicketActionRequest(resolved.Version, "Vazgeçildi"))).StatusCode);
    }

    [Fact]
    public async Task Complaints_need_a_root_cause_and_reopening_keeps_the_previous_resolution_inside_the_window()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);

        // Şikâyet türünde çözüm için kök neden ve düzeltici aksiyon zorunlu (varsayılan politika); bilgi talebinde değil.
        var complaint = await CreateForAsync(agent, s, "Hasarlı teslimat", typeKey: "Complaint");
        var inProgress = await OkAsync(await ActAsync(agent, (await TakeAsync(agent, complaint)).Id, "start", new TicketActionRequest((await GetAsync(agent, complaint.Id)).Version)));
        var rejected = await ActAsync(agent, complaint.Id, "resolve", new TicketActionRequest(inProgress.Version, ResolutionCode: "Resolved", ResolutionNote: "Telafi edildi."));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("rootCause", body);
        Assert.Contains("correctiveAction", body);
        var resolved = await ResolveAsync(agent, inProgress, "Paketleme standardı uygulanmadı", "Paketleme talimatı güncellendi");
        Assert.Equal("Paketleme standardı uygulanmadı", resolved.RootCause);

        var info = await CreateForAsync(agent, s, "Çalışma saatleri", typeKey: "Info");
        var infoOwned = await TakeAsync(agent, info);
        Assert.Equal("Resolved", (await ResolveAsync(agent, infoOwned)).ResolutionCode);

        // Çözülmüş talep gerekçeyle yeniden açılır; önceki çözüm kaydı yerinde kalır, sayaç artar.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(lead, complaint.Id, "reopen", new TicketActionRequest(resolved.Version))).StatusCode);
        var reopened = await OkAsync(await ActAsync(lead, complaint.Id, "reopen", new TicketActionRequest(resolved.Version, "Müşteri çözümü kabul etmedi")));
        Assert.Equal(("Reopened", 1, "Resolved"), (reopened.Status, reopened.ReopenCount, reopened.ResolutionCode));
        Assert.Equal("Müşteri çözümü kabul etmedi", reopened.StatusReason);

        var again = await ResolveAsync(agent, await OkAsync(await ActAsync(agent, complaint.Id, "start", new TicketActionRequest(reopened.Version))), "Kök neden 2", "Aksiyon 2");
        var closed = await OkAsync(await ActAsync(lead, again.Id, "close", new TicketActionRequest(again.Version)));

        // Yeniden açma penceresi içinde açılır, dışında reddedilir (ilişkili yeni talep açılır).
        var inside = await OkAsync(await ActAsync(lead, closed.Id, "reopen", new TicketActionRequest(closed.Version, "Pencere içinde")));
        Assert.Equal(2, inside.ReopenCount);

        var second = await ResolveAsync(agent, await OkAsync(await ActAsync(agent, inside.Id, "start", new TicketActionRequest(inside.Version))), "Kök", "Aksiyon");
        var closedAgain = await OkAsync(await ActAsync(lead, second.Id, "close", new TicketActionRequest(second.Version)));
        await SqlAsync(s.Tenant.Id, "UPDATE support.Tickets SET ClosedAt = {0} WHERE Id = {1}", DateTimeOffset.UtcNow.AddDays(-30), closedAgain.Id);
        var late = await ActAsync(lead, closedAgain.Id, "reopen", new TicketActionRequest((await GetAsync(lead, closedAgain.Id)).Version, "Çok geç"));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("ilişkili yeni talep", await late.Content.ReadAsStringAsync());

        var followUp = await CreateAsync(lead, new SaveTicketRequest(Subject: "Aynı sorun yeniden", PartyId: s.PartyId, ParentTicketId: closedAgain.Id));
        Assert.Equal(closedAgain.Id, followUp.ParentTicketId);
        Assert.Equal(1, await CountAsync(lead, $"&parentTicketId={closedAgain.Id}"));
    }

    [Fact]
    public async Task A_customer_message_on_a_resolved_ticket_reopens_it_and_closed_tickets_stay_closed()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);

        var owned = await TakeAsync(agent, await CreateForAsync(agent, s));
        var resolved = await ResolveAsync(agent, owned);
        await AddMessageAsync(agent, resolved.Id, "customer-message", "Sorun devam ediyor.");
        var reopened = await GetAsync(agent, resolved.Id);
        Assert.Equal(("Reopened", 1), (reopened.Status, reopened.ReopenCount));
        Assert.Equal("Resolved", reopened.ResolutionCode);

        var closed = await OkAsync(await ActAsync(lead, reopened.Id, "cancel", new TicketActionRequest(reopened.Version, "Mükerrer talep")));
        Assert.Equal("Cancelled", closed.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync($"/api/support/tickets/{closed.Id}/messages", new AddTicketMessageRequest("customer-message", "Merhaba"))).StatusCode);
    }

    // ---- İç not ile müşteriye görünen yanıtın ayrımı

    [Fact]
    public async Task Internal_notes_never_reach_users_without_the_internal_permission_and_replies_stay_separate()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var viewer = await LoginAsync(s.Viewer);
        using var lead = await LoginAsync(s.Lead);

        var ticket = await CreateForAsync(agent, s);
        // Tüm kayıt görünürlüğüne sahip izleyici, atanmamış talebi görür; temsilci üzerine alır.
        var owned = await TakeAsync(agent, ticket);

        const string secret = "ÖZEL-NOT: müşteri sinirli, bölge müdürü haberdar.";
        await AddMessageAsync(agent, ticket.Id, "note", secret);
        await AddMessageAsync(agent, ticket.Id, "reply", "Yanıtımız: incelemedeyiz.");
        await AddMessageAsync(agent, ticket.Id, "customer-message", "Ne zaman çözülecek?");

        var all = await MessagesAsync(agent, ticket.Id);
        Assert.Equal(["Internal", "Public", "Public"], all.Select(m => m.Visibility));
        Assert.Equal(["Note", "Outbound", "Inbound"], all.Select(m => m.Direction));

        // İç not okuma izni olmayan kullanıcı iç notu hiçbir yoldan göremez: mesajlar, tarihçe, talep kaydı, liste, arama.
        var seen = await MessagesAsync(viewer, ticket.Id);
        Assert.Equal(["Public", "Public"], seen.Select(m => m.Visibility));
        Assert.DoesNotContain(secret, string.Join('|', seen.Select(m => m.Body)));
        var history = (await viewer.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{ticket.Id}/history"))!;
        Assert.DoesNotContain(history, h => h.Kind == "InternalNote");
        Assert.Contains(history, h => h.Kind == "PublicReply");
        Assert.DoesNotContain(secret, await viewer.GetStringAsync($"/api/support/tickets/{ticket.Id}"));
        Assert.DoesNotContain(secret, await viewer.GetStringAsync("/api/support/tickets?search=ÖZEL"));
        Assert.Equal(0, await CountAsync(viewer, "&search=ÖZEL-NOT"));

        // İzleyici yazamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("note", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("reply", "x"))).StatusCode);

        // Geçersiz tür ve boş/çok uzun metin reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("internal-public", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("note", "   "))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("note", new string('a', 8001)))).StatusCode);

        // Veritabanı iç notun dış kanalda saklanmasını da reddeder (iç görünürlük yalnızca not yönüyle).
        await Assert.ThrowsAnyAsync<Exception>(() => SqlAsync(s.Tenant.Id,
            "UPDATE support.Messages SET Direction = 'Outbound' WHERE TicketId = {0} AND Visibility = 'Internal'", ticket.Id));

        // Gönderilemeyen yanıt (geçersiz e-posta) ilk insan yanıtı sayılmaz.
        var badEmail = await CreateAsync(lead, new SaveTicketRequest(Subject: "Bozuk e-posta", ReporterName: "Zeynep", ReporterEmail: "gecersiz-adres", Channel: "Email"));
        await TakeAsync(lead, badEmail);
        var failed = await AddMessageAsync(lead, badEmail.Id, "reply", "Merhaba");
        Assert.Equal("Failed", failed.DeliveryStatus);
        Assert.Null((await GetAsync(lead, badEmail.Id)).FirstResponseAt);
        Assert.Equal(owned.Id, ticket.Id);
    }

    // ---- Yetki ve veri kapsamı

    [Fact]
    public async Task Unauthorized_users_cannot_see_other_agents_tickets_or_run_actions_they_lack_permission_for()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);
        using var viewer = await LoginAsync(s.Viewer);
        using var noAccess = await LoginAsync(s.NoAccess);
        using var manager = await LoginAsync(s.Manager);

        var mine = await TakeAsync(agent, await CreateForAsync(agent, s, "Agent 1 talebi"));
        var unassigned = await CreateForAsync(agent2, s, "Atanmamış talep");

        // Kapsam "kendi": başka temsilcinin sahipli talebi 404; atanmamış talep kuyrukta görünür.
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{mine.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{mine.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.PostAsJsonAsync($"/api/support/tickets/{mine.Id}/messages", new AddTicketMessageRequest("note", "izinsiz"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(agent2, mine.Id, "start", new TicketActionRequest(mine.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.PutAsJsonAsync($"/api/support/tickets/{mine.Id}", SaveFrom(mine))).StatusCode);
        Assert.Equal(["Atanmamış talep"], (await agent2.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets"))!.Items.Select(t => t.Subject));
        Assert.Equal((1, 1), ((await agent2.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!.Open, (await agent2.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!.Unassigned));

        // Tümünü görebilen izleyici okur ama hiçbir eylem yapamaz.
        Assert.Equal(2, await CountAsync(viewer));
        var actions = (await viewer.GetFromJsonAsync<List<TicketActionDto>>($"/api/support/tickets/{mine.Id}/actions"))!;
        Assert.Empty(actions);
        foreach (var action in new[] { "start", "resolve", "close", "cancel", "take", "assign", "triage" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(viewer, mine.Id, action, new TicketActionRequest(mine.Version, "x"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "x", PartyId: s.PartyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/support/tickets/{mine.Id}", SaveFrom(mine))).StatusCode);

        // Temsilci atama/kapatma/iptal/öncelik yetkisine sahip değil; eylem listesi yalnızca yetkili olduklarını içerir.
        var agentActions = (await agent.GetFromJsonAsync<List<TicketActionDto>>($"/api/support/tickets/{mine.Id}/actions"))!;
        Assert.DoesNotContain(agentActions, a => a.Action is "assign" or "transfer" or "close" or "cancel" or "set-priority" or "reopen");
        Assert.Contains(agentActions, a => a.Action == "start" && a.Allowed);
        Assert.Contains(agentActions, a => a.Action == "take" && !a.Allowed && a.Code == "already-yours");

        // Modül hakkı olmayan ya da rolsüz kullanıcı hiçbir uca ulaşamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync("/api/support/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync("/api/support/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync($"/api/support/tickets/{mine.Id}")).StatusCode);

        // Yönetici tümünü görür.
        Assert.Equal(2, await CountAsync(manager));
        Assert.NotNull(unassigned);
    }

    // ---- Tenant izolasyonu

    [Fact]
    public async Task Another_tenant_cannot_read_change_or_link_tickets_customers_categories_or_queues()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var agentA = await LoginAsync(a.Agent);
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        using var agentB = await LoginAsync(b.Agent);

        var queueA = await CreateRecordAsync<QueueDto>(managerA, "queues", new SaveQueueRequest(Name: "A kuyruğu", IsDefault: true));
        var categoryA = await CreateRecordAsync<CategoryDto>(managerA, "categories", new SaveCategoryRequest(Name: "A kategorisi"));
        var ticketA = await TakeAsync(agentA, await CreateForAsync(agentA, a));
        await AddMessageAsync(agentA, ticketA.Id, "note", "A'nın iç notu");
        var versionBeforeAttacks = (await GetAsync(managerA, ticketA.Id)).Version;

        // Okuma, yazma, eylem, mesaj ve tarihçe: başka tenant'ın kayıt kimliği bulunamadı sayılır, hiçbir şey değişmez.
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/tickets/{ticketA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PutAsJsonAsync($"/api/support/tickets/{ticketA.Id}", SaveFrom(ticketA) with { Subject = "ele geçirildi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"/api/support/tickets/{ticketA.Id}?version={ticketA.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(managerB, ticketA.Id, "cancel", new TicketActionRequest(ticketA.Version, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActAsync(managerB, ticketA.Id, "assign", new TicketActionRequest(ticketA.Version, OwnerId: b.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/tickets/{ticketA.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/tickets/{ticketA.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/support/tickets/{ticketA.Id}/messages", new AddTicketMessageRequest("reply", "sızıntı"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/queues/{queueA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"/api/support/categories/{categoryA.Id}?version={categoryA.Version}")).StatusCode);
        Assert.Equal(0, await CountAsync(managerB));
        Assert.Equal(0, (await managerB.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!.Open);

        var unchanged = await GetAsync(managerA, ticketA.Id);
        Assert.Equal(("Teslimat gecikti", "Assigned", versionBeforeAttacks), (unchanged.Subject, unchanged.Status, unchanged.Version));
        Assert.Single(await MessagesAsync(managerA, ticketA.Id));

        // İlişkilendirme: başka tenant'ın müşteri, kategori, kuyruk, kişi ve ilişkili talep kimlikleriyle oluşturma/atama/devir reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Yabancı müşteri", PartyId: a.PartyId))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Yabancı kategori", PartyId: b.PartyId, CategoryId: categoryA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Yabancı kuyruk", PartyId: b.PartyId, QueueId: queueA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Yabancı kullanıcı", PartyId: b.PartyId, OwnerId: a.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Yabancı ilişkili talep", PartyId: b.PartyId, ParentTicketId: ticketA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/categories", new SaveCategoryRequest(Name: "Yabancı üst", ParentId: categoryA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/categories", new SaveCategoryRequest(Name: "Yabancı kuyruk", DefaultQueueId: queueA.Id))).StatusCode);

        var ownTicket = await CreateForAsync(agentB, b);
        var queueB = await CreateRecordAsync<QueueDto>(managerB, "queues", new SaveQueueRequest(Name: "B kuyruğu", IsDefault: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(managerB, ownTicket.Id, "transfer", new TicketActionRequest(ownTicket.Version, "x", QueueId: queueA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActAsync(managerB, ownTicket.Id, "assign", new TicketActionRequest(ownTicket.Version, OwnerId: a.Agent.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PutAsJsonAsync($"/api/support/tickets/{ownTicket.Id}", SaveFrom(ownTicket) with { PartyId = a.PartyId })).StatusCode);
        Assert.Equal(ownTicket.QueueId, (await GetAsync(managerB, ownTicket.Id)).QueueId);
        Assert.NotEqual(queueA.Id, queueB.Id);

        // Her tenant'ın numarası ve istemci anahtarı kendi kapsamındadır: aynı anahtar iki tenant'ta çakışmaz.
        await CreateAsync(managerA, new SaveTicketRequest(Subject: "Anahtarlı", PartyId: a.PartyId, ClientKey: "ortak-anahtar"));
        await CreateAsync(managerB, new SaveTicketRequest(Subject: "Anahtarlı", PartyId: b.PartyId, ClientKey: "ortak-anahtar"));
    }

    // ---- Eşzamanlılık ve tekrar koruması

    [Fact]
    public async Task Concurrent_takes_and_stale_versions_end_in_a_single_owner_and_clear_conflicts()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);

        var ticket = await CreateForAsync(agent, s);
        var results = await Task.WhenAll(
            ActAsync(agent, ticket.Id, "take", new TicketActionRequest(ticket.Version)),
            ActAsync(agent2, ticket.Id, "take", new TicketActionRequest(ticket.Version)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        // Kaybeden istek ya sürüm çakışması (409) ya da talep artık başkasının olduğu için kapsam dışı kaldığından 404 alır; hiçbir durumda ikinci sahip yazılmaz.
        Assert.True(results.Count(r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) == 1, string.Join(",", results.Select(r => r.StatusCode)));
        var winner = (await results.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<TicketDto>())!;
        var final = await factory.ScalarAsync<Guid>(s.Tenant.Id, "SELECT OwnerId FROM support.Tickets WHERE Id = @id", ("@id", ticket.Id));
        Assert.Equal(winner.OwnerId, final);

        // Eski sürümle yapılan eylem ve düzenleme anlaşılır bir çakışma (409) verir; hiçbir şey yazılmaz.
        var current = await factory.ScalarAsync<long>(s.Tenant.Id, "SELECT Version FROM support.Tickets WHERE Id = @id", ("@id", ticket.Id));
        Assert.True(current > ticket.Version);
        var stale = await (winner.OwnerId == s.Agent.Id ? agent : agent2).PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/start", new TicketActionRequest(ticket.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Assigned", await factory.ScalarAsync<string>(s.Tenant.Id, "SELECT Status FROM support.Tickets WHERE Id = @id", ("@id", ticket.Id)));
    }

    [Fact]
    public async Task Repeating_a_creation_request_with_the_same_client_key_does_not_create_a_second_ticket()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);

        var first = await CreateAsync(agent, new SaveTicketRequest(Subject: "Web formu", PartyId: s.PartyId, Channel: "Web", ClientKey: "form-123"));
        Assert.Equal("form-123", first.ClientKey);

        var repeat = await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Web formu", PartyId: s.PartyId, Channel: "Web", ClientKey: "form-123"));
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Contains(first.Number, await repeat.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountAsync(agent));

        // Eşzamanlı aynı istekler de tek talep üretir.
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Eşzamanlı", PartyId: s.PartyId, ClientKey: "form-parallel"))));
        Assert.Single(concurrent, r => r.StatusCode == HttpStatusCode.Created);
        Assert.DoesNotContain(concurrent, r => r.StatusCode is HttpStatusCode.InternalServerError);
        Assert.Equal(2, await CountAsync(agent));

        // Anahtarsız istekler birbirinden bağımsız taleplerdir.
        await CreateForAsync(agent, s, "Anahtarsız 1");
        await CreateForAsync(agent, s, "Anahtarsız 2");
        Assert.Equal(4, await CountAsync(agent));
    }

    [Fact]
    public async Task List_filters_sorting_and_search_work_on_the_server_and_reject_unknown_values()
    {
        var s = await SetUpAsync();
        using var agent = await LoginAsync(s.Agent);
        using var manager = await LoginAsync(s.Manager);

        var low = await CreateForAsync(manager, s, "Düşük öncelik", "Low", "Low");
        var critical = await CreateForAsync(manager, s, "Kritik sorun", "High", "High");
        var mid = await TakeAsync(agent, await CreateForAsync(agent, s, "Orta"));

        var byPriority = (await manager.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets?sort=priority"))!;
        Assert.Equal([critical.Id, mid.Id, low.Id], byPriority.Items.Select(t => t.Id));
        Assert.Equal(1, await CountAsync(manager, "&priority=Critical"));
        Assert.Equal(2, await CountAsync(manager, "&status=New"));
        Assert.Equal(1, await CountAsync(manager, "&status=Assigned"));
        Assert.Equal(3, await CountAsync(manager, "&status=New,Assigned"));
        Assert.Equal(1, await CountAsync(manager, $"&ownerId={s.Agent.Id}"));
        Assert.Equal(1, await CountAsync(manager, "&search=Kritik"));
        Assert.Equal(1, await CountAsync(manager, $"&search={critical.Number}"));
        Assert.Equal(3, await CountAsync(manager, $"&partyId={s.PartyId}"));
        Assert.Equal(0, await CountAsync(manager, $"&partyId={Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/tickets?status=Uydurma")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/tickets?sort=delete")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/tickets?bilinmeyen=1")).StatusCode);
    }

    // ---- Aktivite (görev/görüşme) talebe bağlanır; talebin yerine geçmez

    [Fact]
    public async Task Activities_link_to_a_ticket_only_when_the_ticket_is_visible_to_the_user()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var a = await factory.CreateUserAsync(tenant);
        var b = await factory.CreateUserAsync(tenant);
        string[] perms = [.. AgentPermissions, Crm.ActivitiesRead, Crm.ActivitiesWrite];
        await factory.CreateRoleAsync(tenant.Id, perms, Scope.Own, a, b);
        Guid partyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Bağlantı Ltd." };
            db.Set<Party>().Add(party);
            await db.SaveChangesAsync();
            partyId = party.Id;
        }

        using var clientA = await LoginAsync(a);
        using var clientB = await LoginAsync(b);
        var ticket = await TakeAsync(clientA, await CreateAsync(clientA, new SaveTicketRequest(Subject: "Geri aranacak", PartyId: partyId)));

        var followUp = await clientA.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Müşteriyi geri ara", ActivityType.Call, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(HttpStatusCode.Created, followUp.StatusCode);
        var activity = (await followUp.Content.ReadFromJsonAsync<ActivityDto>())!;
        var link = await clientA.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(activity.Id, "support.ticket", ticket.Id, IsPrimary: true));
        Assert.True(link.StatusCode == HttpStatusCode.Created, await link.Content.ReadAsStringAsync());
        Assert.Equal(1, (await clientA.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/crm/activities?related=support.ticket:{ticket.Id}"))!.TotalCount);

        // Talebi göremeyen kullanıcı ona aktivite bağlayamaz ve bağlı aktiviteleri sorgulayamaz; bağlantı yetkisiz erişim yolu açmaz.
        var other = await clientB.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Yetkisiz", ActivityType.Task, DateTimeOffset.UtcNow.AddDays(1)));
        var otherActivity = (await other.Content.ReadFromJsonAsync<ActivityDto>())!;
        var denied = await clientB.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(otherActivity.Id, "support.ticket", ticket.Id));
        Assert.NotEqual(HttpStatusCode.Created, denied.StatusCode);
        Assert.Equal(0, (await clientB.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/crm/activities?related=support.ticket:{ticket.Id}"))!.TotalCount);
    }

    [Fact]
    public async Task A_ticket_for_a_crm_company_uses_the_same_shared_customer_record_without_copying_it()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [.. AgentPermissions, Crm.CompaniesRead, Crm.CompaniesWrite], Scope.All, user);
        using var client = await LoginAsync(user);

        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Ortak Müşteri A.Ş."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var companyDto = (await company.Content.ReadFromJsonAsync<CompanyDto>())!;
        var partyId = await factory.ScalarAsync<Guid>(tenant.Id, "SELECT PartyId FROM crm.Companies WHERE Id = @id", ("@id", companyDto.Id));
        var partiesBefore = await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", tenant.Id));

        var ticket = await CreateAsync(client, new SaveTicketRequest(Subject: "Firma kartından açıldı", PartyId: partyId));

        // CRM firması ve destek talebi aynı ortak taraf kaydını kullanır: müşteri kopyalanmadı, ünvan kopyası ortak kayıttan geldi.
        Assert.Equal(partyId, ticket.PartyId);
        Assert.Equal("Ortak Müşteri A.Ş.", ticket.CustomerName);
        Assert.Equal(partiesBefore, await factory.ScalarAsync<int>(tenant.Id, "SELECT COUNT(*) FROM core.Parties WHERE TenantId = @t", ("@t", tenant.Id)));
        Assert.Equal(1, await CountAsync(client, $"&partyId={partyId}"));
    }
}
