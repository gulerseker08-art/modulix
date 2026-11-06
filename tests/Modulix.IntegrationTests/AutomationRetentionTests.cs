using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 2b-2: bitmiş kural çalışmalarının saklama temizliği. Çalışmalar gerçek turla üretilir, yaşları veritabanında geriye çekilir;
/// silme gerçek veritabanında, Row-Level Security altında çalışır. Temizliğin API yüzü yoktur (yalnızca arka plan işi); bu yüzden yetki testi
/// yerine "yalnızca bitmiş ve süresi dolmuş satır silinir" ve tenant izolasyonu sınanır.
/// </summary>
[Collection(AutomationTestCollection.Name)]
public class AutomationRetentionTests(AutomationFixture fixture) : IClassFixture<AutomationFixture>, IDisposable
{
    private const string Item = AutomationTestModule.ItemEntity;

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, HttpClient Client);

    private static AutomationDefinition Scheduled(ConditionNode? condition = null) =>
        new(new ScheduledTrigger("00:00"), condition, [new AutomationActionStep("note", AutomationTestModule.NoteAction, new JsonObject { ["text"] = "Not" })]);

    public void Dispose()
    {
        var options = fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value;
        options.Enabled = true;
        options.RunRetentionDays = 90;
        options.FailedRunRetentionDays = 180;
    }

    [Fact]
    public async Task Finished_runs_and_their_steps_are_deleted_after_the_retention_period_and_fresh_ones_stay()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled(new ConditionLeaf($"{Item}.status", AutomationOperator.Eq, ["open"])));
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Eski");
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "closed", title: "Eski koşulsuz");
        await RunRoundAsync(setup.Tenant.Id);
        await AgeRunsAsync(setup.Tenant.Id, TimeSpan.FromDays(91));

        fixture.Time.Advance(TimeSpan.FromDays(1));
        var fresh = AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Yeni");
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(5, await CountRunsAsync(setup.Tenant.Id)); // ikinci günde üç kaydın hepsi yeniden taranır (2 + 3)

        var result = await CleanAsync(setup.Tenant.Id);

        Assert.NotNull(result);
        Assert.Equal(2, result.Runs);
        Assert.Equal(1, result.Steps); // yalnızca koşulu sağlayan eski çalışmanın adımı vardı
        var left = await RunsAsync(setup.Tenant.Id);
        Assert.Equal(3, left.Count);
        Assert.All(left, r => Assert.True(r.CompletedAt > fixture.Time.GetUtcNow().AddDays(-90)));
        Assert.Contains(left, r => r.RecordId == fresh);
        Assert.Equal(2, await CountStepsAsync(setup.Tenant.Id)); // ikinci günün iki adımı duruyor

        // Tekrar çalıştırmak güvenlidir ve bir şey silmez.
        Assert.Equal(0, (await CleanAsync(setup.Tenant.Id))!.Runs);
        Assert.Equal(3, await CountRunsAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Failed_runs_are_kept_longer_and_unfinished_runs_are_never_deleted()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "Başarısız");
        AutomationProbe.Behavior = _ => new AutomationActionResult(AutomationActionStatus.Failed, "Kalıcı hata");
        await RunRoundAsync(setup.Tenant.Id);
        var failed = Assert.Single(await RunsAsync(setup.Tenant.Id));
        Assert.Equal(AutomationRunStatus.Failed, failed.Status);

        // Başarılı çalışma sınırı (90) geçti ama başarısız çalışma sınırı (180) geçmedi: durur.
        await AgeRunsAsync(setup.Tenant.Id, TimeSpan.FromDays(100));
        Assert.Equal(0, (await CleanAsync(setup.Tenant.Id))!.Runs);

        // Bitmemiş çalışma, ne kadar eski olursa olsun silinmez.
        fixture.Time.Advance(TimeSpan.FromDays(1));
        AutomationProbe.Behavior = _ => new AutomationActionResult(AutomationActionStatus.Failed, "Geçici") { Retryable = true };
        await RunRoundAsync(setup.Tenant.Id);
        var waiting = Assert.Single(await RunsAsync(setup.Tenant.Id), r => r.Status == AutomationRunStatus.Running);
        await AgeCreatedAsync(setup.Tenant.Id, TimeSpan.FromDays(400));
        Assert.Equal(0, (await CleanAsync(setup.Tenant.Id))!.Runs);
        Assert.Contains(await RunsAsync(setup.Tenant.Id), r => r.Id == waiting.Id);

        // Başarısız çalışma da 180 günü aşınca silinir.
        await AgeRunsAsync(setup.Tenant.Id, TimeSpan.FromDays(181), failedOnly: true);
        Assert.Equal(1, (await CleanAsync(setup.Tenant.Id))!.Runs);
        Assert.DoesNotContain(await RunsAsync(setup.Tenant.Id), r => r.Id == failed.Id);
        Assert.Contains(await RunsAsync(setup.Tenant.Id), r => r.Id == waiting.Id);
    }

    [Fact]
    public async Task Old_scan_cursors_are_deleted_and_the_current_one_stays()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A");
        await RunRoundAsync(setup.Tenant.Id);
        await AgeCursorsAsync(setup.Tenant.Id, TimeSpan.FromDays(95));

        fixture.Time.Advance(TimeSpan.FromDays(1));
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(2, await CountCursorsAsync(setup.Tenant.Id));

        var result = await CleanAsync(setup.Tenant.Id);

        Assert.Equal(1, result!.Cursors);
        Assert.Equal(1, await CountCursorsAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Cleaning_one_tenant_never_touches_another_tenants_rows()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        foreach (var setup in new[] { first, second })
        {
            await PublishRuleAsync(setup, Scheduled());
            AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A");
            await RunRoundAsync(setup.Tenant.Id);
            await AgeRunsAsync(setup.Tenant.Id, TimeSpan.FromDays(120));
            await AgeCursorsAsync(setup.Tenant.Id, TimeSpan.FromDays(120));
        }

        var result = await CleanAsync(first.Tenant.Id);

        Assert.Equal(1, result!.Runs);
        Assert.Equal(0, await CountRunsAsync(first.Tenant.Id));
        Assert.Equal(1, await CountRunsAsync(second.Tenant.Id));
        Assert.Equal(1, await CountStepsAsync(second.Tenant.Id));
        Assert.Equal(1, await CountCursorsAsync(second.Tenant.Id));
    }

    [Fact]
    public async Task Cleanup_runs_even_when_the_platform_kill_switch_is_off_and_skips_unknown_tenants()
    {
        var setup = await SetUpAsync();
        await PublishRuleAsync(setup, Scheduled());
        AutomationRecords.Add(setup.Tenant.Id, Item, status: "open", title: "A");
        await RunRoundAsync(setup.Tenant.Id);
        await AgeRunsAsync(setup.Tenant.Id, TimeSpan.FromDays(120));

        fixture.Extended.Services.GetRequiredService<IOptions<AutomationOptions>>().Value.Enabled = false;

        Assert.Equal(1, (await CleanAsync(setup.Tenant.Id))!.Runs);

        // Var olmayan (etkin olmayan) tenant için temizlik çalışmaz.
        Assert.Null(await CleanAsync(Guid.NewGuid()));
    }

    // --- yardımcılar ---

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        await fixture.SetTestModuleAsync(tenant.Id);
        AutomationProbe.Reset();
        return new Setup(tenant, await LoginAsync(admin));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task PublishRuleAsync(Setup setup, AutomationDefinition definition)
    {
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            AutomationTestModule.ModuleKey, Item, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
    }

    private async Task RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.NotNull(round);
    }

    private Task<AutomationRetentionResult?> CleanAsync(Guid tenantId) =>
        fixture.Extended.Services.GetRequiredService<AutomationRunner>().CleanTenantAsync(tenantId, CancellationToken.None);

    /// <summary>Bitmiş çalışmaların tamamlanma zamanını <paramref name="age"/> kadar geriye çeker.</summary>
    private async Task AgeRunsAsync(Guid tenantId, TimeSpan age, bool failedOnly = false)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var at = fixture.Time.GetUtcNow() - age;
        await db.AutomationRuns
            .Where(r => r.CompletedAt != null && (!failedOnly || r.Status == AutomationRunStatus.Failed))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CompletedAt, at));
    }

    private async Task AgeCreatedAsync(Guid tenantId, TimeSpan age)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var at = fixture.Time.GetUtcNow() - age;
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.CreatedAt, at));
    }

    private async Task AgeCursorsAsync(Guid tenantId, TimeSpan age)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var at = fixture.Time.GetUtcNow() - age;
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationScanCursors
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.UpdatedAt, at));
    }

    private async Task<IReadOnlyList<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<int> CountRunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.CountAsync();
    }

    private async Task<int> CountStepsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRunSteps.CountAsync();
    }

    private async Task<int> CountCursorsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationScanCursors.CountAsync();
    }
}
