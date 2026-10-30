using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 6: teklif raporu (son revizyon, para birimi bazlı toplam, kapsam, dönem) ve dışa aktarma (maliyet/iç not yok).</summary>
public class QuoteReportTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] RepPermissions = [QuotesRead, QuotesWrite, CompaniesRead];

    [Fact]
    public async Task The_report_counts_latest_revisions_by_state_and_never_mixes_currencies()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        await NewQuoteAsync(admin, s.CompanyId, price: 100m);                                  // taslak
        var pending = await NewQuoteAsync(admin, s.CompanyId, price: 200m);
        await TransitionAsync(admin, pending.Id, "submit-for-approval");                       // iç onay bekliyor
        var waiting = await PresentedAsync(admin, s.CompanyId, 1000m);                         // müşteride (TRY)
        var usd = await PresentedAsync(admin, s.CompanyId, 500m, currency: "USD");             // müşteride (USD)
        var accepted = await PresentedAsync(admin, s.CompanyId, 2000m, discount: 10m);
        await TransitionAsync(admin, accepted.Id, "record-accepted");
        var rejected = await PresentedAsync(admin, s.CompanyId, 300m);
        await TransitionAsync(admin, rejected.Id, "record-rejected", "Pahalı");
        var cancelled = await PresentedAsync(admin, s.CompanyId, 50m);
        await TransitionAsync(admin, cancelled.Id, "cancel", "Vazgeçildi");

        // Revizyonlu aile: V1 reddedildi, V2 sunuldu → aile yalnızca bir kez, son revizyonuyla sayılır (müşteride bekleyen).
        var v1 = await PresentedAsync(admin, s.CompanyId, 700m);
        await TransitionAsync(admin, v1.Id, "record-rejected", "Revize ister");
        var v2 = (await (await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/revise", new ReviseQuoteRequest("Yeni fiyat"))).Content.ReadFromJsonAsync<QuoteDto>())!;
        var v2Line = (await admin.GetFromJsonAsync<CrmPage<QuoteLineDto>>($"/api/crm/quote-lines?quoteId={v2.Id}"))!.Items.Single();
        Assert.True((await admin.PutAsJsonAsync($"/api/crm/quote-lines/{v2Line.Id}", ToSave(v2Line) with { UnitPrice = 650m })).IsSuccessStatusCode);
        await TransitionAsync(admin, v2.Id, "present");

        var report = await ReportAsync(admin);
        Assert.Equal((1, 1, 3), (report.DraftCount, report.PendingApprovalCount, report.WaitingCount));
        // Para birimleri ayrı: TRY'de iki (1.200 + 780), USD'de bir (600) bekleyen teklif.
        Assert.Equal((2, 1980m), (report.WaitingTotals.Single(t => t.Currency == "TRY").Count, report.WaitingTotals.Single(t => t.Currency == "TRY").Gross));
        Assert.Equal((1, 600m), (report.WaitingTotals.Single(t => t.Currency == "USD").Count, report.WaitingTotals.Single(t => t.Currency == "USD").Gross));
        Assert.Equal(2, report.WaitingTotals.Count);

        // V1 (reddedilmiş) sayılmaz; aile yalnızca son revizyonuyla bir kez.
        Assert.Equal((1, 1, 1, 1), (report.AcceptedCount, report.RejectedCount, report.CancelledCount, report.AcceptedTotals.Single().Count));
        Assert.Equal(2160m, report.AcceptedTotals.Single().Gross);
        Assert.Equal(6, report.PresentedCount);
        Assert.Equal(0.5, report.AcceptanceRate);
        Assert.Equal(1.67, report.AverageDiscountPercent);
        Assert.Equal(1, report.AverageRevisionsToAccept);
        Assert.Equal(3, report.NotDelivered.Count);                 // sunulmuş ve hiç gönderilmemiş
        Assert.Single(report.AwaitingApproval);
        Assert.NotEmpty(report.Definitions);

        // Sorumlu bazında tek sorumlu (yönetici) görünür.
        var owner = Assert.Single(report.ByOwner);
        Assert.Equal((6, 1, 1), (owner.Presented, owner.Accepted, owner.Rejected));
    }

    [Fact]
    public async Task The_report_follows_record_scope_and_the_selected_period()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);
        await NewQuoteAsync(admin, s.CompanyId, 100m);
        await PostAsync<QuoteDto>(rep, "/api/crm/quotes", new SaveQuoteRequest("Temsilci teklifi", s.CompanyId));   // temsilcinin serbest kalem yetkisi yok; boş taslak yeter
        await PresentedAsync(admin, s.CompanyId, 100m);

        // Temsilci (yalnızca kendi kayıtları) yöneticinin tekliflerini toplamlara hiç katmaz.
        var own = await ReportAsync(rep);
        Assert.Equal((1, 0, 0), (own.DraftCount, own.WaitingCount, own.PresentedCount));
        var all = await ReportAsync(admin);
        Assert.Equal((2, 1, 1), (all.DraftCount, all.WaitingCount, all.PresentedCount));

        // Sorumlu süzgeci ve dönem: geçmiş dönemde sunulan/sonuçlanan yok; "şu an" bölümü dönemden bağımsızdır.
        var past = await ReportAsync(admin, "?from=2020-01-01&to=2020-01-31");
        Assert.Equal((0, 0, 1), (past.PresentedCount, past.AcceptedCount, past.WaitingCount));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/api/crm/quotes/report?from=2026-02-01&to=2026-01-01")).StatusCode);

        // Rapor için izin gerekir; hiçbir izni olmayan 403 alır.
        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [CompaniesRead], Scope.All, stranger);
        using var noQuotes = await LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await noQuotes.GetAsync("/api/crm/quotes/report")).StatusCode);
    }

    [Fact]
    public async Task Export_needs_its_own_permission_and_never_contains_costs_or_internal_notes()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);
        var quote = await PresentedAsync(admin, s.CompanyId, 1000m, title: "=HYPERLINK(\"http://kotu.example\")", notes: "GIZLI-IC-NOT", cost: 777m);

        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/crm/quotes/export")).StatusCode);

        var response = await admin.GetAsync("/api/crm/quotes/export?latestOnly=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains(quote.QuoteNumber, csv);
        Assert.DoesNotContain("GIZLI-IC-NOT", csv);
        Assert.DoesNotContain("777", csv);
        Assert.DoesNotContain("unitCost", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internalNotes", csv, StringComparison.OrdinalIgnoreCase);
        // Formül karakteriyle başlayan hücre etkisizleştirilir.
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);

        // Rapor yanıtında da maliyet/iç not yok.
        var raw = await admin.GetStringAsync("/api/crm/quotes/report");
        Assert.DoesNotContain("GIZLI-IC-NOT", raw);
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task Margin_report_needs_the_cost_permission_and_computes_margin_from_line_costs()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);

        // 2 kalem: 1.000 (maliyet 600) + 500 (maliyet yok) → net 1.500, maliyet 600, marj 900 (%60), 1 kalemin maliyeti yok.
        var quote = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Marjlı", s.CompanyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await PostAsync<QuoteLineDto>(admin, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "A", "Free", null, 1m, 1000m, null, "None", 0m, 20m, null, "Standard", UnitCost: 600m));
        await PostAsync<QuoteLineDto>(admin, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "B", "Free", null, 1m, 500m, null, "None", 0m, 20m, null, "Standard"));
        await PostAsync<QuoteLineDto>(admin, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "Opsiyon", "Free", null, 1m, 9999m, null, "None", 0m, 20m, null, "Optional", UnitCost: 1m));
        await TransitionAsync(admin, quote.Id, "present");

        // Maliyet izni olmayan (temsilci) 403; genel raporda maliyet hiç yok.
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/crm/quotes/margin-report")).StatusCode);

        var response = await admin.GetAsync("/api/crm/quotes/margin-report");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var report = (await response.Content.ReadFromJsonAsync<QuoteMarginReportDto>())!;
        var total = Assert.Single(report.PresentedTotals);
        Assert.Equal(("TRY", 1, 1500m, 600m, 900m, 60.0, 1), (total.Currency, total.Count, total.Net, total.Cost, total.Margin, total.MarginPercent, total.LinesWithoutCost));
        Assert.Empty(report.AcceptedTotals);
        var item = Assert.Single(report.LowestMargin);
        Assert.Equal((quote.Id, 60.0, 1), (item.Id, item.MarginPercent, item.LinesWithoutCost));
        Assert.Single(report.MissingCost);
        Assert.NotEmpty(report.Definitions);
    }

    // ---- yardımcılar

    private static SaveQuoteLineRequest ToSave(QuoteLineDto l) => new(
        l.QuoteId, l.Name, l.Kind, l.ProductId, l.Quantity, l.UnitPrice, l.UnitId, l.DiscountKind, l.DiscountValue, l.TaxRatePercent, l.TaxCode,
        l.Role, l.IsSelected, l.ParentStableKey, l.RecurringPeriod, l.Description, l.GroupName, l.Position, l.UnitCost, l.Version);

    private static async Task<QuoteReportDto> ReportAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/crm/quotes/report{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteReportDto>())!;
    }

    private static async Task<QuoteDto> NewQuoteAsync(HttpClient client, Guid companyId, decimal price, string currency = "TRY", decimal discount = 0m, string? title = null, string? notes = null, decimal? cost = null)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes",
            new SaveQuoteRequest(title ?? "Teklif", companyId, Currency: currency, ValidUntil: DateTimeOffset.UtcNow.AddDays(30), InternalNotes: notes));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 1m, price, null, discount > 0 ? "Percent" : "None", discount, 20m, null, "Standard", UnitCost: cost));
        return await GetQuoteAsync(client, quote.Id);
    }

    private static async Task<QuoteDto> PresentedAsync(HttpClient client, Guid companyId, decimal price, string currency = "TRY", decimal discount = 0m, string? title = null, string? notes = null, decimal? cost = null)
    {
        var quote = await NewQuoteAsync(client, companyId, price, currency, discount, title, notes, cost);
        return await TransitionAsync(client, quote.Id, "present");
    }

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action, string? reason = null)
    {
        var route = action switch { "record-accepted" => "record-accepted", "record-rejected" => "record-rejected", _ => action };
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{route}", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep, Guid CompanyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, RepPermissions, Scope.Own, rep);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, rep, company.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
