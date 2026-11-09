using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Reporting;
using Xunit.Abstractions;

namespace Modulix.IntegrationTests;

/// <summary>Yalnızca <c>MODULIX_PERF=1</c> ile çalışan ölçüm testi (normal CI koşusunda atlanır; saatlerce süren yük testi değildir ama saniyeler ister).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MODULIX_PERF") != "1")
        {
            Skip = "Performans ölçümü: MODULIX_PERF=1 ile çalıştırın (ADR-0026 bölüm 7).";
        }
    }
}

/// <summary>
/// ADR-0026 bölüm 7: satış genel görünümü, tahmin oluşturma, rapor çalıştırma ve dışa aktarma için temsilî büyük veri setinde süre ölçümü. Test hedef
/// <b>doğrulamaz</b> (ortama bağlıdır); ölçer, <c>MODULIX_PERF_OUT</c> verilmişse sonucu Markdown tablosu olarak dosyaya yazar. Hedefler, yük profili ve
/// bulgular ADR'de tutulur. Veri, SQL ile toplu eklenir (API ile 10 bin kayıt eklemek ölçülen şey değildir); okuma yolu gerçektir (HTTP, kayıt boru hattı).
/// </summary>
public sealed class ReportPerformanceMeasurementTests(ModulixApiFactory factory, ITestOutputHelper output) : IClassFixture<ModulixApiFactory>
{
    private static int Count => int.TryParse(Environment.GetEnvironmentVariable("MODULIX_PERF_ROWS"), out var rows) && rows > 0 ? rows : 5000;

    [PerfFact]
    public async Task Measure_overview_forecast_report_and_export_on_a_large_dataset()
    {
        var open = Count;
        var closed = Count;
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(admin.Email!, ModulixApiFactory.Password))).StatusCode);
        var company = (await (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Ölçüm A.Ş."))).Content.ReadFromJsonAsync<CompanyDto>())!;
        await client.PostAsJsonAsync("/api/crm/exchange-rates", new SaveExchangeRateRequest("USD", "TRY", 30m, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-400)));

        var seed = Stopwatch.StartNew();
        await SeedAsync(tenant.Id, company.Id, admin.Id, open, closed);
        output.WriteLine($"Tohum: {open} açık + {closed} kapanmış fırsat, {seed.Elapsed.TotalSeconds:F1} sn");

        var summary = new ReportSpecDto
        {
            Dataset = "crm.opportunities",
            Filters = [new() { Field = "status", Operator = "equals", Value = "Open" }],
            Groups = [new() { Field = "stage" }],
            Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }],
        };
        var unfiltered = new ReportSpecDto { Dataset = "crm.opportunities", Groups = [new() { Field = "stage" }], Measures = [new() { Aggregation = "count" }] };
        var table = new ReportSpecDto
        {
            Dataset = "crm.opportunities",
            Columns = ["title", "stage", "amount", "expectedCloseDate"],
            Filters = [new() { Field = "status", Operator = "equals", Value = "Open" }],
            Sort = new() { Field = "amount", Direction = "descending" },
        };
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var results = new List<Measurement>
        {
            await MeasureAsync("Satış genel görünümü (ay)", () => client.GetAsync($"/api/crm/sales/overview?kind=Month&date={today:yyyy-MM-dd}")),
            await MeasureAsync("Rapor: açık fırsatlar, aşamaya göre özet", () => client.PostAsJsonAsync("/api/reports/run", new RunReportRequest(summary, null))),
            await MeasureAsync("Rapor: süzgeçsiz özet (okuma sınırına takılır)", () => client.PostAsJsonAsync("/api/reports/run", new RunReportRequest(unfiltered, null))),
            await MeasureAsync("Rapor: açık fırsat tablosu, 1. sayfa (50)", () => client.PostAsJsonAsync("/api/reports/run", new RunReportRequest(table, null, 1, 50))),
            await MeasureAsync("Dışa aktarma: özet CSV", () => client.PostAsJsonAsync("/api/reports/exports", new ExportReportRequest(summary, "Ölçüm")), cleanup: () => PurgeExportsAsync(tenant.Id)),
            await MeasureAsync("Dışa aktarma: tablo CSV (açık fırsatlar)", () => client.PostAsJsonAsync("/api/reports/exports", new ExportReportRequest(table, "Ölçüm")), cleanup: () => PurgeExportsAsync(tenant.Id)),
            await MeasureAsync(
                "Tahmin oluşturma (ay, tüm kapsam)",
                () => client.PostAsJsonAsync("/api/crm/sales/forecasts", new CreateForecastRequestDto("Month", today, "Tenant", null, "TRY")),
                cleanup: () => DeleteForecastsAsync(tenant.Id)),
        };

        var table1 = new StringBuilder()
            .AppendLine($"Veri: {open} açık + {closed} kapanmış fırsat (tek tenant). Her işlem 1 ısınma + {Runs} ölçüm; süre = istemciden yanıta (HTTP, kimlik doğrulamalı).")
            .AppendLine()
            .AppendLine("| İşlem | p50 (ms) | p95 (ms) | en yüksek (ms) | yanıt |")
            .AppendLine("|---|---:|---:|---:|---|");
        foreach (var r in results)
        {
            table1.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Name} | {r.P50:F0} | {r.P95:F0} | {r.Max:F0} | {r.Status} |"));
        }

        output.WriteLine(table1.ToString());
        if (Environment.GetEnvironmentVariable("MODULIX_PERF_OUT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, table1.ToString());
        }

        Assert.All(results, r => Assert.Equal("200/201", r.Status));
    }

    [PerfFact]
    public async Task Profile_where_the_read_time_goes()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        Guid membershipId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            membershipId = await factory.GetMembershipIdAsync(admin.Id, tenant.Id);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [system.Id], CancellationToken.None));
        }

        var client = factory.CreateBrowserClient();
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(admin.Email!, ModulixApiFactory.Password));
        var company = (await (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Profil A.Ş."))).Content.ReadFromJsonAsync<CompanyDto>())!;
        await SeedAsync(tenant.Id, company.Id, admin.Id, Count, Count);

        await using var owner = factory.Services.GetRequiredService<Modulix.Infrastructure.Platform.ITenantScopeFactory>().Create(new Modulix.Core.Contracts.Tenancy.TenantContext(tenant.Id, admin.Id, membershipId));
        var services = owner.ServiceProvider;
        var pipeline = services.GetRequiredService<Modulix.Core.Contracts.Records.IRecordPipeline>();
        var db = services.GetRequiredService<ModulixDbContext>();
        var filters = new Dictionary<string, string> { ["status"] = "Open" };

        async Task<double> TimeAsync(Func<Task<int>> work, string label)
        {
            await work();
            var times = new List<double>();
            var rows = 0;
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                rows = await work();
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: p50 {times[2]:F0} ms, en yüksek {times[^1]:F0} ms, {rows} satır"));
            return times[2];
        }

        async Task<int> PipelinePagesAsync(int pageSize)
        {
            var total = 0;
            for (var page = 1; ; page++)
            {
                var result = await pipeline.QueryAsync(new Modulix.Core.Contracts.Records.RecordQuery("crm.opportunity", page, pageSize, null, filters) { Bulk = pageSize > 200 }, CancellationToken.None);
                total += result.Value!.Items.Count;
                if (result.Value.Items.Count == 0 || total >= result.Value.TotalCount)
                {
                    return total;
                }
            }
        }

        async Task<int> OnePageAsync(int size)
        {
            var result = await pipeline.QueryAsync(new Modulix.Core.Contracts.Records.RecordQuery("crm.opportunity", 1, size, null, filters), CancellationToken.None);
            return result.Value!.Items.Count;
        }

        await OnePageAsync(1);
        Console.WriteLine("##BASLA##");
        await OnePageAsync(1);
        Console.WriteLine("##BITTI##");
        await TimeAsync(() => OnePageAsync(1), "Boru hattı: tek sayfa, 1 satır");
        await TimeAsync(() => OnePageAsync(50), "Boru hattı: tek sayfa, 50 satır");
        await TimeAsync(() => OnePageAsync(200), "Boru hattı: tek sayfa, 200 satır");
        await TimeAsync(() => PipelinePagesAsync(200), "Kayıt boru hattı, 200'lük sayfalar (bugünkü yol)");
        await TimeAsync(() => PipelinePagesAsync(1000), "Kayıt boru hattı, 1000'lik toplu sayfalar");
        await TimeAsync(async () => (await db.Set<Opportunity>().AsNoTracking().Where(o => o.Status == "Open").ToListAsync()).Count, "Doğrudan EF, tüm sütunlar, tek sorgu");
        await TimeAsync(async () => (await db.Set<Opportunity>().AsNoTracking().Where(o => o.Status == "Open").Select(o => new { o.Id, o.Stage, o.Amount, o.Currency, o.Probability, o.ExpectedCloseDate }).ToListAsync()).Count, "Doğrudan EF, 6 sütun, tek sorgu");
        await TimeAsync(async () => await db.Set<Opportunity>().AsNoTracking().CountAsync(o => o.Status == "Open"), "COUNT(*) tek sorgu");
    }

    private const int Runs = 10;

    private sealed record Measurement(string Name, double P50, double P95, double Max, string Status);

    private static async Task<Measurement> MeasureAsync(string name, Func<Task<HttpResponseMessage>> call, Func<Task>? cleanup = null)
    {
        using (var warm = await call())
        {
            Assert.True(warm.IsSuccessStatusCode, $"{name}: {(int)warm.StatusCode} {await warm.Content.ReadAsStringAsync()}");
        }

        if (cleanup is not null)
        {
            await cleanup();
        }

        var times = new List<double>();
        var status = "200/201";
        for (var i = 0; i < Runs; i++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await call();
            _ = await response.Content.ReadAsByteArrayAsync();
            clock.Stop();
            times.Add(clock.Elapsed.TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                status = $"{(int)response.StatusCode}";
            }

            if (cleanup is not null)
            {
                await cleanup();
            }
        }

        times.Sort();
        return new Measurement(name, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1], status);
    }

    private async Task PurgeExportsAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().ReportExports.ExecuteDeleteAsync();
    }

    private async Task DeleteForecastsAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM crm.ForecastAdjustments; DELETE FROM crm.ForecastItems; DELETE FROM crm.ForecastSubmissions;");
    }

    /// <summary>
    /// Fırsatları tek SQL ifadesiyle toplu ekler. Sütun listesi EF modelinden gelir; belirtilmeyen zorunlu sütunlar türüne göre varsayılan alır. Satır kimlikleri
    /// yeni, tenant ve şirket gerçektir; kaynak lead, teklif ve tarihçe yoktur (ölçülen okuma yolu fırsat tablosudur).
    /// </summary>
    private async Task SeedAsync(Guid tenantId, Guid companyId, Guid ownerId, int open, int closed)
    {
        var total = open + closed;
        var overrides = new Dictionary<string, string>
        {
            ["Id"] = "NEWID()",
            ["TenantId"] = "@tenant",
            ["Title"] = "N'Ölçüm fırsatı ' + CAST(n.i AS nvarchar(12))",
            ["OpportunityNumber"] = "N'PERF-' + CAST(n.i AS nvarchar(12))",
            ["CompanyId"] = "@company",
            ["OwnerId"] = "@owner",
            ["Stage"] = $"CASE WHEN n.i > {open} THEN CASE WHEN n.i % 5 < 3 THEN N'Won' ELSE N'Lost' END ELSE CASE n.i % 4 WHEN 0 THEN N'Lead' WHEN 1 THEN N'Qualified' WHEN 2 THEN N'Proposal' ELSE N'Negotiation' END END",
            ["Status"] = $"CASE WHEN n.i > {open} THEN CASE WHEN n.i % 5 < 3 THEN N'Won' ELSE N'Lost' END ELSE N'Open' END",
            ["Amount"] = "CAST((n.i % 997 + 1) * 1000 AS decimal(18,2))",
            ["Currency"] = "CASE WHEN n.i % 10 = 0 THEN N'USD' ELSE N'TRY' END",
            ["Probability"] = "n.i % 100",
            ["ExpectedCloseDate"] = "DATEADD(day, (n.i % 360) - 180, CAST(SYSDATETIME() AS date))",
            ["ActualCloseDate"] = $"CASE WHEN n.i > {open} THEN DATEADD(day, -(n.i % 300), CAST(SYSDATETIME() AS date)) END",
            ["WonAmount"] = $"CASE WHEN n.i > {open} AND n.i % 5 < 3 THEN CAST((n.i % 997 + 1) * 1000 AS decimal(18,2)) END",
            ["WonCurrency"] = $"CASE WHEN n.i > {open} AND n.i % 5 < 3 THEN CASE WHEN n.i % 10 = 0 THEN N'USD' ELSE N'TRY' END END",
            ["Priority"] = "N'Normal'",
            ["Version"] = "1",
        };

        await InsertAsync(
            tenantId, typeof(Opportunity), total, overrides, new Microsoft.Data.SqlClient.SqlParameter("@tenant", tenantId),
            new Microsoft.Data.SqlClient.SqlParameter("@company", companyId), new Microsoft.Data.SqlClient.SqlParameter("@owner", ownerId));
    }

    /// <summary>Verilen varlık türünün satırlarını EF modelinden üretilen tek SQL ifadesiyle ekler (belirtilmeyen zorunlu sütunlar türüne göre varsayılan alır).</summary>
    private async Task InsertAsync(Guid tenantId, Type type, int total, Dictionary<string, string> overrides, params Microsoft.Data.SqlClient.SqlParameter[] parameters)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var entity = db.Model.FindEntityType(type)!;
        var table = $"[{entity.GetSchema()}].[{entity.GetTableName()}]";
        var names = new List<string>();
        var values = new List<string>();
        foreach (var property in entity.GetProperties())
        {
            var column = property.GetColumnName();
            names.Add($"[{column}]");
            if (overrides.TryGetValue(property.Name, out var expression))
            {
                values.Add(expression);
            }
            else if (property.IsNullable)
            {
                values.Add("NULL");
            }
            else
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                values.Add(clr switch
                {
                    _ when clr == typeof(Guid) => "NEWID()",
                    _ when clr == typeof(string) => "N''",
                    _ when clr == typeof(bool) => "0",
                    _ when clr == typeof(DateTimeOffset) => "SYSDATETIMEOFFSET()",
                    _ when clr == typeof(DateOnly) => "CAST(SYSDATETIME() AS date)",
                    _ => "0",
                });
            }
        }

        var sql = $"""
            WITH n(i) AS (SELECT TOP ({total}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO {table} ({string.Join(", ", names)})
            SELECT {string.Join(", ", values)} FROM n
            """;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));   // 50 bin+ satırlık tohumlama yoğun makinede varsayılan 30 sn'yi aşar
        var inserted = await db.Database.ExecuteSqlRawAsync(sql, parameters);
        Assert.Equal(total, inserted);
    }

    [PerfFact]
    public async Task Profile_lead_dataset_read_cost()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        Guid membershipId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            membershipId = await factory.GetMembershipIdAsync(admin.Id, tenant.Id);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [system.Id], CancellationToken.None));
        }

        var rows = Count * 2;
        var overrides = new Dictionary<string, string>
        {
            ["Id"] = "NEWID()",
            ["TenantId"] = "@tenant",
            ["LeadNumber"] = "N'PERF-L-' + CAST(n.i AS nvarchar(12))",
            ["Subject"] = "N'Ölçüm lead ' + CAST(n.i AS nvarchar(12))",
            ["Status"] = "CASE n.i % 5 WHEN 0 THEN N'New' WHEN 1 THEN N'Contacting' WHEN 2 THEN N'Qualified' WHEN 3 THEN N'Converted' ELSE N'Disqualified' END",
            ["Priority"] = "N'Normal'",
            ["Source"] = "CASE n.i % 3 WHEN 0 THEN N'Referral' WHEN 1 THEN N'Event' ELSE N'WebForm' END",
            ["OwnerId"] = "@owner",
            ["FirstReceivedAt"] = "DATEADD(minute, -n.i, SYSDATETIMEOFFSET())",
            ["Version"] = "1",
        };
        await InsertAsync(tenant.Id, typeof(Lead), rows, overrides, new Microsoft.Data.SqlClient.SqlParameter("@tenant", tenant.Id), new Microsoft.Data.SqlClient.SqlParameter("@owner", admin.Id));

        await using var owner = factory.Services.GetRequiredService<Modulix.Infrastructure.Platform.ITenantScopeFactory>().Create(new Modulix.Core.Contracts.Tenancy.TenantContext(tenant.Id, admin.Id, membershipId));
        var pipeline = owner.ServiceProvider.GetRequiredService<Modulix.Core.Contracts.Records.IRecordPipeline>();

        async Task<int> PagesAsync(Dictionary<string, string>? filters)
        {
            var total = 0;
            for (var page = 1; ; page++)
            {
                var result = await pipeline.QueryAsync(new Modulix.Core.Contracts.Records.RecordQuery("crm.lead", page, 1000, null, filters) { Bulk = true }, CancellationToken.None);
                total += result.Value!.Items.Count;
                if (result.Value.Items.Count == 0 || total >= result.Value.TotalCount || total >= 5000)
                {
                    return total;
                }
            }
        }

        async Task TimeAsync(string label, Dictionary<string, string>? filters)
        {
            await PagesAsync(filters);
            var times = new List<double>();
            var read = 0;
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                read = await PagesAsync(filters);
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: p50 {times[2]:F0} ms, en yüksek {times[^1]:F0} ms, {read} satır"));
        }

        await TimeAsync($"Lead, süzgeçsiz ({rows} kayıttan ilk 5000)", null);
        await TimeAsync("Lead, status=New (2 bin kayıt)", new Dictionary<string, string> { ["status"] = "New" });
        await TimeAsync("Lead, source=Referral (3,3 bin kayıt)", new Dictionary<string, string> { ["source"] = "Referral" });
        await TimeAsync("Lead, status=Qualified + source=Event", new Dictionary<string, string> { ["status"] = "Qualified", ["source"] = "Event" });
    }
}
