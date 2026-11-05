using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Imports;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Imports;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0033 Dilim 1: içe aktarma günlüğünün sunucu tarafı. Mutlu yol (liste, ayrıntı, hata kodu özeti), doğrulama (geçersiz
/// süzgeç), tenant izolasyonu (A, B'nin koşusunu göremez) ve yetki (izinsiz erişemez; yalnızca <c>core.imports.run</c> sahibi
/// kendi koşularını görür).
/// </summary>
public class ImportLogTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string Hash = new('b', ImportRun.FileHashLength);

    [Fact]
    public async Task Reader_lists_runs_newest_first_and_sees_issue_summary_without_cell_values()
    {
        var tenant = await factory.CreateTenantAsync();
        var runner = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], reader);
        var older = await SeedRunAsync(tenant.Id, runner.Id, "eski.csv", failed: 0, created: 5, createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = await SeedRunAsync(
            tenant.Id, runner.Id, "musteriler.xlsx", failed: 3, created: 7,
            issues: [(2, "email", ImportIssueCodes.FormatInvalid), (3, "email", ImportIssueCodes.FormatInvalid), (9, "name", ImportIssueCodes.RequiredMissing)]);

        using var client = await LoginAsync(reader);
        var page = await client.GetFromJsonAsync<ImportRunPageDto>("/api/imports");

        Assert.Equal(2, page!.TotalCount);
        Assert.Equal([newer, older], page.Items.Select(r => r.Id));
        var first = page.Items[0];
        Assert.Equal("PartiallySucceeded", first.Status);
        Assert.Equal(("musteriler.xlsx", 10, 7, 3), (first.FileName, first.TotalRows, first.CreatedCount, first.FailedCount));
        Assert.Equal("Test Kullanıcı", first.UserName);

        var detail = await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{newer}");
        Assert.Equal(3, detail!.Issues.TotalCount);
        Assert.Equal(new ImportIssueCodeCountDto("format-invalid", 2), detail.IssueCodes[0]);
        Assert.Equal(new ImportIssueCodeCountDto("required-missing", 1), detail.IssueCodes[1]);
        Assert.Equal([2, 3, 9], detail.Issues.Items.Select(i => i.RowNo));

        var filtered = await client.GetFromJsonAsync<ImportRunDetailDto>($"/api/imports/{newer}?code=required-missing");
        Assert.Equal(9, Assert.Single(filtered!.Issues.Items).RowNo);

        // Süzgeçler.
        Assert.Equal(1, (await client.GetFromJsonAsync<ImportRunPageDto>("/api/imports?status=Succeeded"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<ImportRunPageDto>("/api/imports?mode=DryRun"))!.TotalCount);
        Assert.Equal(2, (await client.GetFromJsonAsync<ImportRunPageDto>($"/api/imports?userId={runner.Id}"))!.TotalCount);
        var tomorrow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).AddDays(1).ToString("yyyy-MM-dd");
        Assert.Equal(0, (await client.GetFromJsonAsync<ImportRunPageDto>($"/api/imports?from={tomorrow}"))!.TotalCount);
    }

    [Fact]
    public async Task Invalid_filters_are_rejected_and_unknown_runs_are_not_found()
    {
        var tenant = await factory.CreateTenantAsync();
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], reader);
        var run = await SeedRunAsync(tenant.Id, reader.Id, "a.csv", failed: 1, created: 0);
        using var client = await LoginAsync(reader);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/imports?status=Bilinmiyor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/imports?status=99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/imports?mode=Hepsi")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/imports/{run}?severity=Fatal")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/imports/{run}?code=kendi-kodum")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/imports/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task One_tenant_cannot_see_the_runs_of_another()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var readerA = await factory.CreateUserAsync(a);
        var runnerB = await factory.CreateUserAsync(b);
        await factory.CreateRoleAsync(a.Id, [ReadImports], readerA);
        var runOfA = await SeedRunAsync(a.Id, readerA.Id, "a.csv", failed: 0, created: 1);
        var runOfB = await SeedRunAsync(b.Id, runnerB.Id, "b.csv", failed: 1, created: 0, issues: [(1, "name", ImportIssueCodes.RequiredMissing)]);

        using var client = await LoginAsync(readerA);
        var page = await client.GetFromJsonAsync<ImportRunPageDto>("/api/imports");

        Assert.Equal([runOfA], page!.Items.Select(r => r.Id));
        Assert.Equal(0, (await client.GetFromJsonAsync<ImportRunPageDto>($"/api/imports?userId={runnerB.Id}"))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/imports/{runOfB}")).StatusCode);
    }

    [Fact]
    public async Task Without_an_import_permission_the_log_is_forbidden()
    {
        var tenant = await factory.CreateTenantAsync();
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], outsider);
        var run = await SeedRunAsync(tenant.Id, outsider.Id, "a.csv", failed: 0, created: 1);
        using var client = await LoginAsync(outsider);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/imports/{run}")).StatusCode);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/imports")).StatusCode);
    }

    [Fact]
    public async Task A_runner_without_the_read_permission_sees_only_their_own_runs()
    {
        var tenant = await factory.CreateTenantAsync();
        var runner = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [RunImports], runner);
        var own = await SeedRunAsync(tenant.Id, runner.Id, "benim.csv", failed: 0, created: 2);
        var others = await SeedRunAsync(tenant.Id, colleague.Id, "baskasi.csv", failed: 1, created: 0, issues: [(1, "name", ImportIssueCodes.RequiredMissing)]);

        using var client = await LoginAsync(runner);
        var page = await client.GetFromJsonAsync<ImportRunPageDto>("/api/imports");

        Assert.Equal([own], page!.Items.Select(r => r.Id));
        Assert.Equal(0, (await client.GetFromJsonAsync<ImportRunPageDto>($"/api/imports?userId={colleague.Id}"))!.TotalCount);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/imports/{own}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/imports/{others}")).StatusCode);

        // Firmanın tamamını görme izni olan aynı iki koşuyu da görür.
        var manager = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], manager);
        using var managerClient = await LoginAsync(manager);
        Assert.Equal(2, (await managerClient.GetFromJsonAsync<ImportRunPageDto>("/api/imports"))!.TotalCount);
    }

    /// <summary>Koşuyu tenant bağlamıyla doğrudan yazar (içe aktarma motoru Dilim 2'dedir); sayılar varlık kurallarından geçer.</summary>
    private async Task<Guid> SeedRunAsync(
        Guid tenantId, Guid userId, string fileName, int failed, int created, DateTimeOffset? createdAt = null,
        (int Row, string Column, string Code)[]? issues = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var run = ImportRun.Create(
            tenantId, userId, "crm.lead", fileName, 4096, Hash, 1,
            ImportRunMode.Apply, ImportFailurePolicy.SkipInvalidRows, Guid.NewGuid().ToString("N"), now);
        run.Start(created + failed, now);
        run.Complete(created, 0, 0, failed, note: null, now.AddSeconds(5));

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.ImportRuns.Add(run);
        foreach (var (row, column, code) in issues ?? [])
        {
            db.ImportRowIssues.Add(ImportRowIssue.Create(tenantId, run.Id, row, column, code, ImportIssueSeverity.Error, "Örnek hata.", now));
        }

        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
