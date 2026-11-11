using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using Modulix.Shared.Reporting;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0035 dilim 3: ortak rapor merkezi ve sınırlı oluşturucu. Mutlu yol, hata/doğrulama, tenant izolasyonu ve yetki; paylaşım ve sonradan yetki iptali,
/// hassas alan, veri kapsamı, dinamik alan (yeniden adlandırma/kaldırma) ve kart/özet = ayrıntı tutarlılığı.
/// </summary>
public class ReportCenterTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Reports = "/api/reports";
    private const string Opportunities = "crm.opportunities";
    private static readonly string[] DataRead = [CompaniesRead, OpportunitiesRead, LeadsRead];

    private static readonly string[] CoreRead = [CoreModule.Permissions.ReadReports];

    // ---- Katalog

    [Fact]
    public async Task Catalog_lists_the_datasets_columns_ready_reports_and_links_the_user_may_use()
    {
        var s = await SetUpAsync();

        var catalog = await ReadAsync<ReportCatalogDto>(await s.Admin.GetAsync($"{Reports}/catalog"));

        var dataset = catalog.Datasets.Single(d => d.Key == Opportunities);
        Assert.True(dataset.Available);
        Assert.Equal("crm/firsatlar/{id}", dataset.RecordPath);
        Assert.Contains(dataset.Columns, c => c is { Field: "amount", Kind: "money", CurrencyField: "currency" } && c.Use.Contains("measure"));
        Assert.Contains(dataset.Columns, c => c.Field == "stage" && c.Options is { Count: > 0 });
        Assert.NotEmpty(dataset.ReadyReports);
        Assert.Contains(catalog.Datasets, d => d.Key == "crm.leads");
        Assert.Contains(catalog.Links, l => l.Key == "crm.reports.sales-overview");
    }

    [Fact]
    public async Task Catalog_hides_sensitive_columns_ready_reports_and_links_the_user_has_no_permission_for()
    {
        var s = await SetUpAsync();
        var viewer = await UserAsync(s, [.. DataRead, .. CoreRead], Scope.All);

        var catalog = await ReadAsync<ReportCatalogDto>(await viewer.GetAsync($"{Reports}/catalog"));

        var dataset = catalog.Datasets.Single(d => d.Key == Opportunities);
        Assert.DoesNotContain(dataset.Columns, c => c.Field is "amount" or "wonAmount" or "probability");
        // Tutarı kullanan hazır rapor listelenmez, yalnızca sayıya dayananlar kalır.
        Assert.DoesNotContain(dataset.ReadyReports, r => r.Spec.Measures.Any(m => m.Field == "amount"));
        Assert.Contains(dataset.ReadyReports, r => r.Key == "crm.opportunities.loss-reasons");
        Assert.Empty(catalog.Links);   // satış raporu izni yok
    }

    [Fact]
    public async Task A_dataset_without_read_permission_is_listed_as_unavailable_with_a_reason()
    {
        var s = await SetUpAsync();
        var viewer = await UserAsync(s, [CompaniesRead, OpportunitiesRead, .. CoreRead], Scope.All);   // lead izni yok

        var leads = (await ReadAsync<ReportCatalogDto>(await viewer.GetAsync($"{Reports}/catalog"))).Datasets.Single(d => d.Key == "crm.leads");

        Assert.False(leads.Available);
        Assert.NotNull(leads.UnavailableReason);
        Assert.Empty(leads.Columns);
    }

    // ---- Çalıştırma: özet, kart = ayrıntı

    [Fact]
    public async Task Summary_splits_amounts_by_currency_and_the_drilled_detail_total_equals_the_summary_row()
    {
        var s = await SetUpAsync();
        var a = await OpenAsync(s.Admin, s.CompanyId, "A", 100m);
        var b = await OpenAsync(s.Admin, s.CompanyId, "B", 200m);
        await OpenAsync(s.Admin, s.CompanyId, "C", 50m, "USD");
        var d = await OpenAsync(s.Admin, s.CompanyId, "D", 300m);
        await StageAsync(s.Admin, d, "Qualified");
        await WinAsync(s.Admin, await OpenAsync(s.Admin, s.CompanyId, "E", 999m));

        var spec = OpenByStage();
        var result = await RunAsync(s.Admin, spec);

        Assert.Equal("ok", result.Status);
        Assert.True(result.IsSummary);
        Assert.Equal(["stage", "currency", "count", "sum:amount"], result.Columns.Select(c => c.Key));
        var leadTry = result.Rows.Single(r => r.Keys[0] == "Lead" && r.Currency == "TRY");
        var leadUsd = result.Rows.Single(r => r.Keys[0] == "Lead" && r.Currency == "USD");
        Assert.Equal((2m, 300m), (Cell(leadTry, 2), Cell(leadTry, 3)));
        Assert.Equal((1m, 50m), (Cell(leadUsd, 2), Cell(leadUsd, 3)));
        Assert.Equal(3, result.Rows.Count);   // kazanılan E ve kapalılar hariç
        // TRY toplamı USD ile birleşmez.
        Assert.Equal(600m, Cell(result.Totals.Single(t => t.Currency == "TRY"), 3));
        Assert.Equal(50m, Cell(result.Totals.Single(t => t.Currency == "USD"), 3));

        // Ayrıntı: aynı süzgeç + grup anahtarı; toplamı özet satırıyla eşit, kayıtlar aynı fırsatlar.
        var detail = await RunAsync(s.Admin, spec, new ReportDrillDto(leadTry.Keys, leadTry.Currency));
        Assert.False(detail.IsSummary);
        Assert.Equal(2, detail.RowsMatched);
        Assert.Equal(Cell(leadTry, 3), Cell(detail.Totals.Single(), 3));
        Assert.Equal([a.Id, b.Id], detail.Rows.Select(r => r.RecordId!.Value).Order());
        Assert.Equal("crm/firsatlar/{id}", detail.RecordPath);
    }

    [Fact]
    public async Task Table_report_filters_sorts_pages_and_resolves_choice_and_lookup_labels()
    {
        var s = await SetUpAsync();
        foreach (var (title, amount) in new[] { ("Alfa", 300m), ("Beta", 100m), ("Gama", 200m) })
        {
            await OpenAsync(s.Admin, s.CompanyId, title, amount);
        }

        var spec = new ReportSpecDto
        {
            Dataset = Opportunities,
            Columns = ["title", "stage", "companyId", "amount", "ownerId"],
            Filters = [new() { Field = "amount", Operator = "greaterOrEqual", Value = "150" }],
            Sort = new() { Field = "amount", Direction = "descending" },
        };

        var page1 = await RunAsync(s.Admin, spec, pageSize: 1);

        Assert.Equal(2, page1.RowsMatched);
        Assert.Equal(("Alfa", 300m), (page1.Rows.Single().Cells[0].GetString(), page1.Rows.Single().Cells[3].GetDecimal()));
        Assert.Equal("Anka Ltd.", page1.Rows.Single().Cells[2].GetString());   // lookup etiketi, kimlik değil
        Assert.NotEqual("Lead", page1.Rows.Single().Cells[1].GetString());   // seçim anahtarı değil firma etiketi
        Assert.Equal("TRY", page1.Rows.Single().Currency);
        var page2 = await RunAsync(s.Admin, spec, page: 2, pageSize: 1);
        Assert.Equal("Gama", page2.Rows.Single().Cells[0].GetString());
    }

    [Fact]
    public async Task No_data_and_a_real_zero_are_distinguishable()
    {
        var s = await SetUpAsync();
        var spec = new ReportSpecDto
        {
            Dataset = Opportunities,
            Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }],
        };

        var empty = await RunAsync(s.Admin, spec);

        Assert.Equal("ok", empty.Status);
        Assert.Equal(0, empty.RowsMatched);
        Assert.Equal(0m, Cell(empty.Totals.Single(), 1));   // adet gerçek sıfır (0: para birimi)
        Assert.Equal(JsonValueKind.Null, empty.Totals.Single().Cells[2].ValueKind);   // tutar toplamı: veri yok
    }

    [Fact]
    public async Task Records_without_amount_are_excluded_and_reported_not_assumed_zero()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Tutarlı", 100m);
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Tutarsız", s.CompanyId));

        var result = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = Opportunities,
            Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }],
        });

        Assert.Equal(2m, Cell(result.Totals.Single(), 1));
        Assert.Equal(100m, Cell(result.Totals.Single(), 2));
        Assert.Equal(1, result.Columns.Single(c => c.Key == "sum:amount").ExcludedRows);
        Assert.Contains(result.Issues, i => i.Code == "excludedRows" && !i.Blocking);
    }

    [Fact]
    public async Task Date_group_uses_the_requested_grain_and_choice_filters_are_case_tolerant()
    {
        var s = await SetUpAsync();
        var inTwoMonths = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2);
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest("Aylık", s.CompanyId, Amount: 10m, ExpectedCloseDate: inTwoMonths));

        var result = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = Opportunities,
            Filters = [new() { Field = "status", Operator = "equals", Value = "open" }],
            Groups = [new() { Field = "expectedCloseDate", Grain = "month" }],
            Chart = "line",
        });

        Assert.Equal(inTwoMonths.ToString("yyyy-MM"), result.Rows.Single().Keys[0]);
        Assert.Equal("line", result.Chart);
    }

    [Fact]
    public async Task Lead_dataset_groups_by_source()
    {
        var s = await SetUpAsync();
        foreach (var (subject, source, email) in new[] { ("L1", "Referral", "a@x.test"), ("L2", "Referral", "b@x.test"), ("L3", "Event", "c@x.test") })
        {
            await PostAsync<LeadDto>(s.Admin, "/api/crm/leads", new SaveLeadRequest(subject, LastName: "Kaya", Email: email, Source: source));
        }

        var result = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.leads", Groups = [new() { Field = "source" }], Sort = new() { Field = "count", Direction = "descending" } });

        Assert.Equal("Referral", result.Rows[0].Keys[0]);
        Assert.Equal(2m, Cell(result.Rows[0], 1));
        Assert.Equal(3m, Cell(result.Totals.Single(), 1));
    }

    [Fact]
    public async Task Quote_dataset_sums_the_net_total_by_status_and_company_dataset_counts_by_industry()
    {
        var s = await SetUpAsync();
        foreach (var price in new[] { 100m, 250m })
        {
            var quote = await PostAsync<QuoteDto>(s.Admin, "/api/crm/quotes", new SaveQuoteRequest("Teklif", s.CompanyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
            await PostAsync<QuoteLineDto>(s.Admin, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 1m, price, null, "None", 0m, 20m, null, "Standard"));
        }

        foreach (var (name, industry) in new[] { ("Yazılım A", "Yazılım"), ("Yazılım B", "Yazılım"), ("Lojistik C", "Lojistik") })
        {
            await PostAsync<CompanyDto>(s.Admin, "/api/crm/companies", new SaveCompanyRequest(name, Industry: industry));
        }

        var catalog = await ReadAsync<ReportCatalogDto>(await s.Admin.GetAsync($"{Reports}/catalog"));
        Assert.Contains(catalog.Datasets, d => d is { Key: "crm.quotes", Available: true } && d.ReadyReports.Count == 3);
        Assert.Contains(catalog.Datasets, d => d is { Key: "crm.companies", Available: true } && d.Columns.Any(c => c.Field == "industry"));

        var quotes = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = "crm.quotes",
            Groups = [new() { Field = "status" }],
            Measures = [new() { Aggregation = "count" }, new() { Field = "netTotal", Aggregation = "sum" }],
        });
        var draft = Assert.Single(quotes.Rows);
        Assert.Equal(("Draft", "TRY", 2m, 350m), (draft.Keys[0], draft.Currency, Cell(draft, 2), Cell(draft, 3)));
        Assert.Equal("Taslak", draft.Cells[0].GetString());

        var companies = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = "crm.companies",
            Groups = [new() { Field = "industry" }],
            Measures = [new() { Aggregation = "count" }],
            Sort = new() { Field = "count", Direction = "descending" },
        });
        Assert.Equal(("Yazılım", 2m), (companies.Rows[0].Keys[0], Cell(companies.Rows[0], 1)));   // azalan sayıda ilk satır
        Assert.Equal(1m, Cell(companies.Rows.Single(r => r.Keys[0] == "Lojistik"), 1));
        // "Anka Ltd." sektörsüz: boş grup olarak ayrı satırdır.
        Assert.Contains(companies.Rows, r => r.Keys[0] is null && Cell(r, 1) == 1m);

        // Başka tenant bu teklif ve firmaları görmez; teklif izni olmayan teklif veri kümesini kullanamaz.
        var other = await SetUpAsync();
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.quotes", Columns = ["title"] })).Rows);
        var noQuotes = await UserAsync(s, [CompaniesRead, .. CoreRead], Scope.All);
        Assert.Equal("forbidden", (await RunAsync(noQuotes, new ReportSpecDto { Dataset = "crm.quotes", Columns = ["title"] })).Status);
    }

    [Fact]
    public async Task Target_and_forecast_datasets_show_user_names_exclude_count_targets_from_amount_sums_and_hide_the_locked_amount_without_permission()
    {
        var s = await SetUpAsync();
        var (start, end) = (new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        foreach (var target in new[]
        {
            new SaveSalesTargetRequest("WonAmount", "User", s.AdminId, "TRY", start, end, 600_000m),
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", start, end, 1_000_000m),
            new SaveSalesTargetRequest("WonCount", "Tenant", null, null, start, end, 4m),
        })
        {
            await PostAsync<SalesTargetDto>(s.Admin, "/api/crm/sales-targets", target);
        }

        await PostAsync<ForecastHeaderDto>(s.Admin, "/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", DateOnly.FromDateTime(DateTime.UtcNow), "Tenant", null, "TRY"));

        // Kullanıcı kapsamlı tutar hedefi: grup etiketi kimlik değil kullanıcının adıdır.
        var byUser = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = "crm.sales-targets",
            Filters = [new() { Field = "metric", Operator = "equals", Value = "WonAmount" }, new() { Field = "scopeType", Operator = "equals", Value = "User" }],
            Groups = [new() { Field = "scopeUserId" }],
            Measures = [new() { Field = "value", Aggregation = "sum" }],
        });
        var row = Assert.Single(byUser.Rows);
        Assert.Equal(s.AdminId.ToString(), row.Keys[0], ignoreCase: true);
        Assert.NotEqual(row.Keys[0], row.Cells[0].GetString());
        Assert.Equal(600_000m, Cell(row, 2));

        // Adet hedefinin para birimi yok: tutar toplamına girmez ve dışlanan kayıt olarak sayılır (sıfır varsayılmaz).
        var total = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.sales-targets", Measures = [new() { Field = "value", Aggregation = "sum" }] });
        Assert.Equal(1_600_000m, Cell(total.Totals.Single(t => t.Currency == "TRY"), 1));
        Assert.Equal(1, total.Columns.Single(c => c.Key == "sum:value").ExcludedRows);

        var forecasts = await RunAsync(s.Admin, new ReportSpecDto
        {
            Dataset = "crm.forecasts",
            Groups = [new() { Field = "status" }],
            Measures = [new() { Aggregation = "count" }],
        });
        Assert.Equal(("Draft", "Taslak", 1m), (forecasts.Rows.Single().Keys[0], forecasts.Rows.Single().Cells[0].GetString(), Cell(forecasts.Rows.Single(), 1)));

        // Katalog: kullanıcı sütunu etkin üyeleri seçenek olarak verir; kilit anındaki tutar yalnızca tutar iznine sahip olana görünür.
        var catalog = await ReadAsync<ReportCatalogDto>(await s.Admin.GetAsync($"{Reports}/catalog"));
        var users = catalog.Datasets.Single(d => d.Key == "crm.sales-targets").Columns.Single(c => c.Field == "scopeUserId");
        Assert.Equal("user", users.Kind);
        Assert.Contains(users.Options!, o => string.Equals(o.Key, s.AdminId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.Datasets.Single(d => d.Key == "crm.forecasts").Columns, c => c.Field == "realizedAtLock");

        var planner = await UserAsync(s, [ForecastsRead, SalesTargetsRead, .. CoreRead], Scope.All);
        var limited = await ReadAsync<ReportCatalogDto>(await planner.GetAsync($"{Reports}/catalog"));
        Assert.DoesNotContain(limited.Datasets.Single(d => d.Key == "crm.forecasts").Columns, c => c.Field == "realizedAtLock");
        var blocked = await RunAsync(planner, new ReportSpecDto { Dataset = "crm.forecasts", Measures = [new() { Field = "realizedAtLock", Aggregation = "sum" }] });
        Assert.Equal("forbidden", blocked.Status);

        // Başka tenant bu hedef ve tahminleri görmez.
        var other = await SetUpAsync();
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.sales-targets", Columns = ["metric"] })).Rows);
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.forecasts", Columns = ["status"] })).Rows);
    }

    [Fact]
    public async Task Forecast_item_and_target_revision_datasets_are_read_only_hide_amounts_without_permission_and_stay_inside_the_tenant()
    {
        var s = await SetUpAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Tahmin satırı: bugün kapanması beklenen açık fırsat sürümün satırı olur.
        await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities",
            new SaveOpportunityRequest("Satırlı fırsat", s.CompanyId, Amount: 70_000m, Currency: "TRY", ExpectedCloseDate: today));
        var forecast = await PostAsync<ForecastDetailDto>(s.Admin, "/api/crm/sales/forecasts", new CreateForecastRequestDto("Year", today, "Tenant", null, "TRY"));

        // Hedef revizyonu: değer değişince eski/yeni değer ve gerekçe revizyon olarak okunur.
        var target = await PostAsync<SalesTargetDto>(s.Admin, "/api/crm/sales-targets",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31), 1_000m));
        Assert.True((await s.Admin.PutAsJsonAsync($"/api/crm/sales-targets/{target.Id}",
            new SaveSalesTargetRequest("WonAmount", "Tenant", null, "TRY", new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31), 1_500m,
                ChangeReason: "Kampanya geldi", Version: target.Version))).IsSuccessStatusCode);

        var items = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.forecast-items", Columns = ["title", "sourceAmount"] });
        Assert.Equal("Satırlı fırsat", Assert.Single(items.Rows).Cells[0].GetString());

        // Düzeltme geçmişi: kategori düzeltmesi gerekçesiyle okunur.
        var adjusted = await s.Admin.PostAsJsonAsync($"/api/crm/sales/forecasts/{forecast.Header.Id}/adjust", new AdjustForecastRequestDto(forecast.Items.Single().Id, "Category", "Commit", "Müşteri onayladı"));
        Assert.True(adjusted.IsSuccessStatusCode, await adjusted.Content.ReadAsStringAsync());
        var adjustments = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.forecast-adjustments", Columns = ["kind", "toValue", "reason"] });
        var adjustment = Assert.Single(adjustments.Rows);
        Assert.Equal(("Kategori", "Commit", "Müşteri onayladı"), (adjustment.Cells[0].GetString(), adjustment.Cells[1].GetString(), adjustment.Cells[2].GetString()));
        var revisions = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.sales-target-revisions", Columns = ["revisionNo", "oldValue", "newValue", "reason"] });
        var revision = Assert.Single(revisions.Rows);
        Assert.Equal(("Kampanya geldi", 1_000m, 1_500m), (revision.Cells[3].GetString(), Cell(revision, 1), Cell(revision, 2)));

        // Tutar izni olmayan çağıran tahmin satırının tutar/olasılık sütunlarını katalogda görmez ve kullanamaz; satır başlığını görür.
        var planner = await UserAsync(s, [ForecastsRead, SalesTargetsRead, OpportunitiesRead, .. CoreRead], Scope.All);
        var catalog = await ReadAsync<ReportCatalogDto>(await planner.GetAsync($"{Reports}/catalog"));
        Assert.DoesNotContain(catalog.Datasets.Single(d => d.Key == "crm.forecast-items").Columns, c => c.Field == "sourceAmount");
        Assert.DoesNotContain(catalog.Datasets.Single(d => d.Key == "crm.forecast-adjustments").Columns, c => c.Field is "fromValue" or "toValue");
        Assert.Equal("forbidden", (await RunAsync(planner, new ReportSpecDto { Dataset = "crm.forecast-items", Measures = [new() { Field = "sourceAmount", Aggregation = "sum" }] })).Status);

        // Yetkisiz çağıran (tahmin/hedef okuma izni yok) iki veri kümesini de okuyamaz.
        var outsider = await UserAsync(s, [CompaniesRead, .. CoreRead], Scope.All);
        Assert.Equal("forbidden", (await RunAsync(outsider, new ReportSpecDto { Dataset = "crm.forecast-items", Columns = ["title"] })).Status);
        Assert.Equal("forbidden", (await RunAsync(outsider, new ReportSpecDto { Dataset = "crm.forecast-adjustments", Columns = ["reason"] })).Status);
        Assert.Equal("forbidden", (await RunAsync(outsider, new ReportSpecDto { Dataset = "crm.sales-target-revisions", Columns = ["reason"] })).Status);

        // Başka tenant hiçbirini görmez.
        var other = await SetUpAsync();
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.forecast-items", Columns = ["title"] })).Rows);
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.forecast-adjustments", Columns = ["reason"] })).Rows);
        Assert.Empty((await RunAsync(other.Admin, new ReportSpecDto { Dataset = "crm.sales-target-revisions", Columns = ["reason"] })).Rows);
    }

    // ---- Doğrulama

    [Fact]
    public async Task Invalid_definitions_are_reported_with_issues_and_read_no_data()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "X", 1m);

        var unknown = await RunAsync(s.Admin, new ReportSpecDto { Dataset = Opportunities, Columns = ["nope"] });
        var badOperator = await RunAsync(s.Admin, new ReportSpecDto { Dataset = Opportunities, Filters = [new() { Field = "title", Operator = "greater", Value = "1" }] });
        var missing = await RunAsync(s.Admin, new ReportSpecDto { Dataset = "crm.nope" });

        Assert.Equal("invalid", unknown.Status);
        Assert.Equal(0, unknown.RowsRead);
        Assert.Contains(unknown.Issues, i => i is { Code: "unknownColumn", Field: "nope" });
        Assert.Contains(badOperator.Issues, i => i.Code == "invalidOperator");
        Assert.Equal("unknownDataset", missing.Status);
        // Bilinmeyen işleç metni sözleşme dışıdır → 400.
        var raw = await s.Admin.PostAsJsonAsync($"{Reports}/run", new { spec = new { dataset = Opportunities, filters = new[] { new { field = "title", @operator = "between", value = "1" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync($"{Reports}/run", new RunReportRequest(null, null))).StatusCode);
    }

    // ---- Hassas alan ve veri kapsamı (kabul senaryosu 7)

    [Fact]
    public async Task Amounts_do_not_leak_through_totals_filters_or_groups_without_the_amount_permission()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Gizli tutar", 123_456m);
        var viewer = await UserAsync(s, [.. DataRead, .. CoreRead], Scope.All);

        var sum = await RunAsync(viewer, new ReportSpecDto { Dataset = Opportunities, Measures = [new() { Field = "amount", Aggregation = "sum" }] });
        var filter = await RunAsync(viewer, new ReportSpecDto { Dataset = Opportunities, Columns = ["title"], Filters = [new() { Field = "amount", Operator = "greater", Value = "100000" }] });
        var column = await RunAsync(viewer, new ReportSpecDto { Dataset = Opportunities, Columns = ["title", "amount"] });

        foreach (var result in new[] { sum, filter, column })
        {
            Assert.Equal("forbidden", result.Status);
            Assert.Empty(result.Rows);
            Assert.Contains(result.Issues, i => i.Code == "notPermitted");
            Assert.DoesNotContain("123456", JsonSerializer.Serialize(result));
        }

        // Sayıya dayalı rapor çalışır ve tutar sızdırmaz.
        var counts = await RunAsync(viewer, new ReportSpecDto { Dataset = Opportunities, Measures = [new() { Aggregation = "count" }] });
        Assert.Equal("ok", counts.Status);
        Assert.Equal(1m, Cell(counts.Totals.Single(), 0));
    }

    [Fact]
    public async Task Each_caller_sees_only_the_records_in_their_data_scope()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Yöneticinin", 10m);
        var own = await UserAsync(s, [.. DataRead, OpportunitiesWrite, CompaniesWrite, OpportunityAmountsRead, .. CoreRead], Scope.Own);
        var ownCompany = await PostAsync<CompanyDto>(own, "/api/crm/companies", new SaveCompanyRequest("Temsilci Ltd."));
        var mine = await OpenAsync(own, ownCompany.Id, "Temsilcinin", 20m);
        var count = new ReportSpecDto { Dataset = Opportunities, Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }] };

        var adminResult = await RunAsync(s.Admin, count);
        var ownResult = await RunAsync(own, count);

        Assert.Equal((2m, 30m), (Cell(adminResult.Totals.Single(), 1), Cell(adminResult.Totals.Single(), 2)));
        Assert.Equal((1m, 20m), (Cell(ownResult.Totals.Single(), 1), Cell(ownResult.Totals.Single(), 2)));
        Assert.NotEqual(Guid.Empty, mine.Id);
    }

    [Fact]
    public async Task Bulk_pages_hold_more_records_but_apply_the_same_scope_rules_and_the_report_reads_past_one_page()
    {
        var s = await SetUpAsync();
        for (var i = 0; i < 205; i++)
        {
            await OpenAsync(s.Admin, s.CompanyId, $"Toplu {i}", 1m);
        }

        var userEntity = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, OpportunitiesWrite, CompaniesWrite, OpportunityAmountsRead, .. CoreRead], Scope.Own, userEntity);
        var rep = await LoginAsync(userEntity);
        var repCompany = await PostAsync<CompanyDto>(rep, "/api/crm/companies", new SaveCompanyRequest("Bulk Ltd."));
        for (var i = 0; i < 3; i++)
        {
            await OpenAsync(rep, repCompany.Id, $"Temsilci {i}", 1m);
        }

        // Rapor 200'den fazla kaydı (sayfa sınırı aşılsa da) eksiksiz okur.
        var count = new ReportSpecDto { Dataset = Opportunities, Measures = [new() { Aggregation = "count" }] };
        var all = await RunAsync(s.Admin, count);
        Assert.Equal((208, false), (all.RowsRead, all.Truncated));
        Assert.Equal(208m, Cell(all.Totals.Single(), 0));

        // Doğrudan boru hattı: toplu okuma 1000'e kadar sayfa verir, olağan okuma 200'de kalır; kapsam kuralı ikisinde de aynıdır.
        var membershipId = await factory.GetMembershipIdAsync(userEntity.Id, s.TenantId);
        var admin = await factory.GetMembershipIdAsync(s.AdminId, s.TenantId);
        async Task<int> ReadAsync(Guid userId, Guid membership, bool bulk)
        {
            await using var owner = factory.Services.GetRequiredService<Modulix.Infrastructure.Platform.ITenantScopeFactory>()
                .Create(new Modulix.Core.Contracts.Tenancy.TenantContext(s.TenantId, userId, membership));
            var pipeline = owner.ServiceProvider.GetRequiredService<Modulix.Core.Contracts.Records.IRecordPipeline>();
            var result = await pipeline.QueryAsync(
                new Modulix.Core.Contracts.Records.RecordQuery("crm.opportunity", 1, 1000) { Bulk = bulk }, CancellationToken.None);
            return result.Value!.Items.Count;
        }

        Assert.Equal(200, await ReadAsync(s.AdminId, admin, bulk: false));
        Assert.Equal(208, await ReadAsync(s.AdminId, admin, bulk: true));
        Assert.Equal(3, await ReadAsync(userEntity.Id, membershipId, bulk: true));   // kapsam: yalnızca kendi kayıtları
    }

    // ---- Kayıtlı rapor, sürüm ve doğrulama

    [Fact]
    public async Task Saved_report_round_trips_and_runs_with_the_stored_definition()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 100m);

        var saved = await SaveAsync(s.Admin, "Aşama raporu", OpenByStage());
        var loaded = await ReadAsync<ReportDetailDto>(await s.Admin.GetAsync($"{Reports}/{saved.Summary.Id}"));

        Assert.Equal("ok", loaded.Summary.Health);
        Assert.Equal("private", loaded.Summary.Visibility);
        Assert.True(loaded.Summary.IsOwner);
        Assert.Equal("Fırsatlar", loaded.Summary.DatasetName);
        Assert.Equal(["stage"], loaded.Spec.Groups.Select(g => g.Field));
        Assert.Equal("sum", loaded.Spec.Measures[1].Aggregation);
        var run = await ReadAsync<ReportResultDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{saved.Summary.Id}/run", new RunReportRequest(null, null)));
        Assert.Equal(1m, Cell(run.Rows.Single(), 2));
    }

    [Fact]
    public async Task Saving_validates_name_uniqueness_definition_and_version()
    {
        var s = await SetUpAsync();
        var saved = await SaveAsync(s.Admin, "Rapor 1", OpenByStage());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync(Reports, new SaveReportRequest("Rapor 1", null, OpenByStage()))).StatusCode);   // ad tekil
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync(Reports, new SaveReportRequest("  ", null, OpenByStage()))).StatusCode);
        var bad = await s.Admin.PostAsJsonAsync(Reports, new SaveReportRequest("Bozuk", null, new ReportSpecDto { Dataset = Opportunities, Groups = [new() { Field = "nope" }] }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("unknownColumn", await bad.Content.ReadAsStringAsync());

        var update = await s.Admin.PutAsJsonAsync($"{Reports}/{saved.Summary.Id}", new SaveReportRequest("Rapor 1b", "Açıklama", OpenByStage(), saved.Summary.Version));
        var updated = await ReadAsync<ReportDetailDto>(update);
        Assert.Equal((2L, "Rapor 1b"), (updated.Summary.Version, updated.Summary.Name));
        // Eski sürümle yazma çakışır.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PutAsJsonAsync($"{Reports}/{saved.Summary.Id}", new SaveReportRequest("X", null, OpenByStage(), saved.Summary.Version))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"{Reports}/{saved.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"{Reports}/{saved.Summary.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_save_a_report_that_uses_a_column_they_may_not_see()
    {
        var s = await SetUpAsync();
        var author = await UserAsync(s, [.. DataRead, .. CoreRead, CoreModule.Permissions.ManageReports], Scope.All);

        var response = await author.PostAsJsonAsync(Reports, new SaveReportRequest("Tutarlı", null, OpenByStage()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("notPermitted", await response.Content.ReadAsStringAsync());
    }

    // ---- Paylaşım, favori ve sonradan yetki iptali (kabul senaryosu 7)

    [Fact]
    public async Task Sharing_makes_the_definition_visible_but_never_grants_data_access_and_revocation_is_immediate()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 100m);
        var owner = await UserAsync(s, [.. DataRead, OpportunityAmountsRead, CoreModule.Permissions.ReadReports, CoreModule.Permissions.ManageReports, CoreModule.Permissions.ShareReports], Scope.All);
        var userTwo = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. CoreRead], Scope.All, userTwo);   // tutar izni yok
        var two = await LoginAsync(userTwo);
        var three = await UserAsync(s, [.. DataRead, OpportunityAmountsRead, .. CoreRead], Scope.All);

        var report = await SaveAsync(owner, "Paylaşılan", OpenByStage());
        var id = report.Summary.Id;

        // Özel: başkası bulamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await two.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.DoesNotContain((await ListAsync(two, "all")).Items, r => r.Id == id);

        // Seçilen kullanıcıyla paylaş: yalnız o görür; tanımı görür ama tutar izni olmadığı için çalıştıramaz.
        var shared = await ReadAsync<ReportDetailDto>(await owner.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("users", [userTwo.Id])));
        Assert.Equal([userTwo.Id], shared.SharedWith);
        Assert.Equal(HttpStatusCode.OK, (await two.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await three.GetAsync($"{Reports}/{id}")).StatusCode);
        var blocked = await ReadAsync<ReportResultDto>(await two.PostAsJsonAsync($"{Reports}/{id}/run", new RunReportRequest(null, null)));
        Assert.Equal("forbidden", blocked.Status);
        Assert.Empty(blocked.Rows);
        var inList = Assert.Single((await ListAsync(two, "shared")).Items, r => r.Id == id);
        Assert.Equal("columnsInvalid", inList.Health);   // tanım görünür, sütun yetkisi yok
        Assert.False(inList.IsOwner);
        Assert.Empty((await ReadAsync<ReportDetailDto>(await two.GetAsync($"{Reports}/{id}"))).SharedWith);   // paylaşım listesini yalnız sahibi görür

        // Paylaşılan kişi değiştiremez, silemez, paylaşım ayarlayamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await two.PutAsJsonAsync($"{Reports}/{id}", new SaveReportRequest("Hırsız", null, OpenByStage()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await two.DeleteAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await two.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("tenant", null))).StatusCode);

        // Firmaya aç: tutar iznine sahip üçüncü kişi görür ve çalıştırır.
        await ReadAsync<ReportDetailDto>(await owner.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("tenant", null)));
        var ran = await ReadAsync<ReportResultDto>(await three.PostAsJsonAsync($"{Reports}/{id}/run", new RunReportRequest(null, null)));
        Assert.Equal("ok", ran.Status);

        // Yönetme ve paylaşma izni olsa bile sahibi olmayan rapor değiştirilemez, silinemez, paylaşımı ayarlanamaz.
        var editor = await UserAsync(s, [.. DataRead, OpportunityAmountsRead, CoreModule.Permissions.ReadReports, CoreModule.Permissions.ManageReports, CoreModule.Permissions.ShareReports], Scope.All);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"{Reports}/{id}", new SaveReportRequest("Ele geçir", null, OpenByStage()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("private", null))).StatusCode);

        // Favori kişiye özeldir.
        Assert.Equal(HttpStatusCode.OK, (await three.PutAsJsonAsync($"{Reports}/{id}/favorite", new FavoriteReportRequest(true))).StatusCode);
        Assert.Contains((await ListAsync(three, "favorites")).Items, r => r.Id == id && r.IsFavorite);
        Assert.DoesNotContain((await ListAsync(two, "favorites")).Items, r => r.Id == id);

        // Paylaşımı kaldır: erişim hemen biter (tanım, çalıştırma, favori listesi).
        await ReadAsync<ReportDetailDto>(await owner.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("private", null)));
        Assert.Equal(HttpStatusCode.NotFound, (await three.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await three.PostAsJsonAsync($"{Reports}/{id}/run", new RunReportRequest(null, null))).StatusCode);
        Assert.DoesNotContain((await ListAsync(three, "favorites")).Items, r => r.Id == id);
        Assert.Equal(HttpStatusCode.NotFound, (await two.PutAsJsonAsync($"{Reports}/{id}/favorite", new FavoriteReportRequest(true))).StatusCode);
    }

    [Fact]
    public async Task Data_permission_revoked_after_sharing_blocks_the_run_at_once()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 100m);
        var report = await SaveAsync(s.Admin, "Herkese", OpenByStage());
        await ReadAsync<ReportDetailDto>(await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("tenant", null)));
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, OpportunityAmountsRead, .. CoreRead], Scope.All, user);
        var client = await LoginAsync(user);
        Assert.Equal("ok", (await ReadAsync<ReportResultDto>(await client.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/run", new RunReportRequest(null, null)))).Status);

        // Yönetici kullanıcının fırsat okuma iznini kaldırır (aynı oturum, bir sonraki istek).
        await factory.CreateRoleAsync(s.TenantId, [.. CoreRead], Scope.All, user);

        var after = await ReadAsync<ReportResultDto>(await client.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/run", new RunReportRequest(null, null)));
        Assert.Equal("forbidden", after.Status);
        Assert.Empty(after.Rows);
        Assert.Equal("forbidden", (await ListAsync(client, "all")).Items.Single(r => r.Id == report.Summary.Id).Health);
    }

    [Fact]
    public async Task Sharing_validates_the_visibility_and_the_selected_users()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        var other = await factory.CreateTenantAsync();
        var stranger = await factory.CreateUserAsync(other);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("herkes", null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("users", []))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("users", [stranger.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync($"{Reports}?scope=hepsi")).StatusCode);
    }

    // ---- Dinamik alan (kabul senaryosu 11'in dinamik alan kısmı)

    [Fact]
    public async Task Custom_fields_can_be_summed_and_grouped_survive_renames_and_invalidate_the_report_when_removed()
    {
        var s = await SetUpAsync();
        var field = await ReadAsync<CustomFieldDto>(
            await s.Admin.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest("crm.opportunity", "Vade (gün)", CustomFieldType.Number)), HttpStatusCode.Created);
        var segment = await ReadAsync<CustomFieldDto>(
            await s.Admin.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(
                "crm.opportunity", "Segment", CustomFieldType.Choice, Options: [new(null, "KOBİ"), new(null, "Kurumsal")])), HttpStatusCode.Created);
        var kobi = segment.Options[0].Key;
        foreach (var (title, term) in new[] { ("A", 30), ("B", 45) })
        {
            await PostAsync<OpportunityDto>(s.Admin, "/api/crm/opportunities", new SaveOpportunityRequest(title, s.CompanyId, Amount: 1m,
                Custom: new() { [field.Key] = term, [segment.Key] = kobi }));
        }

        var spec = new ReportSpecDto
        {
            Dataset = Opportunities,
            Groups = [new() { Field = $"custom.{segment.Key}" }],
            Measures = [new() { Field = $"custom.{field.Key}", Aggregation = "sum" }],
        };
        var report = await SaveAsync(s.Admin, "Vade raporu", spec);

        var result = await RunAsync(s.Admin, spec);
        Assert.Equal(75m, Cell(result.Rows.Single(), 1));
        Assert.Equal("KOBİ", result.Rows.Single().Cells[0].GetString());   // seçenek etiketi

        // Yeniden adlandırma: kalıcı anahtar aynı kalır, rapor bozulmaz.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/api/customization/fields/{field.Id}", new SaveCustomFieldRequest("crm.opportunity", "Ödeme vadesi", CustomFieldType.Number))).StatusCode);
        Assert.Equal("ok", (await ListAsync(s.Admin, "mine")).Items.Single(r => r.Id == report.Summary.Id).Health);
        Assert.Equal(75m, Cell((await RunAsync(s.Admin, spec)).Rows.Single(), 1));
        var catalog = await ReadAsync<ReportCatalogDto>(await s.Admin.GetAsync($"{Reports}/catalog"));
        Assert.Contains(catalog.Datasets.Single(d => d.Key == Opportunities).Columns, c => c.Field == $"custom.{field.Key}" && c.Label == "Ödeme vadesi" && c.Custom);

        // Kaldırma (arşiv): rapor anlaşılır biçimde geçersiz işaretlenir, sessizce yanlış sonuç vermez.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/customization/fields/{field.Id}/archive", null)).StatusCode);
        var summary = (await ListAsync(s.Admin, "mine")).Items.Single(r => r.Id == report.Summary.Id);
        Assert.Equal("columnsInvalid", summary.Health);
        Assert.Contains("arşivlendi", summary.HealthMessage);
        var broken = await ReadAsync<ReportResultDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/run", new RunReportRequest(null, null)));
        Assert.Equal("invalid", broken.Status);
        Assert.Contains(broken.Issues, i => i.Code == "columnRemoved");
        Assert.Empty(broken.Rows);

        // Alan geri alınınca rapor yeniden çalışır.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/customization/fields/{field.Id}/restore", null)).StatusCode);
        Assert.Equal("ok", (await ListAsync(s.Admin, "mine")).Items.Single(r => r.Id == report.Summary.Id).Health);
    }

    // ---- Modül ve izin

    [Fact]
    public async Task A_disabled_module_makes_its_dataset_and_reports_unusable_with_a_clear_status()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "CRM raporu", OpenByStage());
        await factory.SetModuleAsync(s.TenantId, "crm", enabled: false);

        var run = await RunAsync(s.Admin, OpenByStage());
        var saved = await ReadAsync<ReportResultDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/run", new RunReportRequest(null, null)));

        Assert.Equal("moduleDisabled", run.Status);
        Assert.Equal("moduleDisabled", saved.Status);
        Assert.Equal("moduleDisabled", (await ListAsync(s.Admin, "mine")).Items.Single().Health);
        var catalog = await ReadAsync<ReportCatalogDto>(await s.Admin.GetAsync($"{Reports}/catalog"));
        Assert.DoesNotContain(catalog.Datasets, d => d.ModuleKey == "crm");
        Assert.DoesNotContain(catalog.Links, l => l.ModuleKey == "crm");
    }

    [Fact]
    public async Task Every_endpoint_needs_its_own_report_permission()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        var none = await UserAsync(s, DataRead, Scope.All);
        var reader = await UserAsync(s, [.. DataRead, .. CoreRead], Scope.All);
        var manager = await UserAsync(s, [.. DataRead, .. CoreRead, CoreModule.Permissions.ManageReports], Scope.All);
        var id = report.Summary.Id;

        foreach (var url in new[] { $"{Reports}/catalog", Reports, $"{Reports}/{id}" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await none.PostAsJsonAsync($"{Reports}/run", new RunReportRequest(OpenByStage(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.PostAsJsonAsync($"{Reports}/{id}/run", new RunReportRequest(null, null))).StatusCode);

        // Okuma izni oluşturma ve paylaşma izni vermez; yönetme izni paylaşma izni vermez.
        var count = new ReportSpecDto { Dataset = Opportunities, Measures = [new() { Aggregation = "count" }] };
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Reports, new SaveReportRequest("Yeni", null, count))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync(Reports, new SaveReportRequest("Yeni", null, count))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("tenant", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Reports}/{id}")).StatusCode);
    }

    // ---- Tenant izolasyonu (kabul senaryosu 6)

    [Fact]
    public async Task Another_tenant_sees_neither_the_data_nor_the_reports_and_cannot_touch_them()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        await OpenAsync(a.Admin, a.CompanyId, "A fırsatı", 1_000m);
        var report = await SaveAsync(a.Admin, "A raporu", OpenByStage());
        await ReadAsync<ReportDetailDto>(await a.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("tenant", null)));
        var id = report.Summary.Id;

        // Aynı tanım B'de B'nin verisini (boş) çalıştırır.
        var bResult = await RunAsync(b.Admin, OpenByStage());
        Assert.Empty(bResult.Rows);
        Assert.Equal(0, bResult.RowsRead);
        Assert.Single((await RunAsync(a.Admin, OpenByStage())).Rows);

        Assert.Empty((await ListAsync(b.Admin, "all")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"{Reports}/{id}/run", new RunReportRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync($"{Reports}/{id}", new SaveReportRequest("Ele geçir", null, OpenByStage()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync($"{Reports}/{id}/sharing", new ShareReportRequest("private", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync($"{Reports}/{id}/favorite", new FavoriteReportRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.DeleteAsync($"{Reports}/{id}")).StatusCode);
        // A'nın raporu etkilenmedi.
        Assert.Equal("A raporu", (await ReadAsync<ReportDetailDto>(await a.Admin.GetAsync($"{Reports}/{id}"))).Summary.Name);
    }

    [Fact]
    public async Task Creating_and_deleting_reports_is_written_to_the_audit_trail()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "Denetimli", OpenByStage());
        await ReadAsync<ReportDetailDto>(await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("tenant", null)));
        await s.Admin.DeleteAsync($"{Reports}/{report.Summary.Id}");

        using var scope = factory.OpenScope(s.TenantId);
        var audit = await scope.ServiceProvider.GetRequiredService<Modulix.Core.Audit.IAuditLog>().QueryAsync(
            new Modulix.Core.Audit.AuditQuery(EntityKey: CoreModule.AuditedEntityKeys.Report), CancellationToken.None);

        Assert.Equal(3, audit.TotalCount);
    }

    // ---- Yardımcılar

    private record Setup(HttpClient Admin, Tenant Tenant, Guid CompanyId, Guid AdminId)
    {
        public Guid TenantId => Tenant.Id;
    }

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

        var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return new Setup(client, tenant, company.Id, admin.Id);
    }

    private async Task<HttpClient> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, permissions, scope, user);
        return await LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    /// <summary>Açık fırsatlar, aşamaya göre: kayıt sayısı + tutar toplamı (para birimine göre ayrı).</summary>
    private static ReportSpecDto OpenByStage() => new()
    {
        Dataset = Opportunities,
        Filters = [new() { Field = "status", Operator = "equals", Value = "Open" }],
        Groups = [new() { Field = "stage" }],
        Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }],
        Sort = new() { Field = "count", Direction = "descending" },
        Chart = "bar",
    };

    private static async Task<ReportResultDto> RunAsync(HttpClient client, ReportSpecDto spec, ReportDrillDto? drill = null, int page = 1, int pageSize = 50) =>
        await ReadAsync<ReportResultDto>(await client.PostAsJsonAsync($"{Reports}/run", new RunReportRequest(spec, drill, page, pageSize)));

    private static async Task<ReportDetailDto> SaveAsync(HttpClient client, string name, ReportSpecDto spec) =>
        await ReadAsync<ReportDetailDto>(await client.PostAsJsonAsync(Reports, new SaveReportRequest(name, null, spec)), HttpStatusCode.Created);

    private static async Task<ReportPageDto> ListAsync(HttpClient client, string scope) =>
        await ReadAsync<ReportPageDto>(await client.GetAsync($"{Reports}?scope={scope}"));

    private static decimal Cell(ReportResultRowDto row, int index) => row.Cells[index].GetDecimal();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body) =>
        await ReadAsync<T>(await client.PostAsJsonAsync(url, body), HttpStatusCode.Created);

    private static async Task<OpportunityDto> OpenAsync(HttpClient client, Guid companyId, string title, decimal amount, string currency = "TRY") =>
        await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, Currency: currency, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20)));

    private static async Task StageAsync(HttpClient client, OpportunityDto opportunity, string stage)
    {
        var current = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/stage", new ChangeOpportunityStageRequest(current.Version, stage));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task WinAsync(HttpClient client, OpportunityDto opportunity)
    {
        var current = (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(current.Version));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
