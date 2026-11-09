using System.Net;
using System.Net.Http.Json;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Settings;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// CRM panosu "Benim günüm" (<c>GET /api/crm/my-day</c>): yalnızca kullanıcının kendi kayıtları, kural tabanlı öneriler, bu ay kapanabilecekler ve haftalık sayılar.
/// Mutlu yol, izne bağlı bölümler ve tutar gizleme, yetki ve tenant izolasyonu.
/// </summary>
public class MyDayTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SellerPermissions =
    [
        CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead, OpportunitiesChangeStage, OpportunitiesClose,
        SalesReportsRead, LeadsRead, LeadsCreate, LeadsUpdate, QuotesRead,
    ];

    [Fact]
    public async Task My_day_lists_only_my_overdue_opportunity_new_lead_closing_and_won_this_week()
    {
        var s = await SetUpAsync();
        var today = await WinAsync(s.Admin, s.CompanyId, "Bu hafta kazanıldı", 10_000m);
        var overdue = await OpenAsync(s.Admin, s.CompanyId, "Gecikmiş", 250_000m, 60, today.AddDays(-5));
        var closing = await OpenAsync(s.Admin, s.CompanyId, "Bu ay", 80_000m, 70, LastDayOfMonth(today));
        var lead = await PostAsync<LeadDto>(s.Admin, "/api/crm/leads", new SaveLeadRequest("Depo otomasyonu", FirstName: "Deniz", LastName: "Kaya", CompanyName: "Kuzey Lojistik", OwnerId: s.AdminId));

        // Aynı tenant'ta başka satıcının gecikmiş fırsatı benim günüme girmez.
        var other = await UserAsync(s, SellerPermissions, Scope.All);
        var foreign = await OpenAsync(other.Client, s.CompanyId, "Başkasının", 999_000m, 90, today.AddDays(-3));

        var day = await MyDayAsync(s.Admin);

        Assert.Equal(today, day.Today);
        Assert.True(day.AmountsVisible && day.OpportunitiesAvailable && day.LeadsAvailable && day.QuotesAvailable);
        Assert.Equal((2, 1, 1, 1), (day.OpenOpportunityCount, day.OverdueOpportunityCount, day.NewLeadCount, day.WonThisWeek));
        Assert.Equal(
            [(MyDayKinds.NewLead, lead.Id), (MyDayKinds.OverdueOpportunity, overdue)],
            day.Suggestions.Select(x => (x.Kind, x.RecordId)));
        var late = day.Suggestions[1];
        Assert.Equal((5, 250_000m), (late.DaysOverdue!.Value, late.Amount!.Value));
        Assert.Equal("Deniz Kaya", day.Suggestions[0].Title);
        Assert.DoesNotContain(day.Suggestions, x => x.RecordId == foreign);

        var month = Assert.Single(day.ClosingThisMonth, c => c.Id == closing);
        Assert.Equal((70, false), (month.Probability, month.Overdue));
        Assert.DoesNotContain(day.ClosingThisMonth, c => c.Id == foreign);
        // Eşikler firma ayarıdır; varsayılanlar: teklif hatırlatma 3 gün, fırsat hareketsizlik eşiği 30 gün.
        Assert.Equal((3, 30), (day.QuoteReminderDays, day.StaleDays));
    }

    [Fact]
    public async Task An_accepted_quote_on_an_open_opportunity_is_suggested_first_and_thresholds_follow_tenant_settings()
    {
        var s = await SetUpAsync();
        var seller = await UserAsync(s, [.. SellerPermissions, QuotesWrite, QuotesManualPrice, QuotesPresent, QuotesRecordDecision, CoreModule.Permissions.ManageSettings], Scope.All);
        var opportunity = await OpenAsync(seller.Client, s.CompanyId, "Depo kurulumu", 40_000m, 50, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20));
        var quote = await AcceptedQuoteAsync(seller.Client, s.CompanyId, opportunity);

        await SetSettingAsync(seller.Client, Modulix.Modules.Crm.CrmModule.SettingKeys.QuoteReplyReminderDays, "0");
        await SetSettingAsync(seller.Client, Modulix.Modules.Crm.CrmModule.SettingKeys.OpportunityStaleDays, "7");
        var day = await MyDayAsync(seller.Client);

        var step = Assert.Single(day.Suggestions);
        Assert.Equal((MyDayKinds.AcceptedQuote, opportunity, quote.QuoteNumber), (step.Kind, step.RecordId, step.Subtitle));
        Assert.Equal(0, step.DaysWaiting);
        Assert.Equal((0, 7), (day.QuoteReminderDays, day.StaleDays));
    }

    [Fact]
    public async Task Sections_follow_their_own_permissions_and_amounts_are_hidden_without_the_amount_permission()
    {
        var s = await SetUpAsync();
        var seller = await UserAsync(s, [.. SellerPermissions.Except([OpportunityAmountsRead, LeadsRead, LeadsCreate, LeadsUpdate, QuotesRead])], Scope.Own);
        var today = await WinAsync(s.Admin, s.CompanyId, "Çapa", 1_000m);
        // Tutar izni olmayan satıcı tutarlı fırsat açamaz; fırsatı yönetici açıp satıcıya atar.
        await OpenAsync(s.Admin, s.CompanyId, "Gecikmiş", 50_000m, 40, today.AddDays(-2), owner: seller.UserId);

        var day = await MyDayAsync(seller.Client);

        Assert.False(day.AmountsVisible);
        Assert.False(day.LeadsAvailable);
        Assert.False(day.QuotesAvailable);
        Assert.Equal(0, day.NewLeadCount);
        var step = Assert.Single(day.Suggestions);
        Assert.Equal(MyDayKinds.OverdueOpportunity, step.Kind);
        Assert.Null(step.Amount);
    }

    [Fact]
    public async Task Without_the_sales_report_permission_my_day_is_forbidden()
    {
        var s = await SetUpAsync();
        var seller = await UserAsync(s, [.. SellerPermissions.Except([SalesReportsRead])], Scope.All);

        Assert.Equal(HttpStatusCode.Forbidden, (await seller.Client.GetAsync("/api/crm/my-day")).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_sees_nothing_of_my_day()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        var today = await WinAsync(a.Admin, a.CompanyId, "A kazandı", 5_000m);
        await OpenAsync(a.Admin, a.CompanyId, "A gecikmiş", 7_000m, 60, today.AddDays(-4));
        await OpenAsync(a.Admin, a.CompanyId, "A bu ay", 7_000m, 60, LastDayOfMonth(today));
        await PostAsync<LeadDto>(a.Admin, "/api/crm/leads", new SaveLeadRequest("A adayı", LastName: "Kaya", OwnerId: a.AdminId));

        var other = await MyDayAsync(b.Admin);

        Assert.Empty(other.Suggestions);
        Assert.Empty(other.ClosingThisMonth);
        Assert.Equal((0, 0, 0, 0), (other.OpenOpportunityCount, other.OverdueOpportunityCount, other.NewLeadCount, other.WonThisWeek));
    }

    // ---- Yardımcılar

    private record Setup(HttpClient Admin, Guid AdminId, Tenant Tenant, Guid CompanyId);

    private record Scoped(HttpClient Client, Guid UserId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, SellerPermissions, Scope.All, admin);
        var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return new Setup(client, admin.Id, tenant, company.Id);
    }

    private async Task<Scoped> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.Tenant.Id, permissions, scope, user);
        return new Scoped(await LoginAsync(user), user.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static DateOnly LastDayOfMonth(DateOnly day) => new(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<Guid> OpenAsync(HttpClient client, Guid companyId, string title, decimal amount, int probability, DateOnly close, Guid? owner = null) =>
        (await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, ExpectedCloseDate: close, Probability: probability, OwnerId: owner))).Id;

    /// <summary>Fırsat açar ve kazanıldı olarak kapatır; sunucunun kapanış günü (firmanın bugünü) döner.</summary>
    private static async Task<DateOnly> WinAsync(HttpClient client, Guid companyId, string title, decimal amount)
    {
        var opportunity = await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow)));
        var result = await ReadAsync<OpportunityActionResultDto>(await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(opportunity.Version)));
        return result.Opportunity.ActualCloseDate!.Value;
    }

    /// <summary>Fırsata bağlı teklif açar, kalem ekler, müşteriye sunar ve kabulünü kaydeder.</summary>
    private static async Task<QuoteDto> AcceptedQuoteAsync(HttpClient client, Guid companyId, Guid opportunityId)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes",
            new SaveQuoteRequest("Kurulum teklifi", companyId, OpportunityId: opportunityId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "Kurulum", "Free", null, 1m, 30_000m, null, "None", 0m, 20m, null, "Standard"));
        await QuoteTransitionAsync(client, quote.Id, "present");
        return await QuoteTransitionAsync(client, quote.Id, "record-accepted");
    }

    private static async Task<QuoteDto> QuoteTransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        return await ReadAsync<QuoteDto>(await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version)));
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(x => x.Key == key);
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, current.Version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<MyDayDto> MyDayAsync(HttpClient client) => await ReadAsync<MyDayDto>(await client.GetAsync("/api/crm/my-day"));
}
