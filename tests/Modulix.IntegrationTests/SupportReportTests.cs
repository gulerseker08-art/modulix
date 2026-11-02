using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Shared.Auth;
using Modulix.Shared.Settings;
using Modulix.Shared.Support;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 6: raporlar (hacim, birikim yaşı, süreler, SLA uyumu, yeniden açılma, memnuniyet) ve şikâyet inceleme (tekrarlayan neden, düzeltici aksiyon). Veriler sabit zamanlarla
/// kurulur (talep ve SLA zamanları SQL ile geriye alınır); her sayı kırılım süzgeciyle listeye açılıp aynı sayıyı vermelidir. Yetki, veri kapsamı ve tenant izolasyonu sınanır.
/// </summary>
public class SupportReportTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];
    private static readonly string[] LeadPermissions = [.. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel];
    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage, SlaManage, ReportsRead, ComplaintsManage, "core.settings.manage"];

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public SupportReportTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        host = factory.WithWebHostBuilder(builder => builder.UseSetting("App:PublicUrl", "https://app.modulix.test"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    private sealed record Setup(Tenant Tenant, Guid PartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Reporter, ModulixUser Viewer);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var lead = await factory.CreateUserAsync(tenant);
        var reporter = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ManagerPermissions, Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, LeadPermissions, Scope.All, lead);
        await factory.CreateRoleAsync(tenant.Id, [.. AgentPermissions, ReportsRead], Scope.Own, reporter);
        await factory.CreateRoleAsync(tenant.Id, [TicketsRead], Scope.All, viewer);

        Guid partyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Rapor Müşterisi A.Ş.", Email = "rapor@musteri.example.test" };
            db.Set<Party>().Add(party);
            await db.SaveChangesAsync();
            partyId = party.Id;
        }

        return new Setup(tenant, partyId, manager, lead, reporter, viewer);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private HttpClient Anonymous() => host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private static async Task<T> CreateAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<TicketDto> TicketAsync(HttpClient client, Setup s, string subject, string? priority = null, string? typeKey = null, string? email = null) =>
        CreateAsync<TicketDto>(client, "tickets", new SaveTicketRequest(Subject: subject, PartyId: s.PartyId, TypeKey: typeKey, ReporterEmail: email,
            Impact: priority == "High" ? "High" : null, Urgency: priority == "High" ? "High" : null));

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static async Task<TicketDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> ActAsync(HttpClient client, Guid id, string action, string? reason = null, string? rootCause = null, string? corrective = null, bool resolve = false) =>
        await OkAsync(await client.PostAsJsonAsync($"/api/support/tickets/{id}/{action}", new TicketActionRequest(
            (await GetAsync(client, id)).Version, reason, ResolutionCode: resolve ? "Resolved" : null, ResolutionNote: resolve ? "Çözüldü." : null, RootCause: rootCause, CorrectiveAction: corrective)));

    private static async Task<TicketDto> WorkAsync(HttpClient client, TicketDto t, bool reply = true)
    {
        await ActAsync(client, t.Id, "take");
        await ActAsync(client, t.Id, "start");
        if (reply)
        {
            var response = await client.PostAsJsonAsync($"/api/support/tickets/{t.Id}/messages", new AddTicketMessageRequest("reply", "Dönüş yapıldı."));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return await GetAsync(client, t.Id);
    }

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1, sql);
    }

    /// <summary>Talebin açılış, ilk yanıt ve çözüm anlarını sabitler (kopya kolonlar rapor kaynağıdır).</summary>
    private Task SetTimesAsync(Guid tenantId, Guid ticketId, DateTimeOffset created, int? firstResponseMinutes, int? resolvedMinutes)
    {
        var sql = "UPDATE support.Tickets SET CreatedAt = {1}";
        var args = new List<object> { ticketId, created };
        if (firstResponseMinutes is { } fr) { sql += $", FirstResponseAt = {{{args.Count}}}"; args.Add(created.AddMinutes(fr)); }
        if (resolvedMinutes is { } rs) { sql += $", ResolvedAt = {{{args.Count}}}"; args.Add(created.AddMinutes(rs)); }
        return SqlAsync(tenantId, sql + " WHERE Id = {0}", [.. args]);
    }

    private Task SetInstanceAsync(Guid tenantId, Guid ticketId, string metric, int cycle, long workedSeconds, bool breached) =>
        SqlAsync(tenantId, "UPDATE support.SlaInstances SET WorkedSeconds = {2}, BreachedAt = " + (breached ? "SYSDATETIMEOFFSET()" : "NULL") + " WHERE TicketId = {0} AND Metric = {1} AND Cycle = {3}",
            ticketId, metric, workedSeconds, cycle);

    private static async Task<ReportOverviewDto> OverviewAsync(HttpClient client, string query = "", HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync($"/api/support/reports/overview?{query}");
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<ReportOverviewDto>())! : null!;
    }

    private static async Task<int> ListCountAsync(HttpClient client, string drillDown) =>
        (await client.GetFromJsonAsync<PagedResultDto>($"/api/support/tickets?{drillDown}&pageSize=1"))!.TotalCount;

    private sealed record PagedResultDto(int TotalCount);

    private static string Day(DateTimeOffset moment) => ReportPeriod.LocalDate(moment).ToString("yyyy-MM-dd");

    // ---- Genel bakış

    [Fact]
    public async Task The_overview_computes_volume_backlog_times_sla_compliance_and_reopen_from_real_tickets_and_every_number_opens_the_same_list()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        var calendar = await CreateAsync<SlaCalendarDto>(manager, "sla-calendars", new SaveSlaCalendarRequest(Name: "7/24", IsAlwaysOpen: true));
        await CreateAsync<SlaPolicyDto>(manager, "sla-policies", new SaveSlaPolicyRequest(Name: "Varsayılan", IsDefault: true, FirstResponseMinutes: 60, ResolutionMinutes: 480, CalendarId: calendar.Id));
        var now = DateTimeOffset.UtcNow;

        // T1/T2/T5 çözülür (T5 bir kez yeniden açılıp tekrar çözülür), T3 yanıtsız açık, T4 yeni açık.
        var t1 = await WorkAsync(lead, await TicketAsync(lead, s, "T1 hızlı çözüm"));
        await ActAsync(lead, t1.Id, "resolve", resolve: true);
        var t2 = await WorkAsync(lead, await TicketAsync(lead, s, "T2 geç yanıt"));
        await ActAsync(lead, t2.Id, "resolve", resolve: true);
        var t3 = await TicketAsync(lead, s, "T3 yanıtsız açık", priority: "High");
        var t4 = await TicketAsync(lead, s, "T4 yeni");
        var t5 = await WorkAsync(lead, await TicketAsync(lead, s, "T5 yeniden açılan"));
        await ActAsync(lead, t5.Id, "resolve", resolve: true);
        await ActAsync(lead, t5.Id, "reopen", reason: "Tekrar bozuldu");
        await ActAsync(lead, t5.Id, "start");
        await ActAsync(lead, t5.Id, "resolve", resolve: true);

        await SetTimesAsync(s.Tenant.Id, t1.Id, now.AddDays(-5), 30, 240);
        await SetTimesAsync(s.Tenant.Id, t2.Id, now.AddDays(-4), 90, 300);
        await SetTimesAsync(s.Tenant.Id, t3.Id, now.AddDays(-10), null, null);
        await SetTimesAsync(s.Tenant.Id, t4.Id, now.AddHours(-2), null, null);
        await SetTimesAsync(s.Tenant.Id, t5.Id, now.AddDays(-3), 60, 180);
        // İş süreleri (SLA örneği): ilk yanıt 30/90/60 dk, çözüm 200/250/100 dk; T2'nin ilk yanıtı hedefi aşmış, T3'ünki açıkken aşmış.
        await SetInstanceAsync(s.Tenant.Id, t1.Id, "FirstResponse", 1, 1800, false);
        await SetInstanceAsync(s.Tenant.Id, t2.Id, "FirstResponse", 1, 5400, true);
        await SetInstanceAsync(s.Tenant.Id, t5.Id, "FirstResponse", 1, 3600, false);
        await SetInstanceAsync(s.Tenant.Id, t3.Id, "FirstResponse", 1, 0, true);
        await SetInstanceAsync(s.Tenant.Id, t1.Id, "Resolution", 1, 12000, false);
        await SetInstanceAsync(s.Tenant.Id, t2.Id, "Resolution", 1, 15000, false);
        await SetInstanceAsync(s.Tenant.Id, t5.Id, "Resolution", 2, 6000, false);

        var from = Day(now.AddDays(-9));
        var to = Day(now);
        var report = await OverviewAsync(manager, $"from={from}&to={to}");

        // Hacim: dönemde açılan 4 talep (T3, 10 gün önce, dönem dışıdır); seri dönem boyunca eksiksizdir ve toplamı tutar.
        Assert.Equal(4, report.Meta.TicketCount);
        Assert.False(report.Meta.Truncated);
        Assert.Equal("Europe/Istanbul", report.Meta.TimeZone);
        Assert.Equal(4, report.Volume.Total);
        Assert.Equal(4, report.Volume.Series.Sum(p => p.Created));
        Assert.Equal(10, report.Volume.Series.Count);
        Assert.Equal(1, report.Volume.Series.Single(p => p.Bucket == ReportPeriod.LocalDate(now.AddDays(-5))).Created);
        Assert.Equal(4, report.Volume.ByStatus.Sum(c => c.Count));
        Assert.Equal(4, await ListCountAsync(lead, report.Volume.DrillDown));
        foreach (var item in report.Volume.ByStatus.Concat(report.Volume.ByType).Concat(report.Volume.ByChannel).Concat(report.Volume.ByPriority))
        {
            Assert.Equal(item.Count, await ListCountAsync(lead, item.DrillDown));
        }

        // Birikim: şu an açık 2 talep (T3 10 gün, T4 2 saat); T3 de dönem dışı olsa birikimde görünür.
        Assert.Equal(2, report.Backlog.Open);
        Assert.Equal(2, report.Backlog.Unassigned);
        Assert.InRange(report.Backlog.OldestDays!.Value, 9.9, 10.1);
        Assert.Equal(1, report.Backlog.Buckets.Single(b => b.Key == "d7-14").Count);
        Assert.Equal(1, report.Backlog.Buckets.Single(b => b.Key == "lt1").Count);
        Assert.Equal(report.Backlog.Open, report.Backlog.Buckets.Sum(b => b.Count));
        foreach (var bucket in report.Backlog.Buckets)
        {
            Assert.Equal(bucket.Count, await ListCountAsync(lead, bucket.DrillDown));
        }

        // Süreler (dakika): takvim ve iş süresi ayrı kaynaktır; örneklem büyüklükleri ve ulaşmayanlar dürüstçe döner.
        Assert.Equal((3, 60d, 60d, 90d), (report.Times.FirstResponseCalendar.Count, report.Times.FirstResponseCalendar.Mean, report.Times.FirstResponseCalendar.Median, report.Times.FirstResponseCalendar.P90));
        Assert.Equal((3, 60d, 90d), (report.Times.FirstResponseBusiness.Count, report.Times.FirstResponseBusiness.Median, report.Times.FirstResponseBusiness.Max));
        Assert.Equal((3, 240d, 300d), (report.Times.ResolutionCalendar.Count, report.Times.ResolutionCalendar.Median, report.Times.ResolutionCalendar.Max));
        Assert.Equal((3, 200d, 100d, 250d), (report.Times.ResolutionBusiness.Count, report.Times.ResolutionBusiness.Median, report.Times.ResolutionBusiness.Min, report.Times.ResolutionBusiness.Max));
        Assert.Equal((1, 1), (report.Times.NotYetResponded, report.Times.NotYetResolved));

        // SLA uyumu: pay hedefinde tamamlananlar, payda sonuçlanmışlar (devam eden ve iptal paydada yok).
        var first = report.Sla.Single(x => x.Metric == "FirstResponse");
        Assert.Equal((2, 1, 0, 1, 0, 3, 66.7), (first.Counts.Met, first.Counts.Missed, first.Counts.Breached, first.Counts.InProgress, first.Counts.Cancelled, first.Counts.Decided, first.CompliancePercent));
        var resolution = report.Sla.Single(x => x.Metric == "Resolution");
        Assert.Equal((4, 0, 0, 1, 4, 100d), (resolution.Counts.Met, resolution.Counts.Missed, resolution.Counts.Breached, resolution.Counts.InProgress, resolution.Counts.Decided, resolution.CompliancePercent));

        // Yeniden açılma: dönemde çözülen 3 talepten 1'i yeniden açıldı.
        Assert.Equal((3, 1, 33.3, 1), (report.Reopen.Resolved, report.Reopen.Reopened, report.Reopen.RatePercent, report.Reopen.TotalReopens));
        Assert.Equal(report.Reopen.Reopened, await ListCountAsync(lead, report.Reopen.DrillDown));

        // Süzgeç: kritik öncelik yalnızca T3'tür (dönem dışı); dönem genişletilince görünür.
        var wide = await OverviewAsync(manager, $"from={Day(now.AddDays(-30))}&to={to}&priority=Critical");
        Assert.Equal(1, wide.Meta.TicketCount);
        Assert.Equal(1, wide.Backlog.Open);
        var narrow = await OverviewAsync(manager, $"from={from}&to={to}&priority=Critical");
        Assert.Equal(0, narrow.Meta.TicketCount);
        Assert.Null(narrow.Times.FirstResponseCalendar.Mean);
        Assert.Null(narrow.Reopen.RatePercent);
        Assert.Null(narrow.Sla.Single(x => x.Metric == "Resolution").CompliancePercent);

        // Haftalık ve aylık gruplama toplamı korur.
        Assert.Equal(4, (await OverviewAsync(manager, $"from={from}&to={to}&granularity=week")).Volume.Series.Sum(p => p.Created));
        Assert.Equal(4, (await OverviewAsync(manager, $"from={from}&to={to}&granularity=month")).Volume.Series.Sum(p => p.Created));
    }

    [Fact]
    public async Task Reports_need_their_permission_respect_the_data_scope_validate_input_and_stay_inside_the_tenant()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var reporter = await LoginAsync(s.Reporter);
        using var viewer = await LoginAsync(s.Viewer);
        using var foreign = await LoginAsync(other.Manager);
        using var foreignLead = await LoginAsync(other.Lead);

        await WorkAsync(lead, await TicketAsync(lead, s, "Başkasının talebi"), reply: false);
        await TicketAsync(lead, s, "Atanmamış talep");
        await WorkAsync(reporter, await TicketAsync(reporter, s, "Raportörün talebi"), reply: false);
        await TicketAsync(foreignLead, other, "Başka tenant talebi 1");
        await TicketAsync(foreignLead, other, "Başka tenant talebi 2");
        await TicketAsync(foreignLead, other, "Başka tenant talebi 3");

        // Yetki: rapor izni olmayan 403 (liste okuyabilse de), oturumsuz 401.
        await OverviewAsync(lead, expected: HttpStatusCode.Forbidden);
        await OverviewAsync(viewer, expected: HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync("/api/support/reports/complaints")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/support/reports/overview")).StatusCode);

        // Veri kapsamı: "kendi" kapsamlı kullanıcının raporu, liste ekranının gördüğü kadardır (kendi + atanmamış), yöneticinin raporu hepsini kapsar; başka tenant görünmez.
        var today = Day(DateTimeOffset.UtcNow);
        var all = await OverviewAsync(manager, $"from={today}&to={today}");
        var scoped = await OverviewAsync(reporter, $"from={today}&to={today}");
        Assert.Equal(3, all.Meta.TicketCount);
        Assert.Equal(2, scoped.Meta.TicketCount);
        Assert.Equal(scoped.Meta.TicketCount, await ListCountAsync(reporter, scoped.Volume.DrillDown));
        Assert.Equal(3, (await OverviewAsync(foreign, $"from={today}&to={today}")).Meta.TicketCount);
        Assert.Equal(all.Meta.TicketCount, await ListCountAsync(manager, all.Volume.DrillDown));

        // Girdi doğrulaması.
        await OverviewAsync(manager, $"from={today}&to=2999-01-01", HttpStatusCode.UnprocessableEntity);
        await OverviewAsync(manager, "from=2026-10-10&to=2026-10-01", HttpStatusCode.UnprocessableEntity);
        await OverviewAsync(manager, $"from=2020-01-01&to={today}", HttpStatusCode.UnprocessableEntity);
        await OverviewAsync(manager, "granularity=year", HttpStatusCode.UnprocessableEntity);
        await OverviewAsync(manager, "priority=Urgent", HttpStatusCode.UnprocessableEntity);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/reports/complaints?minOccurrences=1")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.GetAsync("/api/support/reports/complaints?from=2026-10-10&to=2026-10-01")).StatusCode);
    }

    // ---- Memnuniyet

    private static readonly Regex SurveyLink = new(@"anket/(?<tenant>[0-9a-f]{32})#t=(?<token>[A-Za-z0-9_-]+)", RegexOptions.Compiled);

    private async Task<(string Tenant, string Token)> SurveyLinkAsync(Guid tenantId, string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var mail = await db.EmailMessages.AsNoTracking().Where(e => e.TenantId == tenantId && e.ToAddress == address).OrderByDescending(e => e.CreatedAt).FirstAsync();
        var match = SurveyLink.Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        return (match.Groups["tenant"].Value, match.Groups["token"].Value);
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Csat_reports_the_response_rate_average_distribution_and_low_scores_with_an_honest_sample_warning()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var anonymous = Anonymous();
        await SetSettingAsync(manager, "support.survey.enabled", "true");

        var addresses = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var address = $"csat{i}-{Guid.NewGuid():N}@musteri.example.test";
            addresses.Add(address);
            var t = await WorkAsync(lead, await TicketAsync(lead, s, $"Anketli {i}", email: address));
            await ActAsync(lead, t.Id, "resolve", resolve: true);
        }

        // Alıcı e-postası olmayan (müşterisiz, adressiz) talepte anket açılmaz.
        var nobody = await CreateAsync<TicketDto>(lead, "tickets", new SaveTicketRequest(Subject: "Adressiz", ReporterName: "Telefonla arayan"));
        await ActAsync(lead, (await WorkAsync(lead, nobody)).Id, "resolve", resolve: true);

        // İki anket yanıtlanır (5 ve 1), biri yanıtsız kalır.
        foreach (var (address, rating) in new[] { (addresses[0], 5), (addresses[1], 1) })
        {
            var (tenantKey, token) = await SurveyLinkAsync(s.Tenant.Id, address);
            var response = await anonymous.PostAsJsonAsync($"/api/public/support-survey/{tenantKey}/answer", new PublicSurveyAnswerRequest(token, rating));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var today = Day(DateTimeOffset.UtcNow);
        var report = await OverviewAsync(manager, $"from={today}&to={today}");
        var csat = report.Csat;
        Assert.Equal((3, 0, 2, 66.7, 3d, 1, true), (csat.Sent, csat.NotSent, csat.Answered, csat.ResponseRatePercent, csat.AverageRating, csat.Low, csat.SmallSample));
        Assert.Equal(["1", "0", "0", "0", "1"], csat.Distribution.Select(d => d.Count.ToString()));
        Assert.Equal(csat.Low, await ListCountAsync(lead, csat.DrillDown));
        Assert.DoesNotContain("Çok", System.Text.Json.JsonSerializer.Serialize(csat)); // yorum metni raporda yoktur

        // Anketsiz dönem: oran boş (uydurulmaz).
        var empty = await OverviewAsync(manager, $"from={Day(DateTimeOffset.UtcNow.AddDays(-20))}&to={Day(DateTimeOffset.UtcNow.AddDays(-10))}");
        Assert.Equal((0, 0, 0), (empty.Csat.Sent, empty.Csat.Answered, empty.Meta.TicketCount));
        Assert.Null(empty.Csat.ResponseRatePercent);
        Assert.Null(empty.Csat.AverageRating);
    }

    // ---- Şikâyet inceleme

    private static async Task<TicketDto> ComplaintAsync(HttpClient client, Setup s, string subject, string rootCause) =>
        await ResolveComplaintAsync(client, await WorkAsync(client, await TicketAsync(client, s, subject, typeKey: "Complaint"), reply: false), rootCause);

    private static async Task<TicketDto> ResolveComplaintAsync(HttpClient client, TicketDto t, string rootCause) =>
        await ActAsync(client, t.Id, "resolve", rootCause: rootCause, corrective: "Süreç düzeltilecek.", resolve: true);

    private static async Task<List<CorrectiveActionDto>> ActionsAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<CorrectiveActionDto>>($"/api/support/tickets/{ticketId}/corrective-actions"))!;

    private static Task<HttpResponseMessage> AddActionAsync(HttpClient client, Guid ticketId, string? title, Guid? owner, DateOnly? due) =>
        client.PostAsJsonAsync($"/api/support/tickets/{ticketId}/corrective-actions", new AddCorrectiveActionRequest(title, owner, due));

    private static Task<HttpResponseMessage> SetActionAsync(HttpClient client, Guid ticketId, Guid actionId, string status, string? note) =>
        client.PostAsJsonAsync($"/api/support/tickets/{ticketId}/corrective-actions/{actionId}/status", new SetCorrectiveActionStatusRequest(status, note));

    [Fact]
    public async Task Recurring_root_causes_are_grouped_by_normalised_text_with_drill_down_to_the_same_tickets()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var foreign = await LoginAsync(other.Manager);
        using var foreignLead = await LoginAsync(other.Lead);
        var a = await ComplaintAsync(manager, s, "Şikâyet A", "Kargo firması geç teslim etti");
        var b = await ComplaintAsync(manager, s, "Şikâyet B", "  KARGO   FİRMASI GEÇ TESLİM ETTİ. ");
        var c = await ComplaintAsync(manager, s, "Şikâyet C", "Eksik ürün gönderildi");
        var d = await ComplaintAsync(manager, s, "Şikâyet D", "Kargo firması geç teslim etti");
        await ComplaintAsync(foreignLead, other, "Başka tenant şikâyeti", "Kargo firması geç teslim etti");

        var today = Day(DateTimeOffset.UtcNow);
        var review = (await manager.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={today}&to={today}"))!;
        Assert.Equal((4, 4, 2), (review.Complaints, review.WithRootCause, review.MinOccurrences));
        var group = Assert.Single(review.Recurring);
        Assert.Equal(3, group.Count);
        Assert.Equal("Kargo firması geç teslim etti", group.Cause); // en sık yazılan özgün metin
        Assert.Equal(new[] { a.Id, b.Id, d.Id }.Order(), group.TicketIds.Order());
        Assert.Equal(group.Count, await ListCountAsync(manager, group.DrillDown));
        Assert.DoesNotContain(c.Id, group.TicketIds);

        // Eşik yükselince tekrar yok; başka tenant yalnızca kendi şikâyetini görür.
        Assert.Empty((await manager.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={today}&to={today}&minOccurrences=4"))!.Recurring);
        var foreignReview = (await foreign.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={today}&to={today}"))!;
        Assert.Equal(1, foreignReview.Complaints);
        Assert.Empty(foreignReview.Recurring);

        // Şikâyet olmayan talepler incelemeye girmez.
        await WorkAsync(manager, await TicketAsync(manager, s, "Sıradan talep"), reply: false);
        Assert.Equal(4, (await manager.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={today}&to={today}"))!.Complaints);
    }

    [Fact]
    public async Task Corrective_actions_are_added_completed_and_tracked_with_validation_permissions_and_isolation()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var viewer = await LoginAsync(s.Viewer);
        using var foreign = await LoginAsync(other.Manager);
        var complaint = await ComplaintAsync(manager, s, "Şikâyet", "Depo hatası");
        var plain = await WorkAsync(manager, await TicketAsync(manager, s, "Sıradan talep"), reply: false);
        var today = ReportPeriod.LocalDate(DateTimeOffset.UtcNow);

        // Doğrulama.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, " ", s.Lead.Id, today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, "Eğitim ver", null, today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, "Eğitim ver", Guid.NewGuid(), today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, "Eğitim ver", s.Lead.Id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, "Eğitim ver", s.Lead.Id, today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddActionAsync(manager, complaint.Id, "Eğitim ver", s.Lead.Id, today.AddYears(3))).StatusCode);
        // Yalnızca şikâyet türünde.
        Assert.Equal(HttpStatusCode.Conflict, (await AddActionAsync(manager, plain.Id, "Eğitim ver", s.Lead.Id, today.AddDays(5))).StatusCode);

        // Mutlu yol: ekle (çözülmüş şikâyette de), tarihçeye yazılır.
        var created = await AddActionAsync(manager, complaint.Id, "Depo çalışanlarına eğitim ver", s.Lead.Id, today.AddDays(7));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var action = Assert.Single((await created.Content.ReadFromJsonAsync<List<CorrectiveActionDto>>())!);
        Assert.Equal(("Open", false, 0, s.Lead.Id), (action.Status, action.IsOverdue, action.DaysOverdue, action.OwnerId));
        Assert.Contains((await manager.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{complaint.Id}/history"))!, h => h.Kind == "CorrectiveActionAdded");

        // Tamamlama sonuç notu ister; ikinci kez aynı duruma geçiş 409; yeniden açma not ister.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetActionAsync(manager, complaint.Id, action.Id, "Done", " ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetActionAsync(manager, complaint.Id, action.Id, "Pending", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetActionAsync(manager, complaint.Id, action.Id, "Open", "Zaten açık")).StatusCode);
        var done = await SetActionAsync(manager, complaint.Id, action.Id, "Done", "Eğitim 12 kişiye verildi, test yapıldı.");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var closed = Assert.Single((await done.Content.ReadFromJsonAsync<List<CorrectiveActionDto>>())!);
        Assert.Equal(("Done", s.Manager.Id), (closed.Status, closed.ClosedBy));
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetActionAsync(manager, complaint.Id, action.Id, "Open", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActionAsync(manager, complaint.Id, action.Id, "Open", "Test başarısız, tekrar")).StatusCode);
        Assert.Null(Assert.Single(await ActionsAsync(manager, complaint.Id)).ClosedAt);
        Assert.Equal(HttpStatusCode.OK, (await SetActionAsync(manager, complaint.Id, action.Id, "Cancelled", "Kapsam değişti")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetActionAsync(manager, complaint.Id, Guid.NewGuid(), "Done", "x")).StatusCode);

        // Yetki: yönetme izni olmayan yazamaz (403), talebi okuyan listeyi görür; başka tenant 404.
        Assert.Equal(HttpStatusCode.Forbidden, (await AddActionAsync(lead, complaint.Id, "Yetkisiz", s.Lead.Id, today.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetActionAsync(viewer, complaint.Id, action.Id, "Done", "x")).StatusCode);
        Assert.Single(await ActionsAsync(viewer, complaint.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{complaint.Id}/corrective-actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddActionAsync(foreign, complaint.Id, "Sızma", other.Lead.Id, today.AddDays(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetActionAsync(foreign, complaint.Id, action.Id, "Done", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync($"/api/support/tickets/{complaint.Id}/corrective-actions")).StatusCode);

        // İptal edilmiş talepte aksiyon değişmez.
        var cancelled = await ComplaintAsync(manager, s, "İptal edilecek", "Başka neden");
        var open = Assert.Single((await (await AddActionAsync(manager, cancelled.Id, "Önlem", s.Lead.Id, today.AddDays(3))).Content.ReadFromJsonAsync<List<CorrectiveActionDto>>())!);
        await ActAsync(manager, cancelled.Id, "reopen", reason: "Yanlış");
        await ActAsync(manager, cancelled.Id, "cancel", reason: "Vazgeçildi");
        Assert.Equal(HttpStatusCode.Conflict, (await SetActionAsync(manager, cancelled.Id, open.Id, "Done", "x")).StatusCode);
    }

    [Fact]
    public async Task The_complaint_review_summarises_overdue_actions_and_the_on_time_completion_rate_with_stated_numerator_and_denominator()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        var complaint = await ComplaintAsync(manager, s, "Gecikmeli aksiyonlar", "Süreç eksikliği");
        var today = ReportPeriod.LocalDate(DateTimeOffset.UtcNow);

        async Task<CorrectiveActionDto> NewAsync(string title, int dueInDays) =>
            (await (await AddActionAsync(manager, complaint.Id, title, s.Manager.Id, today.AddDays(dueInDays))).Content.ReadFromJsonAsync<List<CorrectiveActionDto>>())!.Single(a => a.Title == title);

        var onTime = await NewAsync("Zamanında", 1);
        var late = await NewAsync("Geç tamamlanan", 1);
        var overdue = await NewAsync("Gecikmiş", 1);
        await NewAsync("Henüz vadesi gelmemiş", 6);
        await SetActionAsync(manager, complaint.Id, onTime.Id, "Done", "Bitti");
        await SetActionAsync(manager, complaint.Id, late.Id, "Done", "Bitti");

        // Son tarihleri geriye al: zamanında tamamlanan 2 gün önceydi (3 gün önce kapatıldı), geç olan 5 gün önceydi, gecikmiş 3 gün önceydi.
        string D(int days) => today.AddDays(days).ToString("yyyy-MM-dd");
        await SqlAsync(s.Tenant.Id, "UPDATE support.CorrectiveActions SET DueDate = {1}, ClosedAt = DATEADD(DAY, -3, SYSDATETIMEOFFSET()) WHERE Id = {0}", onTime.Id, D(-2));
        await SqlAsync(s.Tenant.Id, "UPDATE support.CorrectiveActions SET DueDate = {1} WHERE Id = {0}", late.Id, D(-5));
        await SqlAsync(s.Tenant.Id, "UPDATE support.CorrectiveActions SET DueDate = {1} WHERE Id = {0}", overdue.Id, D(-3));

        var list = await ActionsAsync(manager, complaint.Id);
        var overdueItem = list.Single(a => a.Id == overdue.Id);
        Assert.Equal((true, 3), (overdueItem.IsOverdue, overdueItem.DaysOverdue));
        Assert.False(list.Single(a => a.Id == onTime.Id).IsOverdue);

        var review = (await manager.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={D(-10)}&to={D(0)}"))!;
        var summary = review.Actions;
        Assert.Equal((2, 1, 2, 0), (summary.Open, summary.Overdue, summary.Done, summary.Cancelled));
        Assert.Equal((3, 1, 1, 1), (summary.DueInPeriod, summary.DoneOnTime, summary.DoneLate, summary.OverdueInPeriod));
        Assert.Equal(33.3, summary.OnTimePercent); // 1 / (1 + 1 + 1)
        var listed = Assert.Single(review.OverdueActions);
        Assert.Equal((overdue.Id, complaint.Number, 3), (listed.Id, review.OverdueActions[0].TicketNumber == complaint.Number ? complaint.Number : string.Empty, listed.DaysOverdue));

        // Vadesi dönemde olmayan aksiyonlar orana girmez: dar dönemde payda sıfırsa oran boş.
        var narrow = (await manager.GetFromJsonAsync<ComplaintReviewDto>($"/api/support/reports/complaints?from={D(0)}&to={D(0)}"))!;
        Assert.Equal(0, narrow.Actions.DueInPeriod);
        Assert.Null(narrow.Actions.OnTimePercent);
        Assert.Equal(1, narrow.Actions.Overdue); // birikim dönemden bağımsızdır
    }
}
