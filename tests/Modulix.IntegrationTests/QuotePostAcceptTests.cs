using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Crm;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Settings;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 7: kabul sonrası süreçler — devir sözleşmesi, fırsat kazanma politikası, takip/hatırlatma aktivitesi, süre dolumu işi.</summary>
public class QuotePostAcceptTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task The_handover_contract_exposes_only_an_accepted_quote_without_costs_or_internal_notes()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);

        // Sunulmuş (henüz kabul edilmemiş) teklif devir görünümü vermez.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/crm/quotes/{quote.Id}/handover")).StatusCode);

        // Müşteri bağlantıyla kabul eder.
        var token = (await (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest())).Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!.Token;
        using var anonymous = factory.CreateClient();
        var view = (await (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/view", new PublicQuoteTokenRequest(token))).Content.ReadFromJsonAsync<PublicQuoteView>())!;
        var accept = await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/decision",
            new PublicQuoteDecisionRequest(token, "accept", "Ali Veli", ConfirmTerms: true, DocumentSha256: view.DocumentSha256));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        var response = await admin.GetAsync($"/api/crm/quotes/{quote.Id}/handover");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var handover = (await response.Content.ReadFromJsonAsync<AcceptedQuoteDto>())!;
        Assert.Equal((quote.Id, 1800m, 360m, 2160m, "TRY"), (handover.QuoteId, handover.NetTotal, handover.TaxTotal, handover.GrossTotal, handover.Currency));
        Assert.Equal("Ali Veli", handover.AcceptedBy);
        Assert.Equal(view.DocumentSha256, handover.DocumentSha256);
        var line = Assert.Single(handover.Lines);
        Assert.Equal((2m, 1000m, 1800m), (line.Quantity, line.UnitPrice, line.Net));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("GIZLI-IC-NOT", raw);
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);

        // Başka tenant devir görünümünü göremez.
        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [Modulix.Modules.Crm.CrmModule.Permissions.QuotesRead], Modulix.Core.Contracts.Authorization.RecordScope.All, stranger);
        using var foreign = await LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/crm/quotes/{quote.Id}/handover")).StatusCode);
    }

    [Fact]
    public async Task Accepting_leaves_the_opportunity_alone_by_default_and_wins_it_once_when_the_policy_is_on()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Büyük fırsat", s.CompanyId, Amount: 5000m));

        // Varsayılan (ayar kapalı): kabul fırsata dokunmaz.
        var first = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id);
        await RecordAcceptedAsync(admin, first.Id);
        await DrainAsync(s.TenantId);
        Assert.Equal("Open", (await GetOpportunityAsync(admin, opportunity.Id)).Status);

        // Ayar açık: kabulde bağlı açık fırsat kazanıldı olur; tutar teklifin net toplamıdır.
        await SetSettingAsync(admin, "crm.quote.on-accept-win-opportunity", "true");
        var second = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id);
        await RecordAcceptedAsync(admin, second.Id);
        await DrainAsync(s.TenantId);
        var won = await GetOpportunityAsync(admin, opportunity.Id);
        var trace = string.Join(" | ", (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{second.Id}/history"))!.Select(h => $"{h.Kind}: {h.Reason}"));
        Assert.True((won.Status, won.WonAmount, won.WonCurrency) == ("Won", 1800m, "TRY"), $"Fırsat {won.Status}; tarihçe: {trace}");
        var history = (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{second.Id}/history"))!;
        Assert.Single(history, h => h.Kind == "OpportunityWon");

        // Teslim tekrarlansa da ikinci kazanma ya da ikinci işaret oluşmaz.
        await ReplayAllDeliveriesAsync(s.TenantId);
        await DrainAsync(s.TenantId);
        Assert.Single((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{second.Id}/history"))!, h => h.Kind == "OpportunityWon");
        Assert.Equal("Won", (await GetOpportunityAsync(admin, opportunity.Id)).Status);

        // Fırsat artık açık değil: başka bir teklifin kabulü ona dokunmaz, nedeni tarihçeye yazılır.
        var third = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id);
        await RecordAcceptedAsync(admin, third.Id);
        await DrainAsync(s.TenantId);
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{third.Id}/history"))!, h => h.Kind == "OpportunityWinSkipped");
        Assert.Equal(1800m, (await GetOpportunityAsync(admin, opportunity.Id)).WonAmount);
    }

    [Fact]
    public async Task A_customer_acceptance_through_the_share_link_also_wins_the_linked_open_opportunity()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await SetSettingAsync(admin, "crm.quote.on-accept-win-opportunity", "true");
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Müşteri kabulü fırsatı", s.CompanyId, Amount: 4000m));
        var quote = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id);
        var token = (await (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest())).Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!.Token;

        using var anonymous = factory.CreateClient();
        var view = (await (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/view", new PublicQuoteTokenRequest(token))).Content.ReadFromJsonAsync<PublicQuoteView>())!;
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/decision",
            new PublicQuoteDecisionRequest(token, "accept", "Ali Veli", ConfirmTerms: true, DocumentSha256: view.DocumentSha256))).StatusCode);
        Assert.Equal("Open", (await GetOpportunityAsync(admin, opportunity.Id)).Status);   // olay işlenene kadar dokunulmaz

        await DrainAsync(s.TenantId);
        var won = await GetOpportunityAsync(admin, opportunity.Id);
        Assert.Equal(("Won", 1800m), (won.Status, won.WonAmount));
    }

    [Fact]
    public async Task Presenting_opens_one_follow_up_for_the_owner_only_when_the_setting_is_on()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var off = await PresentedQuoteAsync(admin, s.CompanyId);
        await DrainAsync(s.TenantId);
        Assert.Empty(await ActivitiesAsync(admin, off.QuoteNumber));

        await SetSettingAsync(admin, "crm.quote.follow-up-days", "2");
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        await DrainAsync(s.TenantId);
        await ReplayAllDeliveriesAsync(s.TenantId);
        await DrainAsync(s.TenantId);

        var activity = Assert.Single(await ActivitiesAsync(admin, quote.QuoteNumber));
        Assert.Contains("Teklif takibi", activity.Subject);
        Assert.Equal(s.Admin.Id, activity.OwnerId);
        Assert.Equal(DateTimeOffset.UtcNow.Date.AddDays(2).AddHours(9), activity.DueAt!.Value.UtcDateTime);
        Assert.Single((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!, h => h.Kind == "FollowUpScheduled");

        // Aktivite teklife bağlıdır (ActivityLink); kullanıcı aynı teklife elle başka bir aktivite de bağlar.
        var links = (await admin.GetFromJsonAsync<CrmPage<ActivityLinkDto>>($"/api/crm/activity-links?activityId={activity.Id}"))!.Items;
        Assert.Contains(links, l => l.EntityKey == "crm.quote" && l.RecordId == quote.Id);
        var manual = await PostAsync<ActivityDto>(admin, "/api/crm/activities", new SaveActivityRequest($"Elle not {quote.QuoteNumber}", "FollowUp", DueAt: DateTimeOffset.UtcNow.AddDays(1)));
        await PostAsync<ActivityLinkDto>(admin, "/api/crm/activity-links", new SaveActivityLinkRequest(manual.Id, "crm.quote", quote.Id));

        // Teklif sonuçlanınca tekliften açılan takip iptal edilir; elle açılan aktivite açık kalır.
        await RecordAcceptedAsync(admin, quote.Id);
        await DrainAsync(s.TenantId);
        Assert.Equal("Cancelled", (await admin.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{activity.Id}"))!.Status);
        Assert.Equal("Open", (await admin.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{manual.Id}"))!.Status);
        Assert.Single((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!, h => h.Kind == "FollowUpsClosed");
    }

    [Fact]
    public async Task The_expiry_job_expires_overdue_quotes_and_opens_each_reminder_once()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await SetSettingAsync(admin, "crm.quote.expiry-reminder-days", "5");
        var runner = factory.Services.GetRequiredService<QuoteExpiryRunner>();

        var soon = await PresentedQuoteAsync(admin, s.CompanyId, validUntil: DateTimeOffset.UtcNow.AddDays(3));
        var later = await PresentedQuoteAsync(admin, s.CompanyId, validUntil: DateTimeOffset.UtcNow.AddDays(30));
        var overdue = await PresentedQuoteAsync(admin, s.CompanyId, validUntil: DateTimeOffset.UtcNow.AddSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var run = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.True((run.Expired, run.RemindersCreated) == (1, 1), $"Tur: {run.Expired}/{run.RemindersCreated} {run.Warning}");

        var expired = await GetQuoteAsync(admin, overdue.Id);
        Assert.Equal("Expired", expired.Status);
        Assert.NotNull(expired.DecidedAt);
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{overdue.Id}/history"))!, h => h.Kind == "Expired");
        Assert.Equal("Presented", (await GetQuoteAsync(admin, later.Id)).Status);
        Assert.Equal("Presented", (await GetQuoteAsync(admin, soon.Id)).Status);

        // Yalnızca yaklaşan teklif için, sorumlusuna tek hatırlatma; ikinci tur hiçbir şey eklemez.
        var reminder = Assert.Single(await ActivitiesAsync(admin, soon.QuoteNumber));
        Assert.Contains("Teklif süresi doluyor", reminder.Subject);
        Assert.Equal(s.Admin.Id, reminder.OwnerId);
        Assert.Empty(await ActivitiesAsync(admin, later.QuoteNumber));
        var again = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal((0, 0), (again.Expired, again.RemindersCreated));
        Assert.Single(await ActivitiesAsync(admin, soon.QuoteNumber));

        // Süresi dolan teklif raporda "süresi doldu" olarak sayılır.
        var report = (await admin.GetFromJsonAsync<QuoteReportDto>("/api/crm/quotes/report"))!;
        Assert.Equal(1, report.ExpiredCount);

        // Sorumluya e-posta: hatırlatma ve süre dolumu için birer tane, platform kuyruğunda (gönderim arka plandadır).
        Assert.Equal(1, await QueuedCountAsync($"crm.quote-reminder:{soon.Id:N}", s.Admin.Email!));
        Assert.Equal(1, await QueuedCountAsync($"crm.quote-expired:{overdue.Id:N}", s.Admin.Email!));
        Assert.Equal(0, await QueuedCountAsync($"crm.quote-expired:{later.Id:N}", s.Admin.Email!));

        // Süresi dolan teklifin tekliften açılmış açık aktivitesi yoktu; hatırlatması açılan teklif kabul edilirse hatırlatma kapanır.
        await RecordAcceptedAsync(admin, soon.Id);
        await DrainAsync(s.TenantId);
        Assert.Equal("Cancelled", (await ActivitiesAsync(admin, soon.QuoteNumber)).Single().Status);
    }

    [Fact]
    public async Task The_expiry_job_skips_the_round_while_another_instance_holds_the_lock()
    {
        var s = await SetUpAsync();
        var runner = factory.Services.GetRequiredService<QuoteExpiryRunner>();
        var connectionString = factory.Services.GetRequiredService<Modulix.Infrastructure.Persistence.ModulixDatabaseOptions>().AppConnectionString;

        await using var other = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await other.OpenAsync();
        await using (var take = other.CreateCommand())
        {
            take.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
            take.Parameters.AddWithValue("@name", QuoteExpiryRunner.LockResource(s.TenantId));
            Assert.True(Convert.ToInt32(await take.ExecuteScalarAsync()) >= 0);
        }

        var skipped = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.True(skipped.Skipped);
        Assert.Equal((0, 0), (skipped.Expired, skipped.RemindersCreated));

        await other.CloseAsync();
        var normal = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.False(normal.Skipped);
    }

    // ---- yardımcılar

    private async Task<int> QueuedCountAsync(string reference, string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Modulix.Infrastructure.Persistence.ModulixDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.EmailMessages, e => e.Reference == reference && e.ToAddress == address);
    }

    private async Task<List<ActivityDto>> ActivitiesAsync(HttpClient client, string search) =>
        [.. (await client.GetFromJsonAsync<CrmPage<ActivityDto>>($"/api/crm/activities?search={Uri.EscapeDataString(search)}"))!.Items];

    private static async Task<OpportunityDto> GetOpportunityAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{id}"))!;

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task RecordAcceptedAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/record-accepted", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<QuoteDto> PresentedQuoteAsync(HttpClient client, Guid companyId, Guid? opportunityId = null, DateTimeOffset? validUntil = null)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes",
            new SaveQuoteRequest("Teklif", companyId, OpportunityId: opportunityId, ValidUntil: validUntil ?? DateTimeOffset.UtcNow.AddDays(30), InternalNotes: "GIZLI-IC-NOT"));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 2m, 1000m, null, "Percent", 10m, 20m, null, "Standard", UnitCost: 700m));
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/present", new QuoteTransitionRequest((await GetQuoteAsync(client, quote.Id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var response = await client.PutAsJsonAsync("/api/settings",
            new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, 0) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task DrainAsync(Guid tenantId)
    {
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.OpenScope(tenantId);
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round.Dispatched == 0 && round.Delivered == 0)
            {
                return;
            }
        }
    }

    /// <summary>Tüm teslimleri yeniden "bekliyor" yapar: en az bir kez teslim garantisinin tekrarını taklit eder.</summary>
    private async Task ReplayAllDeliveriesAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<Modulix.Infrastructure.Persistence.ModulixDbContext>();
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
            db.Database, "DELETE FROM platform.EventDeliveries; UPDATE platform.OutboxMessages SET DispatchedAt = NULL");
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, Guid CompanyId, string Slug, Guid TenantId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, company.Id, tenant.Slug, tenant.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
