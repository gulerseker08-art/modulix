using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Aktivite ve takvim yönetimi (ADR-0015): yaşam döngüsü eylemleri, kayıt başına yetki, özel aktivite gizliliği, çoklu ilişki ve
/// zaman akışı, çakışma, toplu işlem ve eşzamanlılık. Gerçek SQL Server ve RLS üzerinde çalışır.
/// </summary>
public sealed class ActivityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Zone = "Europe/Istanbul";

    private static readonly string[] Everything =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite,
        ActivitiesRead, ActivitiesWrite, ActivitiesAssign, ActivitiesReopen, ActivitiesManageCalendar,
    ];

    private async Task<(Tenant Tenant, ModulixUser User, HttpClient Client)> StartAsync(
        string[]? permissions = null, RecordScope scope = RecordScope.All)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions ?? Everything, scope, user);
        return (tenant, user, await LoginAsync(user.Email!));
    }

    private async Task<(ModulixUser User, HttpClient Client)> AddUserAsync(Tenant tenant, string[] permissions, RecordScope scope = RecordScope.All)
    {
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, permissions, scope, user);
        return (user, await LoginAsync(user.Email!));
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password))).StatusCode);
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ActivityDto> CreateAsync(HttpClient client, SaveActivityRequest request) =>
        await ReadAsync<ActivityDto>(await client.PostAsJsonAsync("/api/crm/activities", request), HttpStatusCode.Created);

    private static async Task<JsonObject> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task<List<ActivityDto>> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?pageSize=200{query}"))!.Items.ToList();

    private static Task<HttpResponseMessage> ActionAsync(HttpClient client, Guid id, string action, object body) =>
        client.PostAsJsonAsync($"/api/crm/activities/{id}/{action}", body);

    // ---- 1. Tenant izolasyonu

    [Fact]
    public async Task Another_tenants_activity_is_never_reachable_or_linkable()
    {
        var (_, _, client) = await StartAsync();
        var (_, _, outsider) = await StartAsync();
        var company = await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("A Ltd.")), HttpStatusCode.Created);
        var activity = await CreateAsync(client, new SaveActivityRequest("Gizli iş", CompanyId: company.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/crm/activities/{activity.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync($"/api/crm/activities/{activity.Id}",
            new SaveActivityRequest("Ele geçirildi", Version: activity.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActionAsync(outsider, activity.Id, "complete", new CompleteActivityRequest(activity.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActionAsync(outsider, activity.Id, "cancel", new ActivityReasonRequest(activity.Version, "x"))).StatusCode);
        Assert.Empty(await ListAsync(outsider));

        // Başka tenant'ın firması bağlanamaz; "var olmayan" ile aynı yanıt (varlığı belli edilmez).
        var problem = await ProblemAsync(await outsider.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Bağ", CompanyId: company.Id)),
            HttpStatusCode.UnprocessableEntity);
        Assert.Contains("companyId", problem["errors"]!.AsObject().Select(p => p.Key));
        var own = await CreateAsync(outsider, new SaveActivityRequest("Kendi işi"));
        await ProblemAsync(await outsider.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(own.Id, CrmModule.Entities.Company, company.Id)), HttpStatusCode.UnprocessableEntity);
    }

    // ---- 2. Yaşam döngüsü ve kapılı alanlar

    [Fact]
    public async Task Lifecycle_is_driven_by_actions_not_by_plain_updates()
    {
        var (_, _, client) = await StartAsync();
        var task = await CreateAsync(client, new SaveActivityRequest("Teklifi hazırla", DueAt: DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(ActivityStatus.Open, task.Status);
        Assert.True(task.IsOverdue(DateTimeOffset.UtcNow));

        // Düz güncelleme durumu, tamamlanmayı ve sonucu değiştiremez (sessizce yok sayılır).
        var edited = await ReadAsync<ActivityDto>(await client.PutAsJsonAsync($"/api/crm/activities/{task.Id}",
            new SaveActivityRequest("Teklifi hazırla (v2)", DueAt: task.DueAt, CompletedAt: DateTimeOffset.UtcNow, Outcome: ActivityOutcome.Reached, Version: task.Version)),
            HttpStatusCode.OK);
        Assert.Equal(ActivityStatus.Open, edited.Status);
        Assert.Null(edited.CompletedAt);
        Assert.Null(edited.Outcome);

        // Görev: bloke edilince tamamlanamaz; engel kalkınca tamamlanır; tamamlanan gecikmiş görünmez.
        var blocked = await ReadAsync<ActivityDto>(await ActionAsync(client, task.Id, "block", new ActivityReasonRequest(edited.Version, "Müşteriden bilgi bekleniyor")), HttpStatusCode.OK);
        Assert.Equal(ActivityStatus.Blocked, blocked.Status);
        Assert.Equal("Müşteriden bilgi bekleniyor", blocked.StatusReason);
        await ProblemAsync(await ActionAsync(client, task.Id, "complete", new CompleteActivityRequest(blocked.Version)), HttpStatusCode.UnprocessableEntity);
        await ProblemAsync(await ActionAsync(client, task.Id, "block", new ActivityReasonRequest(blocked.Version, " ")), HttpStatusCode.UnprocessableEntity);
        var open = await ReadAsync<ActivityDto>(await ActionAsync(client, task.Id, "unblock", new ActivityVersionRequest(blocked.Version)), HttpStatusCode.OK);
        var done = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, task.Id, "complete",
            new CompleteActivityRequest(open.Version, Note: "Gönderildi")), HttpStatusCode.OK)).Activity;
        Assert.Equal(ActivityStatus.Completed, done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.False(done.IsOverdue(DateTimeOffset.UtcNow));
        Assert.DoesNotContain(await ListAsync(client, "&overdue=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))), a => a.Id == task.Id);
        Assert.Contains(await ListAsync(client, "&open=false"), a => a.Id == task.Id);

        // Yeniden açma ayrı izin ister ve gerekçe zorunludur; tamamlanma temizlenir.
        await ProblemAsync(await ActionAsync(client, task.Id, "reopen", new ActivityReasonRequest(done.Version, null)), HttpStatusCode.UnprocessableEntity);
        var reopened = await ReadAsync<ActivityDto>(await ActionAsync(client, task.Id, "reopen", new ActivityReasonRequest(done.Version, "Yanlış tamamlandı")), HttpStatusCode.OK);
        Assert.Equal(ActivityStatus.Open, reopened.Status);
        Assert.Null(reopened.CompletedAt);

        // İptal gerekçe ister; iptal edilen gecikmiş sayılmaz.
        await ProblemAsync(await ActionAsync(client, task.Id, "cancel", new ActivityReasonRequest(reopened.Version, "")), HttpStatusCode.UnprocessableEntity);
        var cancelled = await ReadAsync<ActivityDto>(await ActionAsync(client, task.Id, "cancel", new ActivityReasonRequest(reopened.Version, "Müşteri vazgeçti")), HttpStatusCode.OK);
        Assert.Equal(ActivityStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.CompletedAt);
        Assert.False(cancelled.IsOverdue(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Calls_meetings_and_emails_have_their_own_outcomes()
    {
        var (_, _, client) = await StartAsync();

        var call = await CreateAsync(client, new SaveActivityRequest("Geri ara", ActivityType.Call, DateTimeOffset.UtcNow.AddHours(1)));
        var unreachable = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, call.Id, "complete",
            new CompleteActivityRequest(call.Version, ActivityOutcome.NoAnswer)), HttpStatusCode.OK)).Activity;
        Assert.Equal(ActivityStatus.NoAnswer, unreachable.Status);
        // Ulaşılamayan arama düzeltilebilir: yeniden açılır ve gerçekleşen arama olarak tamamlanır.
        var retry = await ReadAsync<ActivityDto>(await ActionAsync(client, call.Id, "reopen", new ActivityReasonRequest(unreachable.Version, "Tekrar arandı")), HttpStatusCode.OK);
        var reached = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, call.Id, "complete", new CompleteActivityRequest(retry.Version)), HttpStatusCode.OK)).Activity;
        Assert.Equal(ActivityStatus.Completed, reached.Status);
        Assert.Equal(ActivityOutcome.Reached, reached.Outcome);

        var meeting = await CreateAsync(client, new SaveActivityRequest("Sunum", ActivityType.Meeting, DateTimeOffset.UtcNow.AddHours(2)));
        var noShow = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, meeting.Id, "complete",
            new CompleteActivityRequest(meeting.Version, ActivityOutcome.NoAnswer)), HttpStatusCode.OK)).Activity;
        Assert.Equal(ActivityStatus.NoShow, noShow.Status);

        // Bu aileler bloke olamaz; görevin sonucu olmaz; e-posta elle "gönderildi" kaydedilir (teslim anlamına gelmez).
        await ProblemAsync(await ActionAsync(client, call.Id, "block", new ActivityReasonRequest(reached.Version, "x")), HttpStatusCode.UnprocessableEntity);
        var task = await CreateAsync(client, new SaveActivityRequest("Görev"));
        await ProblemAsync(await ActionAsync(client, task.Id, "complete", new CompleteActivityRequest(task.Version, ActivityOutcome.Reached)), HttpStatusCode.UnprocessableEntity);
        var email = await CreateAsync(client, new SaveActivityRequest("Teklif e-postası", ActivityType.Email));
        Assert.Equal(ActivityStatus.Draft, email.Status);
        var sent = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, email.Id, "complete", new CompleteActivityRequest(email.Version)), HttpStatusCode.OK)).Activity;
        Assert.Equal(ActivityStatus.Sent, sent.Status);
        Assert.Equal(ActivityOutcome.Sent, sent.Outcome);
        await ProblemAsync(await ActionAsync(client, email.Id, "reopen", new ActivityReasonRequest(sent.Version, "geri al")), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_past_interaction_can_be_logged_as_already_completed()
    {
        var (_, _, client) = await StartAsync();
        var when = DateTimeOffset.UtcNow.AddDays(-3);
        var logged = await CreateAsync(client, new SaveActivityRequest("Dünkü görüşme", ActivityType.Call, when, when, Outcome: ActivityOutcome.Reached));
        Assert.Equal(ActivityStatus.Completed, logged.Status);
        Assert.Equal(when, logged.CompletedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.False(logged.IsOverdue(DateTimeOffset.UtcNow));

        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest(
            "Gelecek", ActivityType.Call, CompletedAt: DateTimeOffset.UtcNow.AddDays(2))), HttpStatusCode.UnprocessableEntity);
    }

    // ---- 3. Sonraki adım, devir ve eşzamanlılık

    [Fact]
    public async Task Completing_with_a_next_step_creates_a_linked_follow_up_in_one_transaction()
    {
        var (_, user, client) = await StartAsync();
        var company = await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Sonraki Ltd.")), HttpStatusCode.Created);
        var call = await CreateAsync(client, new SaveActivityRequest("İlk arama", ActivityType.Call, DateTimeOffset.UtcNow, CompanyId: company.Id));
        await ReadAsync<ActivityLinkDto>(await client.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(call.Id, CrmModule.Entities.Company, company.Id, IsPrimary: true)), HttpStatusCode.Created);

        var next = DateTimeOffset.UtcNow.AddDays(2);
        var result = await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, call.Id, "complete",
            new CompleteActivityRequest(call.Version, NextStep: new ActivityNextStepRequest("Teklif e-postası gönder", ActivityType.Email, next))), HttpStatusCode.OK);

        Assert.Equal(ActivityStatus.Completed, result.Activity.Status);
        var step = result.NextStep!;
        Assert.Equal(call.Id, step.PreviousActivityId);
        Assert.Equal(company.Id, step.CompanyId);
        Assert.Equal(ActivityStatus.Draft, step.Status);
        Assert.Equal(user.Id, step.OwnerId);
        var links = await client.GetFromJsonAsync<CrmPage<ActivityLinkDto>>($"/api/crm/activity-links?activityId={step.Id}");
        Assert.Single(links!.Items);
        Assert.Equal([step.Id], (await ListAsync(client, $"&previousActivityId={call.Id}")).Select(a => a.Id));

        // Sonraki adım geçersizse tamamlama da geri alınır (hepsi ya da hiçbiri).
        var other = await CreateAsync(client, new SaveActivityRequest("İkinci"));
        await ProblemAsync(await ActionAsync(client, other.Id, "complete",
            new CompleteActivityRequest(other.Version, NextStep: new ActivityNextStepRequest("Konu", "YokBoyleTur"))), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ActivityStatus.Open, (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{other.Id}"))!.Status);
    }

    [Fact]
    public async Task Concurrent_edits_never_silently_overwrite_each_other()
    {
        var (tenant, _, client) = await StartAsync();
        var (_, second) = await AddUserAsync(tenant, Everything);
        var activity = await CreateAsync(client, new SaveActivityRequest("Ortak iş"));

        var first = await ReadAsync<ActivityDto>(await ActionAsync(client, activity.Id, "start", new ActivityVersionRequest(activity.Version)), HttpStatusCode.OK);
        // İkinci kullanıcı eski sürümle gelir: tamamlama da düz güncelleme de 409 alır.
        await ProblemAsync(await ActionAsync(second, activity.Id, "complete", new CompleteActivityRequest(activity.Version)), HttpStatusCode.Conflict);
        await ProblemAsync(await second.PutAsJsonAsync($"/api/crm/activities/{activity.Id}",
            new SaveActivityRequest("Ezilen başlık", Version: activity.Version)), HttpStatusCode.Conflict);
        var current = (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{activity.Id}"))!;
        Assert.Equal("Ortak iş", current.Subject);
        Assert.Equal(ActivityStatus.InProgress, current.Status);
        Assert.Equal(first.Version, current.Version);
    }

    [Fact]
    public async Task Reassigning_to_someone_else_needs_the_assign_permission()
    {
        var (tenant, user, client) = await StartAsync();
        var plain = new[] { ActivitiesRead, ActivitiesWrite };
        var (rep, repClient) = await AddUserAsync(tenant, plain);
        var activity = await CreateAsync(repClient, new SaveActivityRequest("Benim işim"));
        Assert.Equal(rep.Id, activity.OwnerId);

        // Düz güncelleme sorumluyu değiştiremez; yetkisiz kullanıcı başkasına atayamaz.
        var viaPut = await ReadAsync<ActivityDto>(await repClient.PutAsJsonAsync($"/api/crm/activities/{activity.Id}",
            new SaveActivityRequest("Benim işim", OwnerId: user.Id, Version: activity.Version)), HttpStatusCode.OK);
        Assert.Equal(rep.Id, viaPut.OwnerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActionAsync(repClient, activity.Id, "reassign", new ReassignActivityRequest(viaPut.Version, user.Id))).StatusCode);

        // Yetkili atar; geçmişte eski ve yeni sorumlu durur.
        var assigned = await ReadAsync<ActivityDto>(await ActionAsync(client, activity.Id, "reassign", new ReassignActivityRequest(viaPut.Version, user.Id)), HttpStatusCode.OK);
        Assert.Equal(user.Id, assigned.OwnerId);
        var history = await client.GetStringAsync($"/api/records/{CrmModule.Entities.Activity}/{activity.Id}/history");
        Assert.Contains("ownerId", history);

        // Aynı kişiye tekrar atama değişiklik üretmez.
        var again = await ReadAsync<ActivityDto>(await ActionAsync(client, activity.Id, "reassign", new ReassignActivityRequest(assigned.Version, user.Id)), HttpStatusCode.OK);
        Assert.Equal(assigned.Version, again.Version);
    }

    // ---- 4. Erteleme, çakışma, takvim

    [Fact]
    public async Task Rescheduling_keeps_history_counts_postponements_and_detects_overlaps()
    {
        var (_, _, client) = await StartAsync();
        var nine = new DateTimeOffset(2026, 12, 7, 6, 0, 0, TimeSpan.Zero);
        var a = await CreateAsync(client, new SaveActivityRequest("Toplantı A", ActivityType.Meeting, StartAt: nine, EndAt: nine.AddHours(1), TimeZone: Zone));
        var b = await CreateAsync(client, new SaveActivityRequest("Toplantı B", ActivityType.Meeting, StartAt: nine.AddHours(3), EndAt: nine.AddHours(4), TimeZone: Zone));
        Assert.Equal(0, b.RescheduleCount);

        // Gerekçe zorunlu; çakışma varsa reddedilir, bilinçli onayla planlanır.
        await ProblemAsync(await ActionAsync(client, b.Id, "reschedule", new RescheduleActivityRequest(b.Version, null, StartAt: nine, EndAt: nine.AddHours(1))), HttpStatusCode.UnprocessableEntity);
        var conflict = await ProblemAsync(await ActionAsync(client, b.Id, "reschedule",
            new RescheduleActivityRequest(b.Version, "Öne alındı", StartAt: nine.AddMinutes(30), EndAt: nine.AddMinutes(90))), HttpStatusCode.UnprocessableEntity);
        Assert.Contains("conflict", conflict["errors"]!.AsObject().Select(p => p.Key));
        var moved = await ReadAsync<ActivityDto>(await ActionAsync(client, b.Id, "reschedule",
            new RescheduleActivityRequest(b.Version, "Öne alındı", StartAt: nine.AddMinutes(30), EndAt: nine.AddMinutes(90), AllowConflict: true)), HttpStatusCode.OK);
        Assert.Equal(1, moved.RescheduleCount);
        Assert.Equal(nine.AddMinutes(30), moved.StartAt!.Value);

        // Arka arkaya bloklar çakışma sayılmaz (yarı açık aralık).
        var back = await ReadAsync<ActivityDto>(await ActionAsync(client, b.Id, "reschedule",
            new RescheduleActivityRequest(moved.Version, "Sonraya", StartAt: nine.AddHours(1), EndAt: nine.AddHours(2))), HttpStatusCode.OK);
        Assert.Equal(2, back.RescheduleCount);

        // Kapanmış aktivite ertelenmez ve düz güncellemeyle de zamanı değişmez.
        var closed = (await ReadAsync<CompleteActivityResultDto>(await ActionAsync(client, a.Id, "complete", new CompleteActivityRequest(a.Version)), HttpStatusCode.OK)).Activity;
        await ProblemAsync(await ActionAsync(client, a.Id, "reschedule", new RescheduleActivityRequest(closed.Version, "Geç", StartAt: nine.AddDays(1), EndAt: nine.AddDays(1).AddHours(1))), HttpStatusCode.UnprocessableEntity);
        await ProblemAsync(await client.PutAsJsonAsync($"/api/crm/activities/{a.Id}", new SaveActivityRequest(
            "Toplantı A", ActivityType.Meeting, StartAt: nine.AddDays(1), EndAt: nine.AddDays(1).AddHours(1), TimeZone: Zone, Version: closed.Version)), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Calendar_window_returns_timed_all_day_and_deadline_items_without_shifting_dates()
    {
        var (_, _, client) = await StartAsync();
        var day = new DateOnly(2027, 3, 28);
        var allDay = await CreateAsync(client, new SaveActivityRequest("Fuar", ActivityType.Meeting, IsAllDay: true, StartDate: day, EndDate: day.AddDays(2)));
        // Tüm gün etkinliğinin günleri saat dilimi dönüşümüyle kaymaz.
        Assert.Equal(day, allDay.StartDate);
        Assert.Equal(day.AddDays(2), allDay.EndDate);
        Assert.Null(allDay.StartAt);

        var timed = await CreateAsync(client, new SaveActivityRequest("Sunum", ActivityType.Meeting,
            StartAt: new DateTimeOffset(2027, 3, 29, 7, 0, 0, TimeSpan.Zero), EndAt: new DateTimeOffset(2027, 3, 29, 8, 0, 0, TimeSpan.Zero), TimeZone: Zone));
        var deadline = await CreateAsync(client, new SaveActivityRequest("Rapor", DueAt: new DateTimeOffset(2027, 3, 30, 12, 0, 0, TimeSpan.Zero)));
        var outside = await CreateAsync(client, new SaveActivityRequest("Uzak", DueAt: new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero)));

        string Range(string from, string to) => "&range=" + Uri.EscapeDataString($"{from}T00:00:00Z|{to}T00:00:00Z|{from}|{to}");
        var week = await ListAsync(client, Range("2027-03-29", "2027-04-04"));
        Assert.Contains(week, a => a.Id == allDay.Id);
        Assert.Contains(week, a => a.Id == timed.Id);
        Assert.Contains(week, a => a.Id == deadline.Id);
        Assert.DoesNotContain(week, a => a.Id == outside.Id);

        // Gün sınırında biten blok bir sonraki günü kapsamaz; sorgu penceresi sınırlıdır.
        Assert.DoesNotContain(await ListAsync(client, Range("2027-03-30", "2027-03-31")), a => a.Id == timed.Id);
        await ProblemAsync(await client.GetAsync("/api/crm/activities?" + Range("2027-01-01", "2027-12-31").TrimStart('&')), HttpStatusCode.UnprocessableEntity);

        // Saatli etkinlik saat dilimi olmadan, tüm gün etkinliği saatle kaydedilemez.
        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest(
            "Saat dilimsiz", StartAt: DateTimeOffset.UtcNow.AddDays(1), EndAt: DateTimeOffset.UtcNow.AddDays(1).AddHours(1))), HttpStatusCode.UnprocessableEntity);
        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest(
            "Karışık", IsAllDay: true, StartDate: day, StartAt: DateTimeOffset.UtcNow.AddDays(1), TimeZone: Zone)), HttpStatusCode.UnprocessableEntity);
    }

    // ---- 5. Özel aktivite

    [Fact]
    public async Task A_private_activity_shows_only_busy_time_to_users_without_access()
    {
        var (tenant, owner, ownerClient) = await StartAsync();
        var (_, other) = await AddUserAsync(tenant, [ActivitiesRead, ActivitiesWrite, CompaniesRead, ActivitiesManageCalendar]);
        var (_, privileged) = await AddUserAsync(tenant, [ActivitiesRead, ActivitiesViewPrivate, CompaniesRead]);
        var (participantUser, participant) = await AddUserAsync(tenant, [ActivitiesRead], RecordScope.Own);
        var company = await ReadAsync<CompanyDto>(await ownerClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Sır A.Ş.")), HttpStatusCode.Created);
        var start = new DateTimeOffset(2027, 1, 12, 7, 0, 0, TimeSpan.Zero);
        var secret = await CreateAsync(ownerClient, new SaveActivityRequest(
            "Satın alma görüşmesi: Sır A.Ş.", ActivityType.Meeting, StartAt: start, EndAt: start.AddHours(1), TimeZone: Zone,
            CompanyId: company.Id, Notes: "iskonto %30", Location: "Gizli oda", IsPrivate: true));

        // Ayrıntıyı göremeyen kapsamdaki kullanıcı yalnızca meşguliyet görür.
        var busy = (await other.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{secret.Id}"))!;
        Assert.True(busy.Masked);
        Assert.Equal("Meşgul", busy.Subject);
        Assert.Null(busy.Notes);
        Assert.Null(busy.CompanyId);
        Assert.Null(busy.Location);
        Assert.Equal(start, busy.StartAt!.Value);
        var listed = Assert.Single(await ListAsync(other, "&range=" + Uri.EscapeDataString("2027-01-12T00:00:00Z|2027-01-13T00:00:00Z|2027-01-12|2027-01-12")));
        Assert.Equal("Meşgul", listed.Subject);

        // Arama, ilişki süzgeci, ilişkili kayıt listesi ve yazma yolları özel ayrıntıyı ele vermez.
        Assert.Empty(await ListAsync(other, "&search=Sır"));
        Assert.Empty(await ListAsync(other, $"&companyId={company.Id}"));
        Assert.Empty(await ListAsync(other, $"&timeline={Uri.EscapeDataString(CrmModule.Entities.Company + ":" + company.Id)}"));
        var related = (await other.GetFromJsonAsync<ActivityRelatedDto>($"/api/crm/activities/{secret.Id}/related"))!;
        Assert.Empty(related.Links);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/crm/activities/{secret.Id}", new SaveActivityRequest("Okundu", Version: secret.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ActionAsync(other, secret.Id, "cancel", new ActivityReasonRequest(secret.Version, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/crm/activities/{secret.Id}?version={secret.Version}")).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<CrmPage<ActivityLinkDto>>($"/api/crm/activity-links?activityId={secret.Id}"))!.Items);

        // Sahibi, özel izin sahibi ve katılımcı ayrıntıyı görür.
        Assert.Equal("Satın alma görüşmesi: Sır A.Ş.", (await privileged.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{secret.Id}"))!.Subject);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync($"/api/crm/activities/{secret.Id}")).StatusCode);
        await ReadAsync<ActivityParticipantDto>(await ownerClient.PostAsJsonAsync("/api/crm/activity-participants",
            new SaveActivityParticipantRequest(secret.Id, UserId: participantUser.Id)), HttpStatusCode.Created);
        var seen = (await participant.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{secret.Id}"))!;
        Assert.False(seen.Masked);
        Assert.Equal("iskonto %30", seen.Notes);
        Assert.Equal(owner.Id, seen.OwnerId);
    }

    // ---- 6. İlişkiler ve zaman akışı

    [Fact]
    public async Task Links_are_validated_against_the_callers_access_and_never_duplicate_the_timeline()
    {
        var (tenant, _, admin) = await StartAsync();
        var (rep, repClient) = await AddUserAsync(tenant, [CompaniesRead, CompaniesWrite, ContactsRead, OpportunitiesRead, ActivitiesRead, ActivitiesWrite], RecordScope.Own);
        var visible = await ReadAsync<CompanyDto>(await repClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Benim Ltd.")), HttpStatusCode.Created);
        var hidden = await ReadAsync<CompanyDto>(await admin.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Başkasının Ltd.")), HttpStatusCode.Created);
        var opportunity = await ReadAsync<OpportunityDto>(await admin.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Gizli fırsat", hidden.Id, Notes: "gizli not")), HttpStatusCode.Created);

        // Kapsam dışındaki kayıt bağlanamaz (doğrudan ya da ilişki satırıyla); hata "bulunamadı" der.
        var denied = await ProblemAsync(await repClient.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Bağ", CompanyId: hidden.Id)), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("Firma bulunamadı.", denied["errors"]!["companyId"]![0]!.GetValue<string>());
        var activity = await CreateAsync(repClient, new SaveActivityRequest("Görüşme", ActivityType.Call, CompanyId: visible.Id));
        await ProblemAsync(await repClient.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(activity.Id, CrmModule.Entities.Opportunity, opportunity.Id)), HttpStatusCode.UnprocessableEntity);
        await ProblemAsync(await repClient.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(activity.Id, "yok.tur", Guid.NewGuid())), HttpStatusCode.UnprocessableEntity);

        // Aynı firma hem doğrudan hem ilişki satırıyla bağlı olsa da zaman akışında bir kez görünür; ikinci birincil ilişki reddedilir.
        var link = await ReadAsync<ActivityLinkDto>(await repClient.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(activity.Id, CrmModule.Entities.Company, visible.Id, IsPrimary: true)), HttpStatusCode.Created);
        await ProblemAsync(await repClient.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(activity.Id, CrmModule.Entities.Company, visible.Id)), HttpStatusCode.UnprocessableEntity);
        var timeline = await ListAsync(repClient, $"&timeline={Uri.EscapeDataString(CrmModule.Entities.Company + ":" + visible.Id)}");
        Assert.Single(timeline);
        Assert.Equal(1, (await repClient.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?related={Uri.EscapeDataString(CrmModule.Entities.Company + ":" + visible.Id)}"))!.TotalCount);

        // Yönetici fırsatı aynı aktiviteye bağlar; temsilci aktiviteyi görür ama fırsatın adını/tutarını görmez.
        await ReadAsync<ActivityLinkDto>(await admin.PostAsJsonAsync("/api/crm/activity-links",
            new SaveActivityLinkRequest(activity.Id, CrmModule.Entities.Opportunity, opportunity.Id)), HttpStatusCode.Created);
        var asRep = (await repClient.GetFromJsonAsync<ActivityRelatedDto>($"/api/crm/activities/{activity.Id}/related"))!;
        Assert.Equal(1, asRep.HiddenCount);
        Assert.DoesNotContain(asRep.Links, l => l.EntityKey == CrmModule.Entities.Opportunity);
        Assert.DoesNotContain("Gizli fırsat", System.Text.Json.JsonSerializer.Serialize(asRep));
        var asAdmin = (await admin.GetFromJsonAsync<ActivityRelatedDto>($"/api/crm/activities/{activity.Id}/related"))!;
        Assert.Contains(asAdmin.Links, l => l.Title == "Gizli fırsat");

        // İlişki silinince aktivite ve geçmişi korunur.
        Assert.Equal(HttpStatusCode.NoContent, (await repClient.DeleteAsync($"/api/crm/activity-links/{link.Id}?version={link.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await repClient.GetAsync($"/api/crm/activities/{activity.Id}")).StatusCode);
        Assert.Equal(rep.Id, activity.OwnerId);
    }

    [Fact]
    public async Task External_participants_are_tracked_without_gaining_any_crm_access()
    {
        var (_, _, client) = await StartAsync();
        var activity = await CreateAsync(client, new SaveActivityRequest("Müşteri toplantısı", ActivityType.Meeting));
        var guest = await ReadAsync<ActivityParticipantDto>(await client.PostAsJsonAsync("/api/crm/activity-participants",
            new SaveActivityParticipantRequest(activity.Id, Email: "Misafir@Ornek.test", DisplayName: "Misafir", Role: "Optional")), HttpStatusCode.Created);
        Assert.Equal("Optional", guest.Role);

        // Aynı kişi (e-posta büyük/küçük harfe duyarsız) iki kez eklenmez; kimlik türleri karıştırılamaz.
        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activity-participants",
            new SaveActivityParticipantRequest(activity.Id, Email: "misafir@ornek.test")), HttpStatusCode.UnprocessableEntity);
        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activity-participants",
            new SaveActivityParticipantRequest(activity.Id, UserId: Guid.NewGuid(), Email: "x@ornek.test")), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1, (await client.GetFromJsonAsync<CrmPage<ActivityParticipantDto>>($"/api/crm/activity-participants?activityId={activity.Id}"))!.TotalCount);
    }

    // ---- 7. Toplu işlem

    [Fact]
    public async Task Bulk_actions_report_partial_success_per_record()
    {
        var (tenant, _, client) = await StartAsync();
        var (_, readOnly) = await AddUserAsync(tenant, [ActivitiesRead]);
        var one = await CreateAsync(client, new SaveActivityRequest("Bir"));
        var two = await CreateAsync(client, new SaveActivityRequest("İki"));
        var three = await CreateAsync(client, new SaveActivityRequest("Üç"));

        var result = await ReadAsync<BulkActivityResultDto>(await client.PostAsJsonAsync("/api/crm/activities/bulk", new BulkActivityRequest(
            "complete", [new BulkActivityItem(one.Id, one.Version), new BulkActivityItem(two.Id, two.Version + 5), new BulkActivityItem(Guid.NewGuid(), 1)])),
            HttpStatusCode.OK);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Contains(result.Failures, f => f.Id == two.Id);
        Assert.Equal(ActivityStatus.Completed, (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{one.Id}"))!.Status);
        Assert.Equal(ActivityStatus.Open, (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{two.Id}"))!.Status);

        // Yetkisiz kullanıcı için her kayıt ayrı ayrı reddedilir; hiçbiri değişmez.
        var denied = await ReadAsync<BulkActivityResultDto>(await readOnly.PostAsJsonAsync("/api/crm/activities/bulk", new BulkActivityRequest(
            "cancel", [new BulkActivityItem(three.Id, three.Version)], Reason: "Toplu")), HttpStatusCode.OK);
        Assert.Equal(0, denied.Succeeded);
        Assert.Equal(ActivityStatus.Open, (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{three.Id}"))!.Status);

        var shifted = await ReadAsync<BulkActivityResultDto>(await client.PostAsJsonAsync("/api/crm/activities/bulk", new BulkActivityRequest(
            "reschedule-by-days", [new BulkActivityItem(three.Id, three.Version)], Reason: "Tatil", Days: 3)), HttpStatusCode.OK);
        Assert.Equal(1, shifted.Succeeded);
        await ProblemAsync(await client.PostAsJsonAsync("/api/crm/activities/bulk", new BulkActivityRequest("complete", [])), HttpStatusCode.BadRequest);
    }
}
