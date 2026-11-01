using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Modulix.Shared.Support;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 2: SLA. Takvim ve politika yönetimi (doğrulama, izin, sürüm), politika seçimi ve anlık görüntü, duraklatma, ilk yanıt/çözüm tamamlama, ihlalin gerçek hedef anına
/// göre işaretlenmesi, eskalasyonun bir kez uygulanması (tekrar ve paralel çalıştırmada), yeniden açma döngüsü, öncelik değişiminin ihlali silememesi, yetkili yeniden hesaplama,
/// önizleme, tenant izolasyonu ve gösterge/liste tutarlılığı. Saat beklenmez: hedef anları veritabanında geriye alınır, hesaplar birim testlerde sabit saatle sınanır.
/// </summary>
public class SupportSlaTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];
    private static readonly string[] LeadPermissions = [.. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel, PriorityOverride, SlaRecalculate];
    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage, SlaManage];

    private sealed record Setup(Tenant Tenant, Guid PartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Agent, ModulixUser Agent2, ModulixUser Viewer, ModulixUser NoAccess);

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
            var party = new Party { Name = "SLA Müşterisi A.Ş.", Email = "sla@musteri.example.test" };
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

    private static string Weekdays(string start = "09:00", string end = "18:00") =>
        JsonSerializer.Serialize(Enumerable.Range(1, 5).Select(d => new { day = d, start, end }));

    private static async Task<T> CreateAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<SlaPolicyDto> PolicyAsync(HttpClient client, string name, int? firstResponse = 60, int? resolution = 480, Guid? calendarId = null, string? priority = null,
        string? type = null, string? rules = null, bool isDefault = false, int precedence = 100, bool pauseInternal = false, Guid? partyId = null) =>
        await CreateAsync<SlaPolicyDto>(client, "sla-policies", new SaveSlaPolicyRequest(
            Name: name, FirstResponseMinutes: firstResponse, ResolutionMinutes: resolution, CalendarId: calendarId, Priority: priority, TypeKey: type, EscalationRules: rules,
            IsDefault: isDefault, Precedence: precedence, PauseOnWaitingInternal: pauseInternal, PartyId: partyId));

    private static async Task<TicketDto> OpenTicketAsync(HttpClient client, Setup s, string subject = "SLA talebi", string? impact = null, string? urgency = null, string? type = null)
    {
        var response = await client.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: subject, PartyId: s.PartyId, Impact: impact, Urgency: urgency, TypeKey: type));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static async Task<List<SlaInstanceDto>> SlaAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<SlaInstanceDto>>($"/api/support/tickets/{id}/sla"))!;

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid id, string action, TicketActionRequest request) =>
        client.PostAsJsonAsync($"/api/support/tickets/{id}/{action}", request);

    private static async Task<TicketDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> TakeAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "take", new TicketActionRequest(t.Version)));

    private static async Task<TicketDto> ResolveAsync(HttpClient client, TicketDto t) =>
        await OkAsync(await ActAsync(client, t.Id, "resolve", new TicketActionRequest(t.Version, ResolutionCode: "Resolved", ResolutionNote: "Çözüldü.")));

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    /// <summary>Çalışan SLA örneğini zamanda geriye alır: başlangıç, sayaç ve hedef anı birlikte kayar (7/24 politika).</summary>
    private async Task RewindAsync(Guid tenantId, Guid ticketId, string metric, TimeSpan ago)
    {
        var seconds = -(int)ago.TotalSeconds;
        await SqlAsync(tenantId,
            "UPDATE support.SlaInstances SET StartedAt = DATEADD(second, {0}, StartedAt), RunningSince = DATEADD(second, {0}, RunningSince), DueAt = DATEADD(second, {0}, DueAt), WarningAt = DATEADD(second, {0}, WarningAt) WHERE TicketId = {1} AND Metric = {2} AND Status = 'Running'",
            seconds, ticketId, metric);
        // Listedeki hedef anı kopyası da aynı kayar (otorite SLA örneğidir; kopya aynı işlemde eşitlenir).
        await SqlAsync(tenantId,
            metric == "Resolution" ? "UPDATE support.Tickets SET ResolutionDueAt = DATEADD(second, {0}, ResolutionDueAt) WHERE Id = {1}" : "UPDATE support.Tickets SET FirstResponseDueAt = DATEADD(second, {0}, FirstResponseDueAt) WHERE Id = {1}",
            seconds, ticketId);
    }

    private async Task<SlaSchedulerRun> RunSchedulerAsync(Guid tenantId)
    {
        var (run, skipped) = await factory.Services.GetRequiredService<SupportJobsRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return run!;
    }

    private static string Rules(params object[] rules) => JsonSerializer.Serialize(rules, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });

    private static object Rule(string id, string metric, int percent, string action, Guid? userId = null, Guid? queueId = null) => new { id, metric, percent, action, userId, queueId };

    // ---- Yönetim: takvim ve politika

    [Fact]
    public async Task Calendar_and_policy_management_is_validated_versioned_and_needs_the_manage_permission()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/support/sla-calendars", new SaveSlaCalendarRequest(Name: "Yetkisiz", WeeklyHours: Weekdays()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/support/sla-policies")).StatusCode);

        // Takvim doğrulaması: saat dilimi, pencere, tatil.
        async Task<HttpStatusCode> CalendarStatus(SaveSlaCalendarRequest r) => (await manager.PostAsJsonAsync("/api/support/sla-calendars", r)).StatusCode;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await CalendarStatus(new SaveSlaCalendarRequest(Name: "Bozuk dilim", TimeZoneId: "Yok/Olmayan", WeeklyHours: Weekdays())));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await CalendarStatus(new SaveSlaCalendarRequest(Name: "Pencere yok", WeeklyHours: null)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await CalendarStatus(new SaveSlaCalendarRequest(Name: "Ters pencere", WeeklyHours: Weekdays("18:00", "09:00"))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await CalendarStatus(new SaveSlaCalendarRequest(Name: "Bozuk tatil", WeeklyHours: Weekdays(), Holidays: """[{"date":"29.10.2026"}]""")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await CalendarStatus(new SaveSlaCalendarRequest(Name: "", WeeklyHours: Weekdays())));
        var calendar = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "Mesai", WeeklyHours: Weekdays(), Holidays: """[{"date":"2026-10-29","name":"Cumhuriyet Bayramı"}]"""));
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        Assert.True(always.IsAlwaysOpen);

        // Politika doğrulaması.
        async Task<HttpStatusCode> PolicyStatus(SaveSlaPolicyRequest r) => (await manager.PostAsJsonAsync("/api/support/sla-policies", r)).StatusCode;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Hedefsiz")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Sıfır hedef", ResolutionMinutes: 0)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Kısıtlı varsayılan", ResolutionMinutes: 60, IsDefault: true, Priority: "High")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Yabancı tür", ResolutionMinutes: 60, TypeKey: "Olmayan")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Geçersiz öncelik", ResolutionMinutes: 60, Priority: "Urgent")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Bilinmeyen takvim", ResolutionMinutes: 60, CalendarId: Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Kural bozuk", ResolutionMinutes: 60, EscalationRules: "{bozuk")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Kullanıcısız bildirim", ResolutionMinutes: 60, EscalationRules: Rules(Rule("lead", "Resolution", 100, "NotifyUser")))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "Yabancı kullanıcı", ResolutionMinutes: 60, EscalationRules: Rules(Rule("lead", "Resolution", 100, "NotifyUser", Guid.NewGuid())))));

        var fallback = await PolicyAsync(manager, "Varsayılan", isDefault: true, calendarId: always.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await PolicyStatus(new SaveSlaPolicyRequest(Name: "İkinci varsayılan", ResolutionMinutes: 60, IsDefault: true)));
        Assert.Equal(1, fallback.Revision);

        // Sürüm yalnızca anlamlı değişiklikte artar.
        var same = await manager.PutAsJsonAsync($"/api/support/sla-policies/{fallback.Id}", new SaveSlaPolicyRequest(fallback.Version, fallback.Name, fallback.Description, fallback.IsActive, true, fallback.Precedence, null, null, null, null, 60, 480, always.Id));
        Assert.Equal(1, (await same.Content.ReadFromJsonAsync<SlaPolicyDto>())!.Revision);
        var changed = await manager.PutAsJsonAsync($"/api/support/sla-policies/{fallback.Id}", new SaveSlaPolicyRequest((await manager.GetFromJsonAsync<SlaPolicyDto>($"/api/support/sla-policies/{fallback.Id}"))!.Version, fallback.Name, null, true, true, 100, null, null, null, null, 30, 240, always.Id));
        Assert.Equal(2, (await changed.Content.ReadFromJsonAsync<SlaPolicyDto>())!.Revision);

        // Kullanılan takvim ve politika silinemez.
        var other = await PolicyAsync(manager, "Mesai politikası", calendarId: calendar.Id, priority: "High");
        var unused = await PolicyAsync(manager, "Kullanılmayan", type: "Info", calendarId: always.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/support/sla-calendars/{calendar.Id}?version={calendar.Version}")).StatusCode);
        var ticket = await OpenTicketAsync(manager, s, impact: "High", urgency: "Medium");
        Assert.NotEmpty(await SlaAsync(manager, ticket.Id));
        var current = (await manager.GetFromJsonAsync<SlaPolicyDto>($"/api/support/sla-policies/{fallback.Id}"))!;
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/support/sla-policies/{unused.Id}?version={unused.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/support/sla-policies/{other.Id}?version={other.Version}")).StatusCode);
        // Hiçbir örnekte kullanılmayan (yedek) politika silinebilir.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/support/sla-policies/{current.Id}?version={current.Version}")).StatusCode);
    }

    // ---- Seçim, anlık görüntü ve önizleme

    [Fact]
    public async Task A_new_ticket_gets_instances_from_the_most_specific_policy_and_later_policy_edits_do_not_touch_open_tickets()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);

        // Politika yokken talep hedefsizdir (sessizce varsayılan uydurulmaz).
        var unscoped = await OpenTicketAsync(agent, s, "Politikasız");
        Assert.Empty(await SlaAsync(agent, unscoped.Id));
        Assert.Null(unscoped.ResolutionDueAt);

        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(manager, "Genel", calendarId: always.Id, isDefault: true, firstResponse: 120, resolution: 960);
        var specific = await PolicyAsync(manager, "Yüksek öncelik", calendarId: always.Id, priority: "High", firstResponse: 30, resolution: 240);

        var ticket = await OpenTicketAsync(agent, s, "Yüksek", impact: "High", urgency: "Medium");
        var instances = await SlaAsync(agent, ticket.Id);
        Assert.Equal(["FirstResponse", "Resolution"], instances.Select(i => i.Metric).Order());
        var resolution = instances.Single(i => i.Metric == "Resolution");
        Assert.Equal(("Yüksek öncelik", 1, 240, "Running"), (resolution.PolicyName, resolution.PolicyRevision, resolution.TargetMinutes, resolution.State));
        Assert.Equal(resolution.StartedAt.AddMinutes(240), resolution.DueAt);
        Assert.Equal(30, instances.Single(i => i.Metric == "FirstResponse").TargetMinutes);
        var refreshed = await GetAsync(agent, ticket.Id);
        Assert.Equal(resolution.DueAt, refreshed.ResolutionDueAt);
        Assert.NotNull(refreshed.FirstResponseDueAt);

        // Normal öncelikli talep yedek politikayı alır.
        var normal = await OpenTicketAsync(agent, s, "Normal");
        Assert.Equal("Genel", (await SlaAsync(agent, normal.Id)).First().PolicyName);

        // Politika sonradan değişse de açık talebin anlık görüntüsü ve hedefi değişmez; yeni talep yeni sürümü alır.
        var edit = await manager.PutAsJsonAsync($"/api/support/sla-policies/{specific.Id}", new SaveSlaPolicyRequest(specific.Version, specific.Name, null, true, false, 100, null, null, "High", null, 5, 10, always.Id));
        Assert.Equal(2, (await edit.Content.ReadFromJsonAsync<SlaPolicyDto>())!.Revision);
        Assert.Equal((240, 1), ((await SlaAsync(agent, ticket.Id)).Single(i => i.Metric == "Resolution") is var old ? (old.TargetMinutes, old.PolicyRevision) : default));
        var next = await OpenTicketAsync(agent, s, "Yeni yüksek", impact: "High", urgency: "Medium");
        Assert.Equal((10, 2), ((await SlaAsync(agent, next.Id)).Single(i => i.Metric == "Resolution") is var fresh ? (fresh.TargetMinutes, fresh.PolicyRevision) : default));
    }

    [Fact]
    public async Task Preview_shows_the_selected_policy_and_calendar_based_targets_for_a_sample_ticket()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        var other = await SetUpAsync();
        using var foreign = await LoginAsync(other.Manager);

        var calendar = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "Mesai", WeeklyHours: Weekdays()));
        var holidayCalendar = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars",
            new SaveSlaCalendarRequest(Name: "Pazartesi tatil", WeeklyHours: Weekdays(), Holidays: """[{"date":"2026-11-09","name":"Tatil"}]"""));
        var rules = Rules(Rule("warn", "Resolution", 75, "NotifyOwner"));
        await PolicyAsync(manager, "İki saat", firstResponse: null, resolution: 120, calendarId: calendar.Id, priority: "Normal", rules: rules);
        await PolicyAsync(manager, "Tatilli", firstResponse: null, resolution: 120, calendarId: holidayCalendar.Id, priority: "High");

        // Cuma 17:00 (İstanbul) açılan kayıt, hedef 2 iş saati: Pazartesi 10:00; Pazartesi tatilse Salı 10:00. Uyarı eşiği %75 = 1,5 iş saati.
        var friday = new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero);
        var normal = (await (await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Normal", StartAt: friday))).Content.ReadFromJsonAsync<SlaPreviewDto>())!;
        Assert.True(normal.Matched);
        Assert.Equal("İki saat", normal.PolicyName);
        var metric = Assert.Single(normal.Metrics);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), metric.DueAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 6, 30, 0, TimeSpan.Zero), metric.WarningAt);
        Assert.Single(normal.Rules);

        var high = (await (await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "High", StartAt: friday))).Content.ReadFromJsonAsync<SlaPreviewDto>())!;
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 7, 0, 0, TimeSpan.Zero), Assert.Single(high.Metrics).DueAt);

        // Eşleşme yoksa hedefsiz; geçersiz girdi ve yabancı tenant kimlikleri reddedilir; yetkisiz kullanıcı önizleyemez.
        var none = (await (await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Low", StartAt: friday))).Content.ReadFromJsonAsync<SlaPreviewDto>())!;
        Assert.False(none.Matched);
        Assert.Empty(none.Metrics);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Olmayan", "Low"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Acil"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await foreign.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Low", PartyId: s.PartyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Normal"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/sla-policies/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Policy_selection_through_the_api_is_deterministic_precedence_decides_between_equally_specific_policies()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));

        await PolicyAsync(manager, "B politikası", calendarId: always.Id, type: "Support", precedence: 100, resolution: 100);
        await PolicyAsync(manager, "A politikası", calendarId: always.Id, type: "Support", precedence: 100, resolution: 200);
        await PolicyAsync(manager, "Öncelikli", calendarId: always.Id, type: "Support", precedence: 10, resolution: 300);
        await PolicyAsync(manager, "Müşteriye özel", calendarId: always.Id, type: "Support", partyId: s.PartyId, precedence: 500, resolution: 400);

        // En özel (iki kısıt: tür + müşteri) kazanır, öncelik sayısı yalnızca eşit özgüllükte karar verir.
        var ticket = await OpenTicketAsync(agent, s);
        Assert.Equal("Müşteriye özel", (await SlaAsync(agent, ticket.Id)).First().PolicyName);
        var anonymous = await agent.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: "Anonim", ReporterName: "Ali"));
        var anon = (await anonymous.Content.ReadFromJsonAsync<TicketDto>())!;
        Assert.Equal("Öncelikli", (await SlaAsync(agent, anon.Id)).First().PolicyName);
    }

    // ---- Duraklatma ve tamamlama

    [Fact]
    public async Task Waiting_for_the_customer_pauses_the_clock_internal_waiting_does_not_by_default_and_the_policy_can_change_that()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(manager, "Varsayılan", calendarId: always.Id, isDefault: true);

        var ticket = await TakeAsync(agent, await OpenTicketAsync(agent, s));
        ticket = await OkAsync(await ActAsync(agent, ticket.Id, "start", new TicketActionRequest(ticket.Version)));
        var startDue = (await SlaAsync(agent, ticket.Id)).Single(i => i.Metric == "Resolution").DueAt;

        // İç ekip beklemesi varsayılan olarak sayacı durdurmaz.
        var internalWait = await OkAsync(await ActAsync(agent, ticket.Id, "wait-internal", new TicketActionRequest(ticket.Version, "Tedarikçi")));
        Assert.All(await SlaAsync(agent, ticket.Id), i => Assert.Equal("Running", i.Status));

        // Müşteri beklemesi durdurur: hedef anı yok, duraklatma aralığı açık.
        var waiting = await OkAsync(await ActAsync(agent, ticket.Id, "wait-customer", new TicketActionRequest(internalWait.Version, "Bilgi bekleniyor")));
        var paused = await SlaAsync(agent, ticket.Id);
        Assert.All(paused, i => Assert.Equal(("Paused", "Paused"), (i.Status, i.State)));
        Assert.All(paused, i => Assert.Null(i.DueAt));
        var pause = Assert.Single(paused.First().Pauses);
        Assert.Equal(("WaitingCustomer", null), (pause.Reason, pause.EndedAt));
        Assert.Null((await GetAsync(agent, ticket.Id)).ResolutionDueAt);

        // Müşteri yanıtı sayacı sürdürür: aralık kapanır, hedef anı yeniden hesaplanır (kalan iş süresi korunur).
        await SqlAsync(s.Tenant.Id, "UPDATE support.SlaPauses SET StartedAt = DATEADD(hour, -3, StartedAt)");
        var message = await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/messages", new AddTicketMessageRequest("customer-message", "Fatura no: 1"));
        Assert.Equal(HttpStatusCode.Created, message.StatusCode);
        var resumed = (await SlaAsync(agent, ticket.Id)).Single(i => i.Metric == "Resolution");
        Assert.Equal(("Running", "Running"), (resumed.Status, resumed.State));
        Assert.NotNull(resumed.Pauses[0].EndedAt);
        Assert.True(resumed.DueAt >= startDue, "Duraklatma hedefi öne çekemez.");
        Assert.True(resumed.WorkedMinutes < 5);
        Assert.Equal("InProgress", waiting.Status == "WaitingCustomer" ? (await GetAsync(agent, ticket.Id)).Status : "?");

        // Politika iç ekip beklemesini de durduracak şekilde ayarlıysa durdurur.
        var strict = await SetUpAsync();
        using var strictManager = await LoginAsync(strict.Manager);
        using var strictAgent = await LoginAsync(strict.Agent);
        var strictAlways = await CreateAsync<SlaCalendarDto>(strictManager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(strictManager, "Katı", calendarId: strictAlways.Id, isDefault: true, pauseInternal: true);
        var t2 = await TakeAsync(strictAgent, await OpenTicketAsync(strictAgent, strict));
        t2 = await OkAsync(await ActAsync(strictAgent, t2.Id, "start", new TicketActionRequest(t2.Version)));
        await OkAsync(await ActAsync(strictAgent, t2.Id, "wait-internal", new TicketActionRequest(t2.Version, "Ekip")));
        Assert.All(await SlaAsync(strictAgent, t2.Id), i => Assert.Equal("Paused", i.Status));
    }

    [Fact]
    public async Task First_response_and_resolution_complete_the_instances_and_a_late_completion_is_a_breach_at_the_real_target_time()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(manager, "Varsayılan", calendarId: always.Id, isDefault: true, firstResponse: 60, resolution: 480);

        // Zamanında ilk yanıt ve çözüm: Met. İç not ilk yanıt sayılmaz.
        var ok = await TakeAsync(agent, await OpenTicketAsync(agent, s, "Zamanında"));
        await agent.PostAsJsonAsync($"/api/support/tickets/{ok.Id}/messages", new AddTicketMessageRequest("note", "İç not"));
        Assert.Equal("Running", (await SlaAsync(agent, ok.Id)).Single(i => i.Metric == "FirstResponse").State);
        await agent.PostAsJsonAsync($"/api/support/tickets/{ok.Id}/messages", new AddTicketMessageRequest("reply", "Merhaba"));
        var afterReply = await SlaAsync(agent, ok.Id);
        Assert.Equal(("Completed", "Met"), (afterReply.Single(i => i.Metric == "FirstResponse").Status, afterReply.Single(i => i.Metric == "FirstResponse").State));
        var started = await OkAsync(await ActAsync(agent, ok.Id, "start", new TicketActionRequest((await GetAsync(agent, ok.Id)).Version)));
        await ResolveAsync(agent, started);
        Assert.All(await SlaAsync(agent, ok.Id), i => Assert.Equal("Met", i.State));
        Assert.Null((await GetAsync(agent, ok.Id)).ResolutionDueAt);

        // Hedef anı geçtikten sonra tamamlanan örnek: ihlal, iş çalışmasa da gerçek hedef anıyla kaydedilir.
        var late = await TakeAsync(agent, await OpenTicketAsync(agent, s, "Geç"));
        await RewindAsync(s.Tenant.Id, late.Id, "FirstResponse", TimeSpan.FromHours(3));
        var dueBefore = (await SlaAsync(agent, late.Id)).Single(i => i.Metric == "FirstResponse").DueAt!.Value;
        await agent.PostAsJsonAsync($"/api/support/tickets/{late.Id}/messages", new AddTicketMessageRequest("reply", "Geç yanıt"));
        var first = (await SlaAsync(agent, late.Id)).Single(i => i.Metric == "FirstResponse");
        Assert.Equal(("Completed", "Missed"), (first.Status, first.State));
        Assert.Equal(dueBefore, first.BreachedAt);
        Assert.True(first.CompletedAt > dueBefore);

        // Çözümle ilk yanıt hedefi de kapanır; iptal örnekleri iptal eder.
        var noReply = await TakeAsync(agent, await OpenTicketAsync(agent, s, "Yanıtsız çözüm"));
        var noReplyStarted = await OkAsync(await ActAsync(agent, noReply.Id, "start", new TicketActionRequest((await GetAsync(agent, noReply.Id)).Version)));
        await ResolveAsync(agent, noReplyStarted);
        Assert.All(await SlaAsync(agent, noReply.Id), i => Assert.Equal("Completed", i.Status));
        using var lead = await LoginAsync(s.Lead);
        var cancelled = await OpenTicketAsync(agent, s, "İptal");
        await OkAsync(await ActAsync(lead, cancelled.Id, "cancel", new TicketActionRequest(cancelled.Version, "Vazgeçildi")));
        Assert.All(await SlaAsync(lead, cancelled.Id), i => Assert.Equal("Cancelled", i.State));
    }

    // ---- Zamanlayıcı: ihlal ve eskalasyon

    [Fact]
    public async Task The_scheduler_marks_breaches_at_the_real_target_time_and_applies_each_escalation_exactly_once()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        var general = await CreateAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Genel", IsDefault: true));
        var escalation = await CreateAsync<QueueDto>(manager, "queues", new SaveQueueRequest(Name: "Eskalasyon"));
        await PolicyAsync(manager, "Eskalasyonlu", calendarId: always.Id, isDefault: true, firstResponse: null, resolution: 480, rules: Rules(
            Rule("warn", "Resolution", 75, "NotifyOwner"),
            Rule("lead", "Resolution", 100, "NotifyUser", s.Lead.Id),
            Rule("move", "Resolution", 150, "Reassign", queueId: escalation.Id)));

        var ticket = await TakeAsync(agent, await OpenTicketAsync(agent, s, "Eskale edilecek"));
        Assert.Equal(general.Id, ticket.QueueId);
        var before = (await SlaAsync(agent, ticket.Id)).Single();
        Assert.NotNull(before.WarningAt);

        // Hedef henüz gelmedi: iş hiçbir şey yapmaz.
        Assert.Equal(new SlaSchedulerRun(0, 0), await RunSchedulerAsync(s.Tenant.Id));

        // Kesinti simülasyonu: sayaç 2 gün önce başlamış gibi (hedefin %150'si geçmiş). İş şimdi çalışır ama ihlal gerçek hedef anıyla işlenir.
        await RewindAsync(s.Tenant.Id, ticket.Id, "Resolution", TimeSpan.FromDays(2));
        var due = (await SlaAsync(agent, ticket.Id)).Single().DueAt!.Value;
        Assert.True(due < DateTimeOffset.UtcNow.AddDays(-1));

        var run = await RunSchedulerAsync(s.Tenant.Id);
        Assert.Equal(new SlaSchedulerRun(1, 3), run);
        var after = (await SlaAsync(lead, ticket.Id)).Single();
        Assert.Equal(("Breached", due), (after.State, after.BreachedAt));
        Assert.Equal(["lead", "move", "warn"], after.Escalations.Select(e => e.RuleId).Order());
        Assert.Equal(new[] { 75, 100, 150 }, after.Escalations.Select(e => e.Percent).Order());
        Assert.All(after.Escalations, e => Assert.True(e.ThresholdAt < DateTimeOffset.UtcNow.AddDays(-1) || e.RuleId == "move"));

        // Yeniden atama: kuyruk değişti, sorumlu korundu, tarihçeye düştü; bildirimler (sahip ve lider) yalnızca bir kez kuyruğa yazıldı.
        var moved = await GetAsync(lead, ticket.Id);
        Assert.Equal((escalation.Id, s.Agent.Id), (moved.QueueId, moved.OwnerId));
        var emails = () => factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM platform.EmailMessages WHERE Reference LIKE @r", ("@r", $"support.sla:{after.Id}:%"));
        Assert.Equal(2, await emails());
        var kinds = (await lead.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{ticket.Id}/history"))!.Select(h => h.Kind).ToList();
        Assert.Contains("SlaBreached", kinds);
        Assert.Contains("SlaEscalated", kinds);

        // Tekrar ve paralel çalıştırma: hiçbir eylem ya da kayıt çoğalmaz.
        Assert.Equal(new SlaSchedulerRun(0, 0), await RunSchedulerAsync(s.Tenant.Id));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => factory.Services.GetRequiredService<SupportJobsRunner>().RunTenantAsync(s.Tenant.Id, CancellationToken.None)));
        Assert.All(parallel, r => Assert.Equal(0, (r.Run?.Breaches ?? 0) + (r.Run?.Escalations ?? 0)));
        Assert.Equal(3, (await SlaAsync(lead, ticket.Id)).Single().Escalations.Count);
        Assert.Equal(2, await emails());
        Assert.Equal(1, (await lead.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{ticket.Id}/history"))!.Count(h => h.Kind == "SlaBreached"));
    }

    [Fact]
    public async Task A_breach_survives_priority_changes_and_reopening_starts_a_new_cycle_while_keeping_the_old_one()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(manager, "Yüksek", calendarId: always.Id, priority: "High", firstResponse: null, resolution: 60);
        await PolicyAsync(manager, "Düşük", calendarId: always.Id, priority: "Low", firstResponse: null, resolution: 6000);
        await PolicyAsync(manager, "Normal", calendarId: always.Id, priority: "Normal", firstResponse: null, resolution: 480);

        // İhlal edilmemiş örnek: öncelik değişince politika yeniden seçilir, harcanan süre korunur, hedef kalan süreden yeniden hesaplanır.
        var movable = await TakeAsync(lead, await OpenTicketAsync(agent, s, "Öncelik düşecek", impact: "High", urgency: "Medium"));
        Assert.Equal("Yüksek", (await SlaAsync(lead, movable.Id)).Single().PolicyName);
        await OkAsync(await ActAsync(lead, movable.Id, "set-priority", new TicketActionRequest((await GetAsync(lead, movable.Id)).Version, "Müşteri onayladı", Priority: "Low")));
        var lowered = (await SlaAsync(lead, movable.Id)).Single();
        Assert.Equal(("Düşük", 6000), (lowered.PolicyName, lowered.TargetMinutes));
        Assert.True(lowered.DueAt > lowered.StartedAt.AddMinutes(5990));

        // İhlal edilmiş örnek: öncelik düşürülse de ihlal silinmez, hedef değişmez.
        var breached = await TakeAsync(lead, await OpenTicketAsync(agent, s, "İhlal", impact: "High", urgency: "Medium"));
        await RewindAsync(s.Tenant.Id, breached.Id, "Resolution", TimeSpan.FromHours(5));
        Assert.Equal(1, (await RunSchedulerAsync(s.Tenant.Id)).Breaches);
        var marked = (await SlaAsync(lead, breached.Id)).Single();
        Assert.Equal("Breached", marked.State);
        await OkAsync(await ActAsync(lead, breached.Id, "set-priority", new TicketActionRequest((await GetAsync(lead, breached.Id)).Version, "Hafifletildi", Priority: "Low")));
        var stillBreached = (await SlaAsync(lead, breached.Id)).Single();
        Assert.Equal(("Breached", "Yüksek", 60, marked.BreachedAt), (stillBreached.State, stillBreached.PolicyName, stillBreached.TargetMinutes, stillBreached.BreachedAt));

        // Çözüm döngüyü kapatır (hedef geçmiş: Missed); yeniden açma yeni döngü başlatır, önceki döngü ve ihlal olduğu gibi kalır.
        var resolved = await ResolveAsync(lead, await OkAsync(await ActAsync(lead, breached.Id, "start", new TicketActionRequest((await GetAsync(lead, breached.Id)).Version))));
        Assert.Equal("Missed", (await SlaAsync(lead, breached.Id)).Single().State);
        var reopened = await OkAsync(await ActAsync(lead, breached.Id, "reopen", new TicketActionRequest(resolved.Version, "Tekrar bozuldu")));
        var cycles = await SlaAsync(lead, breached.Id);
        Assert.Equal([1, 2], cycles.Select(i => i.Cycle).Order());
        Assert.Equal(("Missed", marked.BreachedAt), (cycles.Single(i => i.Cycle == 1).State, cycles.Single(i => i.Cycle == 1).BreachedAt));
        var second = cycles.Single(i => i.Cycle == 2);
        Assert.Equal(("Running", "Düşük"), (second.State, second.PolicyName));
        Assert.Equal(reopened.ResolutionDueAt, second.DueAt);
    }

    // ---- Yeniden hesaplama, görünürlük, göstergeler

    [Fact]
    public async Task Recalculation_needs_its_own_permission_and_a_reason_and_writes_before_and_after_to_the_history()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        using var lead = await LoginAsync(s.Lead);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        var policy = await PolicyAsync(manager, "Varsayılan", calendarId: always.Id, isDefault: true, firstResponse: null, resolution: 480);
        var ticket = await TakeAsync(agent, await OpenTicketAsync(agent, s));

        var edited = await manager.PutAsJsonAsync($"/api/support/sla-policies/{policy.Id}", new SaveSlaPolicyRequest(policy.Version, policy.Name, null, true, true, 100, null, null, null, null, null, 120, always.Id));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(480, (await SlaAsync(agent, ticket.Id)).Single().TargetMinutes);

        var current = await GetAsync(lead, ticket.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/sla/recalculate", new SlaRecalculateRequest(current.Version, "Deneme"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await lead.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/sla/recalculate", new SlaRecalculateRequest(current.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/sla/recalculate", new SlaRecalculateRequest(current.Version - 1, "Eski sürüm"))).StatusCode);

        var recalculated = await lead.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/sla/recalculate", new SlaRecalculateRequest(current.Version, "Politika düzeltmesi"));
        Assert.True(recalculated.StatusCode == HttpStatusCode.OK, await recalculated.Content.ReadAsStringAsync());
        var after = (await SlaAsync(lead, ticket.Id)).Single();
        Assert.Equal((120, 2), (after.TargetMinutes, after.PolicyRevision));
        var history = (await lead.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{ticket.Id}/history"))!;
        Assert.Equal("Politika düzeltmesi", history.Single(h => h.Kind == "SlaRecalculationRequested").Reason);
        Assert.Contains("Önce:", history.Single(h => h.Kind == "SlaRecalculated").Reason);
        Assert.Contains("Sonra:", history.Single(h => h.Kind == "SlaRecalculated").Reason);

        // Açık örneği olmayan (çözülmüş) talep yeniden hesaplanamaz.
        var resolved = await ResolveAsync(agent, await OkAsync(await ActAsync(agent, ticket.Id, "start", new TicketActionRequest((await GetAsync(agent, ticket.Id)).Version))));
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/sla/recalculate", new SlaRecalculateRequest(resolved.Version, "Çözülmüş"))).StatusCode);
    }

    [Fact]
    public async Task Sla_data_follows_ticket_visibility_and_stays_inside_the_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var agentA = await LoginAsync(a.Agent);
        using var agentA2 = await LoginAsync(a.Agent2);
        using var viewerA = await LoginAsync(a.Viewer);
        using var noAccessA = await LoginAsync(a.NoAccess);
        using var managerB = await LoginAsync(b.Manager);
        using var leadB = await LoginAsync(b.Lead);
        var always = await CreateAsync<SlaCalendarDto>(managerA, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        var policy = await PolicyAsync(managerA, "A politikası", calendarId: always.Id, isDefault: true);
        var owned = await TakeAsync(agentA, await OpenTicketAsync(agentA, a, "A'nın talebi"));

        // Aynı tenant: yalnızca talebi görebilen SLA'sını görür (kapsam "kendi"); izleyici okur; yetkisiz kullanıcı erişemez.
        Assert.Equal(HttpStatusCode.NotFound, (await agentA2.GetAsync($"/api/support/tickets/{owned.Id}/sla")).StatusCode);
        Assert.NotEmpty(await SlaAsync(viewerA, owned.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccessA.GetAsync($"/api/support/tickets/{owned.Id}/sla")).StatusCode);

        // Başka tenant: talep, SLA, yeniden hesaplama, politika ve takvim kimlikleri bulunamadı sayılır; politikada yabancı kimlik kullanılamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/tickets/{owned.Id}/sla")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await leadB.PostAsJsonAsync($"/api/support/tickets/{owned.Id}/sla/recalculate", new SlaRecalculateRequest(owned.Version, "Sızma"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/sla-policies/{policy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/support/sla-calendars/{always.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"/api/support/sla-policies/{policy.Id}?version={policy.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/sla-policies", new SaveSlaPolicyRequest(Name: "Yabancı takvim", ResolutionMinutes: 60, CalendarId: always.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/sla-policies", new SaveSlaPolicyRequest(Name: "Yabancı müşteri", ResolutionMinutes: 60, PartyId: a.PartyId))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync("/api/support/sla-policies", new SaveSlaPolicyRequest(
            Name: "Yabancı kullanıcı", ResolutionMinutes: 60, EscalationRules: Rules(Rule("x", "Resolution", 100, "NotifyUser", a.Lead.Id))))).StatusCode);
        Assert.Empty((await managerB.GetFromJsonAsync<PagedResult<SlaPolicyDto>>("/api/support/sla-policies"))!.Items);

        // B tenant'ının zamanlayıcı turu A'nın verisini işlemez; A'nın turu B'yi etkilemez.
        await RewindAsync(a.Tenant.Id, owned.Id, "Resolution", TimeSpan.FromDays(3));
        Assert.Equal(new SlaSchedulerRun(0, 0), await RunSchedulerAsync(b.Tenant.Id));
        Assert.Equal(1, (await RunSchedulerAsync(a.Tenant.Id)).Breaches);
    }

    [Fact]
    public async Task Dashboard_sla_counts_use_the_same_filters_as_the_list_and_the_list_can_sort_by_target()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var agent = await LoginAsync(s.Agent);
        var always = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await PolicyAsync(manager, "Uyarılı", calendarId: always.Id, isDefault: true, firstResponse: null, resolution: 480, rules: Rules(Rule("warn", "Resolution", 75, "NotifyOwner")));

        var healthy = await OpenTicketAsync(manager, s, "Sağlıklı");
        var risky = await OpenTicketAsync(manager, s, "Riskli");
        var breached = await OpenTicketAsync(manager, s, "İhlalli");
        var summary = (await manager.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!;
        Assert.Equal((0, 0), (summary.SlaAtRisk, summary.SlaBreached));

        await RewindAsync(s.Tenant.Id, risky.Id, "Resolution", TimeSpan.FromHours(7));   // %75 = 6 saat geçti, hedefe 1 saat var
        await RewindAsync(s.Tenant.Id, breached.Id, "Resolution", TimeSpan.FromHours(9)); // hedef 8 saat: geçti
        summary = (await manager.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!;
        Assert.Equal((1, 1), (summary.SlaAtRisk, summary.SlaBreached));
        var riskyList = (await manager.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets?slaAtRisk=true&open=true"))!;
        var breachedList = (await manager.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets?slaBreached=true&open=true"))!;
        Assert.Equal(risky.Id, Assert.Single(riskyList.Items).Id);
        Assert.Equal(breached.Id, Assert.Single(breachedList.Items).Id);

        // Zamanlayıcı ihlali işaretledikten sonra sayılar değişmez (aynı kayıtlar); sağlıklı talep hiçbirine girmez.
        await RunSchedulerAsync(s.Tenant.Id);
        summary = (await manager.GetFromJsonAsync<TicketSummaryDto>("/api/support/summary"))!;
        Assert.Equal((1, 1), (summary.SlaAtRisk, summary.SlaBreached));

        // Listeyi hedef anına göre sıralamak en yakın hedefi başa getirir.
        var sorted = (await manager.GetFromJsonAsync<PagedResult<TicketDto>>("/api/support/tickets?sort=resolutionDue"))!;
        Assert.Equal([breached.Id, risky.Id, healthy.Id], sorted.Items.Select(t => t.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/tickets?slaBreached=belki")).StatusCode);
        Assert.NotNull(agent);
    }
}
