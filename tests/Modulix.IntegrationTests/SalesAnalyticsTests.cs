using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0035 dilim 2: teklif kohortu, lead dönüşümü, aşama akışı, ekip performansı ve veri kalitesi. Mutlu yol, hata/doğrulama, tenant izolasyonu ve yetki.
/// </summary>
public class SalesAnalyticsTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Analytics = "/api/crm/sales/analytics";

    // ---- Teklif kohortu (kabul senaryosu 4'ün uçtan uca küçük hali)

    [Fact]
    public async Task Quote_cohort_counts_families_not_revisions_excludes_drafts_and_shows_the_revision_rate()
    {
        var s = await SetUpAsync();
        var accepted = await PresentedAsync(s.Admin, s.CompanyId, 1_000m);
        await TransitionAsync(s.Admin, accepted.Id, "record-accepted");
        var rejected = await PresentedAsync(s.Admin, s.CompanyId, 500m);
        await TransitionAsync(s.Admin, rejected.Id, "record-rejected", "Pahalı");
        var revised = await PostAsync<QuoteDto>(s.Admin, $"/api/crm/quotes/{rejected.Id}/revise", new ReviseQuoteRequest("Yeni fiyat"));
        await TransitionAsync(s.Admin, revised.Id, "present");
        var cancelled = await PresentedAsync(s.Admin, s.CompanyId, 200m);
        await TransitionAsync(s.Admin, cancelled.Id, "cancel", "Vazgeçildi");
        await NewQuoteAsync(s.Admin, s.CompanyId, 300m);   // taslak: hiç gönderilmedi

        var result = await GetAsync<QuoteCohortDto>(s.Admin, "quotes");
        var data = result.Data;

        // 3 aile (taslak yok): kabul 1, beklemede 1 (revize edilen ailenin son revizyonu sunuldu), iptal 1.
        Assert.Equal((3, 1, 1, 0, 1), (data.Families, data.Accepted, data.Pending, data.Rejected, data.Cancelled));
        Assert.Equal(33.33m, data.AcceptanceRatePercent);
        // 4 gönderilen revizyon: aile başına 1,33; revizyon kabul oranı %25 (aile oranından düşük).
        Assert.Equal((4, 1.33m, 25m), (data.PresentedRevisions, data.AverageRevisionsPerFamily, data.RevisionAcceptanceRatePercent));
        Assert.Equal(1_000m, data.AcceptedNet.Amount);   // vergi hariç net tutar
        Assert.False(data.Mature);   // dönem sürüyor
        Assert.Contains(result.Warnings, w => w.Contains("olgun değil"));
        Assert.NotEmpty(result.Definitions);
    }

    [Fact]
    public async Task Quote_cohort_before_the_presentation_has_no_families_and_rejects_a_bad_period_kind()
    {
        var s = await SetUpAsync();
        var presented = await PresentedAsync(s.Admin, s.CompanyId, 100m);
        await TransitionAsync(s.Admin, presented.Id, "record-accepted");

        // Kesim dünün sonu: bugün sunulan teklif henüz yok.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var earlier = await GetAsync<QuoteCohortDto>(s.Admin, "quotes", $"&asOf={yesterday:yyyy-MM-dd}");
        Assert.Equal(0, earlier.Data.Families);
        Assert.Null(earlier.Data.AcceptanceRatePercent);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.GetAsync($"{Analytics}/quotes?kind=Haftalik")).StatusCode);
    }

    // ---- Lead kohortu

    [Fact]
    public async Task Lead_cohort_counts_unique_leads_and_both_conversion_rates()
    {
        var s = await SetUpAsync();
        var converted = await QualifiedLeadAsync(s.Admin, "Dönüşecek", "Delta Ltd.", "delta@firma.test");
        var convert = await s.Admin.PostAsJsonAsync($"/api/crm/leads/{converted.Id}/convert",
            new ConvertLeadRequest(converted.Version, CreateCompany: true, CreateContact: true, OpportunityTitle: "Delta fırsatı", Amount: 10_000m));
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        await PostAsync<LeadDto>(s.Admin, "/api/crm/leads", new SaveLeadRequest("Açık lead", LastName: "Yılmaz", Email: "y@firma.test"));
        await PostAsync<LeadDto>(s.Admin, "/api/crm/leads", new SaveLeadRequest("Bir diğeri", LastName: "Demir", Email: "d@firma.test"));

        var data = (await GetAsync<LeadCohortDto>(s.Admin, "leads")).Data;

        Assert.Equal((3, 1, 0, 2), (data.Leads, data.Converted, data.Disqualified, data.Open));
        Assert.Equal(33.33m, data.RateOfAllPercent);
        Assert.Equal(33.33m, data.RateOfEligiblePercent);
        Assert.False(data.Mature);
    }

    // ---- Aşama akışı (tarihçeden)

    [Fact]
    public async Task Stage_flow_comes_from_history_counts_unique_opportunities_and_does_not_invent_missing_history()
    {
        var s = await SetUpAsync();
        var first = await OpenAsync(s.Admin, s.CompanyId, "İlerleyen", 1_000m);
        var second = await OpenAsync(s.Admin, s.CompanyId, "Duran", 2_000m);
        await StageAsync(s.Admin, first, "Qualified");
        await StageAsync(s.Admin, first, "Proposal");
        await StageAsync(s.Admin, first, "Qualified");   // geri dönüş: Qualified tek fırsat olarak bir kez sayılır
        await StageAsync(s.Admin, first, "Proposal");   // tekrar giriş: Proposal da tek fırsat olarak bir kez sayılır
        await StageAsync(s.Admin, second, "Qualified");

        var flow = (await GetAsync<StageFlowDto>(s.Admin, "stages")).Data;

        Assert.Equal(2, flow.Cohort);
        Assert.NotNull(flow.TrackingStart);
        var lead = flow.Rows.Single(r => r.Stage == "Lead");
        Assert.Equal((2, 2, 100m, 0), (lead.Entered, lead.Advanced, lead.AdvanceRatePercent, lead.StillHere));
        var qualified = flow.Rows.Single(r => r.Stage == "Qualified");
        Assert.Equal((2, 1, 1), (qualified.Entered, qualified.Advanced, qualified.StillHere));
        var proposal = flow.Rows.Single(r => r.Stage == "Proposal");
        Assert.Equal((1, 0, 1), (proposal.Entered, proposal.Advanced, proposal.StillHere));
        var negotiation = flow.Rows.Single(r => r.Stage == "Negotiation");
        Assert.Equal((0, null), (negotiation.Entered, negotiation.AdvanceRatePercent));   // hiç girilmeyen aşamada oran uydurulmaz

        // Geçmiş dönem: o dönemde açılmış fırsat yok → kohort 0, oran yok (uydurma yok).
        var past = (await GetAsync<StageFlowDto>(s.Admin, "stages", "&date=2020-06-15")).Data;
        Assert.Equal(0, past.Cohort);
        Assert.All(past.Rows, r => Assert.Null(r.AdvanceRatePercent));
    }

    // ---- Ekip performansı

    [Fact]
    public async Task Team_performance_groups_by_current_owner_with_cycle_sample_and_hides_amounts_without_permission()
    {
        // Saat sabittir (bugün UTC 12:00 = İstanbul 15:00): sonuç testin çalıştığı saate bağlı değildir. Gece yarısı sınırı OpportunityCycleTimeZoneTests'te sınanır.
        using var host = factory.WithClock(new MutableTimeProvider(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero)));
        var s = await SetUpAsync(host);
        var won = await OpenAsync(s.Admin, s.CompanyId, "Kazanılan", 5_000m);
        var winResult = await ReadAsync<OpportunityActionResultDto>(await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{won.Id}/win", new WinOpportunityRequestDto(won.Version)));
        var anchor = winResult.Opportunity.ActualCloseDate!.Value;
        var lost = await OpenAsync(s.Admin, s.CompanyId, "Kaybedilen", 1_000m);
        Assert.True((await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{lost.Id}/lose", new LoseOpportunityRequestDto(lost.Version, "NoDecision"))).IsSuccessStatusCode);
        await OpenAsync(s.Admin, s.CompanyId, "Açık", 2_000m);

        var team = (await GetAsync<List<TeamPerformanceRowDto>>(s.Admin, "team", $"&date={anchor:yyyy-MM-dd}")).Data;

        var row = Assert.Single(team);
        Assert.Equal((1, 1, 50m, 1), (row.Performance.WonCount, row.Performance.LostCount, row.Performance.WinRatePercent, row.Performance.OpenCount));
        Assert.Equal(5_000m, row.Performance.Realized.Amount);
        Assert.Equal(1, row.Performance.CycleSample);   // tarihçesi olan kazanılmış fırsat
        Assert.Equal((0m, 0m), (row.Performance.AverageCycleDays, row.Performance.MedianCycleDays));   // aynı gün açılıp kazanıldı

        // Hedef: kullanıcı hedefi tanımlıysa gerçekleşme hesaplanır.
        var start = new DateOnly(anchor.Year, anchor.Month, 1);
        await PostAsync<SalesTargetDto>(s.Admin, "/api/crm/sales-targets",
            new SaveSalesTargetRequest("WonAmount", "User", row.Performance.OwnerId, "TRY", start, start.AddMonths(1).AddDays(-1), 10_000m));
        var withTarget = (await GetAsync<List<TeamPerformanceRowDto>>(s.Admin, "team", $"&date={anchor:yyyy-MM-dd}")).Data.Single();
        Assert.Equal((10_000m, 50m), (withTarget.Target, withTarget.Progress!.AttainmentPercent));

        // Tutar izni olmayan kullanıcı adetleri görür, tutarları ve hedefi görmez.
        var noAmounts = await UserAsync(s, [CompaniesRead, OpportunitiesRead, SalesReportsRead, SalesTargetsRead], Scope.All);
        var hidden = await GetAsync<List<TeamPerformanceRowDto>>(noAmounts, "team", $"&date={anchor:yyyy-MM-dd}");
        Assert.False(hidden.AmountsVisible);
        var hiddenRow = hidden.Data.Single();
        Assert.Equal((1, 0m, null), (hiddenRow.Performance.WonCount, hiddenRow.Performance.Realized.Amount, hiddenRow.Target));
    }

    // ---- Veri kalitesi

    [Fact]
    public async Task Data_quality_lists_each_problem_separately_and_a_rate_clears_the_missing_rate()
    {
        var s = await SetUpAsync();
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Tutarsız", s.CompanyId, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5)));
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Tarihsiz", s.CompanyId, Amount: 100m));
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Geciken", s.CompanyId, Amount: 100m, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10)));
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Dolar", s.CompanyId, Amount: 100m, Currency: "USD", ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3)));

        var data = (await GetAsync<DataQualityDto>(s.Admin, "quality")).Data;
        Assert.Equal(4, data.OpenCount);
        Assert.Equal(1, Issue(data, DataQualityCodes.MissingAmount).Count);
        Assert.Equal(1, Issue(data, DataQualityCodes.MissingCloseDate).Count);
        Assert.Equal(1, Issue(data, DataQualityCodes.Overdue).Count);
        Assert.Equal("Geciken", Issue(data, DataQualityCodes.Overdue).Samples.Single().Title);
        Assert.Equal(1, Issue(data, DataQualityCodes.MissingRate).Count);
        Assert.Equal(4, Issue(data, DataQualityCodes.Stale).Count);   // hiç müşteri etkileşimi yok
        Assert.Equal(0, Issue(data, DataQualityCodes.NoHistory).Count);   // yeni fırsatların tarihçesi var

        await PostAsync<ExchangeRateDto>(s.Admin, "/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 30m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30)));
        Assert.Equal(0, Issue((await GetAsync<DataQualityDto>(s.Admin, "quality")).Data, DataQualityCodes.MissingRate).Count);

        // Tutar izni olmayan kullanıcı için tutar/kur/olasılık sorunları hiç hesaplanmaz (eksik sanılmaz).
        var noAmounts = await UserAsync(s, [CompaniesRead, OpportunitiesRead, SalesReportsRead], Scope.All);
        var hidden = (await GetAsync<DataQualityDto>(noAmounts, "quality")).Data;
        Assert.DoesNotContain(hidden.Issues, i => i.Code is DataQualityCodes.MissingAmount or DataQualityCodes.MissingRate or DataQualityCodes.MissingProbability);
        Assert.Contains(hidden.Issues, i => i.Code == DataQualityCodes.Overdue);
    }

    // ---- Yetki, kapsam ve izolasyon

    [Fact]
    public async Task Analytics_need_the_report_permission_and_the_underlying_record_permission_and_follow_data_scope()
    {
        var s = await SetUpAsync();
        var opportunity = await OpenAsync(s.Admin, s.CompanyId, "Yöneticinin", 1_000m);
        var quote = await PresentedAsync(s.Admin, s.CompanyId, 100m);
        Assert.NotEqual(Guid.Empty, quote.Id);
        Assert.NotEqual(Guid.Empty, opportunity.Id);

        // Rapor izni yok → 403 (her analiz).
        var none = await UserAsync(s, [CompaniesRead, OpportunitiesRead, QuotesRead, LeadsRead], Scope.All);
        foreach (var path in new[] { "quotes", "leads", "stages", "team", "quality" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"{Analytics}/{path}")).StatusCode);
        }

        // Rapor izni var ama alttaki kayıt izni yok → 403 (rapor veri erişimi kazandırmaz).
        var onlyReports = await UserAsync(s, [CompaniesRead, SalesReportsRead], Scope.All);
        foreach (var path in new[] { "quotes", "leads", "stages", "team", "quality" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await onlyReports.GetAsync($"{Analytics}/{path}")).StatusCode);
        }

        // Veri kapsamı Kendi → yöneticinin teklif, fırsat ve aşama kayıtları hiçbir analize girmez.
        var rep = await UserAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead, QuotesRead, LeadsRead, SalesReportsRead], Scope.Own);
        var quotes = await GetAsync<QuoteCohortDto>(rep, "quotes");
        Assert.True(quotes.ScopeLimited);
        Assert.Equal(0, quotes.Data.Families);
        Assert.Equal(0, (await GetAsync<StageFlowDto>(rep, "stages")).Data.Cohort);
        Assert.Empty((await GetAsync<List<TeamPerformanceRowDto>>(rep, "team")).Data);
        Assert.Equal(0, (await GetAsync<DataQualityDto>(rep, "quality")).Data.OpenCount);
    }

    [Fact]
    public async Task Another_tenant_sees_nothing_in_any_analysis()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        await OpenAsync(a.Admin, a.CompanyId, "A fırsatı", 1_000m);
        var quote = await PresentedAsync(a.Admin, a.CompanyId, 100m);
        await TransitionAsync(a.Admin, quote.Id, "record-accepted");

        Assert.Equal(0, (await GetAsync<QuoteCohortDto>(b.Admin, "quotes")).Data.Families);
        Assert.Equal(0, (await GetAsync<LeadCohortDto>(b.Admin, "leads")).Data.Leads);
        Assert.Equal(0, (await GetAsync<StageFlowDto>(b.Admin, "stages")).Data.Cohort);
        Assert.Empty((await GetAsync<List<TeamPerformanceRowDto>>(b.Admin, "team")).Data);
        Assert.Equal(0, (await GetAsync<DataQualityDto>(b.Admin, "quality")).Data.OpenCount);
        // Kendi tenant'ında görünür.
        Assert.Equal(1, (await GetAsync<QuoteCohortDto>(a.Admin, "quotes")).Data.Families);
    }

    // ---- Yardımcılar

    private record Setup(HttpClient Admin, Tenant Tenant, Guid CompanyId, WebApplicationFactory<Program>? Host = null)
    {
        public Guid TenantId => Tenant.Id;
    }

    private async Task<Setup> SetUpAsync(WebApplicationFactory<Program>? host = null)
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

        var client = host is null ? await LoginAsync(admin) : await host.LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return new Setup(client, tenant, company.Id, host);
    }

    private async Task<HttpClient> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, permissions, scope, user);
        return s.Host is null ? await LoginAsync(user) : await s.Host.LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static QualityIssueDto Issue(DataQualityDto data, string code) => data.Issues.Single(i => i.Code == code);

    private static async Task<AnalyticsDto<T>> GetAsync<T>(HttpClient client, string path, string query = "") =>
        await ReadAsync<AnalyticsDto<T>>(await client.GetAsync($"{Analytics}/{path}?kind=Month{query}"));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body) =>
        await ReadAsync<T>(await client.PostAsJsonAsync(url, body), HttpStatusCode.Created);

    private static async Task<OpportunityDto> OpenAsync(HttpClient client, Guid companyId, string title, decimal amount) =>
        await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20)));

    private static async Task StageAsync(HttpClient client, OpportunityDto opportunity, string stage)
    {
        var current = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/stage", new ChangeOpportunityStageRequest(current.Version, stage));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<LeadDto> QualifiedLeadAsync(HttpClient client, string subject, string company, string email)
    {
        var lead = await PostAsync<LeadDto>(client, "/api/crm/leads", new SaveLeadRequest(
            subject, FirstName: "Mehmet", LastName: "Kaya", CompanyName: company, Email: email, NeedDescription: "Kapsam netleşti", EstimatedBudget: 75_000m, Source: "Referral"));
        return await ReadAsync<LeadDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version)));
    }

    private static async Task<QuoteDto> NewQuoteAsync(HttpClient client, Guid companyId, decimal price)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes", new SaveQuoteRequest("Teklif", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 1m, price, null, "None", 0m, 20m, null, "Standard"));
        return (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{quote.Id}"))!;
    }

    private static async Task<QuoteDto> PresentedAsync(HttpClient client, Guid companyId, decimal price) =>
        await TransitionAsync(client, (await NewQuoteAsync(client, companyId, price)).Id, "present");

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action, string? reason = null)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        return await ReadAsync<QuoteDto>(await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version, reason)));
    }
}
