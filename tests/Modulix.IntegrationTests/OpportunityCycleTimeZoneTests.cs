using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>Sabit ya da elle ilerleyen saatle çalışan test sunucusu: sonuç, testin günün hangi saatinde çalıştığına bağlı olmaz.</summary>
internal static class ClockTestHost
{
    public static WebApplicationFactory<Program> WithClock(this ModulixApiFactory factory, TimeProvider time) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(time);
        }));

    public static async Task<HttpClient> LoginAsync(this WebApplicationFactory<Program> host, ModulixUser user)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}

/// <summary>
/// Fırsat kapanış günü ve satış döngüsü, UTC gece yarısı ile firmanın (İstanbul) gece yarısı arasındaki saatlerde (UTC 21:00–24:00).
/// Kapanış günü, tarihçedeki oluşturulma günü ve raporun "bugün"ü aynı saat dilimi kuralına (firmanınki) bağlıdır; UTC gününe
/// bakıldığında döngü −1 çıkıp örneklemden düşüyordu. Saat sabittir: test hangi saatte çalışırsa çalışsın sınırın içindedir.
/// Mutlu yol, doğrulama, tenant izolasyonu ve yetki.
/// </summary>
public sealed class OpportunityCycleTimeZoneTests : IClassFixture<ModulixApiFactory>, IDisposable
{
    private const string Team = "/api/crm/sales/analytics/team?kind=Month";

    // Bugünün (UTC) 20:50'si = İstanbul 23:50. Gün gerçek takvimden alınır (oturum süreleri gerçek saatten uzaklaşmasın), saat sabittir.
    private static readonly DateTimeOffset BeforeLocalMidnight = new(DateTime.UtcNow.Date.AddHours(20).AddMinutes(50), TimeSpan.Zero);

    private readonly ModulixApiFactory factory;
    private readonly MutableTimeProvider time = new(BeforeLocalMidnight);
    private readonly WebApplicationFactory<Program> host;

    public OpportunityCycleTimeZoneTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        host = factory.WithClock(time);
    }

    /// <summary>İstanbul'da gece yarısından önceki gün (UTC günüyle aynı).</summary>
    private static DateOnly LocalYesterday => DateOnly.FromDateTime(BeforeLocalMidnight.UtcDateTime);

    /// <summary>İstanbul'da gece yarısından sonraki gün; UTC hâlâ bir önceki gündedir.</summary>
    private static DateOnly LocalToday => LocalYesterday.AddDays(1);

    public void Dispose() => host.Dispose();

    // ---- Mutlu yol (regresyon)

    [Fact]
    public async Task Cycle_days_follow_the_company_day_when_an_opportunity_is_won_after_local_midnight()
    {
        var s = await SetUpAsync();

        // İstanbul 23:50 (dün): gece yarısından önce açılan fırsat.
        var before = await OpenAsync(s.Admin, s.CompanyId, "Gece yarısından önce", 4_000m);
        // İstanbul 00:10 (bugün; UTC 21:10, hâlâ önceki gün): gece yarısından sonra açılan fırsat.
        time.Advance(TimeSpan.FromMinutes(20));
        var after = await OpenAsync(s.Admin, s.CompanyId, "Gece yarısından sonra", 1_000m);
        // İstanbul 00:30: ikisi de kazanılır, biri kaybedilir.
        time.Advance(TimeSpan.FromMinutes(20));
        var wonBefore = await WinAsync(s.Admin, before);
        var wonAfter = await WinAsync(s.Admin, after);
        var lost = await OpenAsync(s.Admin, s.CompanyId, "Kaybedilen", 500m);
        var lostResult = await ReadAsync<OpportunityActionResultDto>(
            await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{lost.Id}/lose", new LoseOpportunityRequestDto(lost.Version, "NoDecision")));

        // Kapanış günü firmanın günüdür (UTC günü bir gün geride kalırdı).
        Assert.Equal(LocalToday, wonBefore.ActualCloseDate);
        Assert.Equal(LocalToday, wonAfter.ActualCloseDate);
        Assert.Equal(LocalToday, lostResult.Opportunity.ActualCloseDate);

        // Ekip performansı: iki kazanılan da örneklemde; süreler 1 gün (dün açıldı) ve 0 gün (aynı gün) → ortalama 0,5.
        var row = Assert.Single((await GetAsync<AnalyticsDto<List<TeamPerformanceRowDto>>>(s.Admin, $"{Team}&date={LocalToday:yyyy-MM-dd}")).Data);
        Assert.Equal((2, 1, 2), (row.Performance.WonCount, row.Performance.LostCount, row.Performance.CycleSample));
        Assert.Equal(0.5m, row.Performance.AverageCycleDays);
        Assert.Equal(5_000m, row.Performance.Realized.Amount);

        // Fırsat raporu aynı kuralı kullanır (oluşturulma günü UTC'ye göre alınsaydı ortalama 1 çıkardı).
        var report = await ReportAsync(s.Admin);
        Assert.Equal((2, 1), (report.WonCount, report.LostCount));
        Assert.Equal(0.5, report.AverageCycleDays);

        // Dönem verilmezse rapor firmanın bugününe kadar olan ayı alır; bugün kazanılanlar içindedir.
        var defaults = await GetAsync<OpportunityReportDto>(s.Admin, "/api/crm/opportunities/report");
        Assert.Equal(LocalToday, defaults.To);
        Assert.Equal(2, defaults.WonCount);
    }

    // ---- Geriye uyum: kapanışı UTC gününe göre damgalanmış eski kayıt

    [Fact]
    public async Task A_close_date_one_day_behind_the_creation_day_counts_as_zero_days_and_stays_in_the_sample()
    {
        var s = await SetUpAsync();
        time.Advance(TimeSpan.FromMinutes(40));   // İstanbul 00:30
        var opp = await OpenAsync(s.Admin, s.CompanyId, "Eski damga", 2_000m);

        // Düzeltmeden önce kazanılan kayıtlarda kapanış günü UTC günüydü: oluşturulma gününden (firma saatiyle) bir gün geride.
        var won = (await ReadAsync<OpportunityActionResultDto>(await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win",
            new WinOpportunityRequestDto(opp.Version, ActualCloseDate: LocalYesterday)))).Opportunity;
        Assert.Equal(LocalYesterday, won.ActualCloseDate);

        var row = Assert.Single((await GetAsync<AnalyticsDto<List<TeamPerformanceRowDto>>>(s.Admin, $"{Team}&date={LocalYesterday:yyyy-MM-dd}")).Data);
        Assert.Equal((1, 1, 0m), (row.Performance.WonCount, row.Performance.CycleSample, row.Performance.AverageCycleDays));
        Assert.Equal(0d, (await ReportAsync(s.Admin)).AverageCycleDays);
    }

    // ---- Doğrulama

    [Fact]
    public async Task Close_and_recontact_dates_are_validated_against_the_company_today_after_local_midnight()
    {
        var s = await SetUpAsync();
        time.Advance(TimeSpan.FromMinutes(40));   // İstanbul 00:30
        var opp = await OpenAsync(s.Admin, s.CompanyId, "Doğrulama", 1_000m);

        // Yarın (firma günü) gelecekte bir kapanıştır.
        var future = await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version, ActualCloseDate: LocalToday.AddDays(1)));
        Assert.Contains("actualCloseDate", (await ProblemAsync(future)).Errors.Keys);

        // Yeniden iletişim tarihi firmanın bugününden sonra olmalı: bugün (UTC'ye göre "yarın") kabul edilmez.
        var sameDay = await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose",
            new LoseOpportunityRequestDto(opp.Version, "NoDecision", RecontactDate: LocalToday));
        Assert.Contains("recontactDate", (await ProblemAsync(sameDay)).Errors.Keys);

        var lost = (await ReadAsync<OpportunityActionResultDto>(await s.Admin.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose",
            new LoseOpportunityRequestDto(opp.Version, "NoDecision", RecontactDate: LocalToday.AddDays(1))))).Opportunity;
        Assert.Equal(LocalToday, lost.ActualCloseDate);
    }

    // ---- Tenant izolasyonu

    [Fact]
    public async Task Another_tenant_sees_no_cycle_sample_from_a_win_at_the_boundary()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        time.Advance(TimeSpan.FromMinutes(40));   // İstanbul 00:30
        await WinAsync(a.Admin, await OpenAsync(a.Admin, a.CompanyId, "A fırsatı", 1_000m));

        Assert.Empty((await GetAsync<AnalyticsDto<List<TeamPerformanceRowDto>>>(b.Admin, $"{Team}&date={LocalToday:yyyy-MM-dd}")).Data);
        var report = await ReportAsync(b.Admin);
        Assert.Equal(0, report.WonCount);
        Assert.Null(report.AverageCycleDays);

        // Kendi tenant'ında görünür.
        Assert.Equal(1, Assert.Single((await GetAsync<AnalyticsDto<List<TeamPerformanceRowDto>>>(a.Admin, $"{Team}&date={LocalToday:yyyy-MM-dd}")).Data).Performance.CycleSample);
    }

    // ---- Yetki

    [Fact]
    public async Task Winning_and_the_cycle_report_need_their_permissions_and_follow_data_scope_at_the_boundary()
    {
        var s = await SetUpAsync();
        time.Advance(TimeSpan.FromMinutes(40));   // İstanbul 00:30
        var opp = await OpenAsync(s.Admin, s.CompanyId, "Yöneticinin", 1_000m);

        // Yalnızca okuma izni olan kullanıcı fırsatı kazandıramaz; kapanış günü yazılmaz.
        var reader = await UserAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead], Scope.All);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version))).StatusCode);
        Assert.Null((await s.Admin.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opp.Id}"))!.ActualCloseDate);
        // Rapor izni olmadan ekip performansı okunamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Team}&date={LocalToday:yyyy-MM-dd}")).StatusCode);

        await WinAsync(s.Admin, opp);

        // Veri kapsamı Kendi: yöneticinin kazandığı fırsat ne ekip performansına ne fırsat raporuna girer.
        var rep = await UserAsync(s, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead, SalesReportsRead], Scope.Own);
        Assert.Empty((await GetAsync<AnalyticsDto<List<TeamPerformanceRowDto>>>(rep, $"{Team}&date={LocalToday:yyyy-MM-dd}")).Data);
        var report = await ReportAsync(rep);
        Assert.Equal(0, report.WonCount);
        Assert.Null(report.AverageCycleDays);
    }

    // ---- Yardımcılar

    private sealed record Setup(HttpClient Admin, Tenant Tenant, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
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

        var client = await host.LoginAsync(admin);
        var company = await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd.")), HttpStatusCode.Created);
        return new Setup(client, tenant, company.Id);
    }

    private async Task<HttpClient> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.Tenant.Id, permissions, scope, user);
        return await host.LoginAsync(user);
    }

    private static async Task<OpportunityDto> OpenAsync(HttpClient client, Guid companyId, string title, decimal amount) =>
        await ReadAsync<OpportunityDto>(await client.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, ExpectedCloseDate: LocalToday.AddDays(20))), HttpStatusCode.Created);

    private static async Task<OpportunityDto> WinAsync(HttpClient client, OpportunityDto opportunity) =>
        (await ReadAsync<OpportunityActionResultDto>(
            await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(opportunity.Version)))).Opportunity;

    /// <summary>Fırsat raporu: gece yarısının iki yanını da kapsayan dönem.</summary>
    private static async Task<OpportunityReportDto> ReportAsync(HttpClient client) =>
        await GetAsync<OpportunityReportDto>(client, $"/api/crm/opportunities/report?from={LocalYesterday:yyyy-MM-dd}&to={LocalToday:yyyy-MM-dd}");

    private static async Task<T> GetAsync<T>(HttpClient client, string url) => await ReadAsync<T>(await client.GetAsync(url));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
