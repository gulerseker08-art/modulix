using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Automation;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 4c-4: kalıcı çok adımlı iş akışı uçtan uca. "Bekle" ve "Onay sonucunu bekle" adımları çalışmayı veritabanında bekletir;
/// saat ilerleyince ya da onay sonuçlanınca akış kaldığı adımdan sürer, ret akışı bilerek durdurur. Gerçek veritabanı, kayıt boru hattı
/// ve RLS altında; saat elle ilerler.
/// </summary>
public class AutomationFlowTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Approver, HttpClient Client);

    private static AutomationActionStep SendForApproval(string process) =>
        new("onay", "core.approval.request", new JsonObject { ["process"] = process });

    private static AutomationActionStep AwaitApproval(int? timeoutDays = null) =>
        new("bekle-onay", FlowAutomation.AwaitApprovalAction, new JsonObject { ["timeoutDays"] = timeoutDays });

    private static AutomationActionStep Wait(int days, int hours = 0) =>
        new("bekle", FlowAutomation.WaitAction, new JsonObject { ["days"] = days, ["hours"] = hours });

    private static AutomationActionStep FollowUp() =>
        new("takip", CrmAutomation.FollowUpAction, new JsonObject { ["subject"] = "Onaylanan fırsatı kapatın", ["dueInDays"] = 1 });

    private static AutomationDefinition Flow(params AutomationActionStep[] steps) => new(new ManualTrigger(), null, steps);

    [Fact]
    public async Task A_wait_step_parks_the_run_and_it_resumes_from_the_same_step_when_the_time_comes()
    {
        var setup = await SetUpAsync();
        var opportunity = await CreateOpportunityAsync(setup, "Bekleyen akış", 1000m);
        var rule = await PublishAsync(setup.Client, Flow(Wait(2), FollowUp()));

        var run = await StartAsync(setup, rule, opportunity);
        await RoundAsync(setup.Tenant.Id);

        var waiting = await DetailAsync(setup, run);
        Assert.Equal("Running", waiting.Run.Status);
        Assert.Equal("Pending", waiting.Steps[0].Status);
        Assert.NotNull(waiting.Steps[0].WaitingUntil);
        Assert.Equal(0, waiting.Steps[0].Attempts); // bekleme deneme hakkından sayılmaz
        Assert.Equal("Pending", waiting.Steps[1].Status);

        // Bir gün sonra hâlâ bekler; süre (ilk denemeden itibaren 2 gün) dolunca kaldığı yerden sürer, ilk adım tekrar başlamaz.
        fixture.Time.Advance(TimeSpan.FromDays(1));
        await RoundAsync(setup.Tenant.Id);
        Assert.Equal("Running", (await DetailAsync(setup, run)).Run.Status);

        fixture.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await RoundAsync(setup.Tenant.Id);
        var done = await DetailAsync(setup, run);
        Assert.Equal("Succeeded", done.Run.Status);
        Assert.All(done.Steps, s => Assert.Equal("Succeeded", s.Status));
        Assert.All(done.Steps, s => Assert.Null(s.WaitingUntil));
        Assert.NotNull(done.Steps[1].EffectRecordId); // takip aktivitesi açıldı
    }

    [Fact]
    public async Task An_approval_flow_waits_for_the_decision_and_continues_only_when_approved()
    {
        var setup = await SetUpAsync();
        var process = await CreateProcessAsync(setup);
        var opportunity = await CreateOpportunityAsync(setup, "Onay akışı", 50_000m);
        var rule = await PublishAsync(setup.Client, Flow(SendForApproval(process.Name), AwaitApproval(), FollowUp()));

        var run = await StartAsync(setup, rule, opportunity);
        await RoundAsync(setup.Tenant.Id);

        var waiting = await DetailAsync(setup, run);
        Assert.Equal("Running", waiting.Run.Status);
        Assert.Equal(["Succeeded", "Pending", "Pending"], waiting.Steps.Select(s => s.Status));
        Assert.NotNull(waiting.Steps[1].WaitingUntil);

        // Onay gelmeden yoklama akışı ilerletmez.
        fixture.Time.Advance(FlowAutomation.ApprovalPollInterval + TimeSpan.FromSeconds(1));
        await RoundAsync(setup.Tenant.Id);
        Assert.Null((await DetailAsync(setup, run)).Steps[2].CompletedAt);

        await DecideAsync(setup, approve: true);
        fixture.Time.Advance(FlowAutomation.ApprovalPollInterval + TimeSpan.FromSeconds(1));
        await RoundAsync(setup.Tenant.Id);

        var done = await DetailAsync(setup, run);
        Assert.Equal("Succeeded", done.Run.Status);
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], done.Steps.Select(s => s.Status));
        Assert.NotNull(done.Steps[2].EffectRecordId);
    }

    [Fact]
    public async Task A_rejection_stops_the_flow_and_invalid_steps_are_refused_at_publish_and_closing_the_rule_cancels_a_waiting_run()
    {
        var setup = await SetUpAsync();
        var process = await CreateProcessAsync(setup);
        var opportunity = await CreateOpportunityAsync(setup, "Reddedilecek", 70_000m);
        var rule = await PublishAsync(setup.Client, Flow(SendForApproval(process.Name), AwaitApproval(), FollowUp()));

        var run = await StartAsync(setup, rule, opportunity);
        await RoundAsync(setup.Tenant.Id);
        await DecideAsync(setup, approve: false);
        fixture.Time.Advance(FlowAutomation.ApprovalPollInterval + TimeSpan.FromSeconds(1));
        await RoundAsync(setup.Tenant.Id);

        var stopped = await DetailAsync(setup, run);
        Assert.Equal("Succeeded", stopped.Run.Status); // bilerek durdu, hata değil
        Assert.Contains("reddedildi", stopped.Run.Note);
        Assert.Equal(["Succeeded", "Skipped", "Skipped"], stopped.Steps.Select(s => s.Status));
        Assert.Null(stopped.Steps[2].EffectRecordId); // takip açılmadı

        // Yayın: sıfır süreli bekleme ve onaya açık olmayan kayıt türünde onay beklemesi reddedilir.
        var zero = await TryPublishAsync(setup.Client, Flow(Wait(0), FollowUp()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        Assert.Contains("sıfırdan büyük", await zero.Content.ReadAsStringAsync());
        var lead = await TryPublishAsync(setup.Client, Flow(AwaitApproval()), CrmModule.Entities.Lead);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lead.StatusCode);
        Assert.Contains("onay sonucu beklenemez", await lead.Content.ReadAsStringAsync());

        // Bekleyen akışın kuralı kapatılırsa akış uyanınca iptal olur, kalan adım çalışmaz.
        var other = await CreateOpportunityAsync(setup, "Kural kapanacak", 1000m);
        var waitRule = await PublishAsync(setup.Client, Flow(Wait(1), FollowUp()));
        var parked = await StartAsync(setup, waitRule, other);
        await RoundAsync(setup.Tenant.Id);
        Assert.Equal(HttpStatusCode.OK, (await setup.Client.PutAsJsonAsync($"/api/automation/rules/{waitRule}/active", new SetAutomationRuleActiveRequest(false))).StatusCode);
        fixture.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await RoundAsync(setup.Tenant.Id);
        var cancelled = await DetailAsync(setup, parked);
        Assert.Equal("Cancelled", cancelled.Run.Status);
        Assert.Null(cancelled.Steps[1].EffectRecordId);
    }

    [Fact]
    public async Task One_tenants_approval_only_resumes_its_own_flow_and_runs_are_not_visible_across_tenants()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        var firstRun = await StartApprovalFlowAsync(first);
        var secondRun = await StartApprovalFlowAsync(second);

        await DecideAsync(first, approve: true);
        fixture.Time.Advance(FlowAutomation.ApprovalPollInterval + TimeSpan.FromSeconds(1));
        await RoundAsync(first.Tenant.Id);
        await RoundAsync(second.Tenant.Id);

        Assert.Equal("Succeeded", (await DetailAsync(first, firstRun)).Run.Status);
        Assert.Equal("Running", (await DetailAsync(second, secondRun)).Run.Status);

        // Bir firmanın akışı diğer firmadan görülemez.
        Assert.Equal(HttpStatusCode.NotFound, (await second.Client.GetAsync($"/api/automation/runs/{firstRun}")).StatusCode);
    }

    [Fact]
    public async Task Publishing_an_approval_wait_needs_the_request_approvals_permission()
    {
        var setup = await SetUpAsync();
        var withoutRequest = new[]
        {
            CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CoreModule.Permissions.PublishAutomation,
            CrmModule.Permissions.OpportunitiesRead,
        };
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, withoutRequest, member);
        using var client = await LoginAsync(member);

        Assert.NotEqual(HttpStatusCode.OK, (await TryPublishAsync(client, Flow(AwaitApproval()))).StatusCode);

        // "Bekle" izin istemez: aynı kullanıcı yalnızca bekleme adımlı bir akışı yayınlayabilir.
        Assert.Equal(HttpStatusCode.OK, (await TryPublishAsync(client, Flow(Wait(1)))).StatusCode);
    }

    // --- yardımcılar ---

    private async Task<Guid> StartApprovalFlowAsync(Setup setup)
    {
        var process = await CreateProcessAsync(setup);
        var opportunity = await CreateOpportunityAsync(setup, "İzolasyon", 10_000m);
        var rule = await PublishAsync(setup.Client, Flow(SendForApproval(process.Name), AwaitApproval(), FollowUp()));
        var run = await StartAsync(setup, rule, opportunity);
        await RoundAsync(setup.Tenant.Id);
        Assert.Equal("Running", (await DetailAsync(setup, run)).Run.Status);
        return run;
    }

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        var approver = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.DecideApprovals], approver);
        return new Setup(tenant, admin, approver, await LoginAsync(admin));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<ApprovalDefinitionDto> CreateProcessAsync(Setup setup)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Akış onayı {Guid.NewGuid():N}"[..24], null, "TRY", [new ApprovalStepDefinitionDto("Yönetici", 1, setup.Approver.Id, null, null, null)],
            null, CrmModule.Entities.Opportunity));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApprovalDefinitionDto>())!;
    }

    private async Task DecideAsync(Setup setup, bool approve)
    {
        using var approver = await LoginAsync(setup.Approver);
        var request = Assert.Single((await (await approver.GetAsync("/api/approvals/requests?view=Waiting&pageSize=50"))
            .Content.ReadFromJsonAsync<ApprovalRequestPageDto>())!.Items);
        var response = await approver.PostAsJsonAsync(
            $"/api/approvals/requests/{request.Id}/{(approve ? "approve" : "reject")}", new DecideApprovalRequest(approve ? null : "Bütçe yok", request.Version));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateOpportunityAsync(Setup setup, string title, decimal amount)
    {
        var company = await CreateRecordAsync(setup.Client, CrmModule.Entities.Company, new JsonObject { ["name"] = $"Firma {Guid.NewGuid():N}"[..20] });
        return await CreateRecordAsync(setup.Client, CrmModule.Entities.Opportunity, new JsonObject
        {
            ["title"] = title,
            ["companyId"] = company.ToString(),
            ["stage"] = "Lead",
            ["amount"] = amount,
            ["currency"] = "TRY",
        });
    }

    private static async Task<Guid> CreateRecordAsync(HttpClient client, string entityKey, JsonObject values)
    {
        var response = await client.PostAsJsonAsync($"/api/records/{entityKey}", new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> TryPublishAsync(
        HttpClient client, AutomationDefinition definition, string entityKey = CrmModule.Entities.Opportunity) =>
        (await PublishRuleAsync(client, definition, entityKey)).Response;

    private static async Task<(HttpResponseMessage Response, Guid? RuleId)> PublishRuleAsync(
        HttpClient client, AutomationDefinition definition, string entityKey = CrmModule.Entities.Opportunity)
    {
        var create = await client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            CrmModule.ModuleKey, entityKey, $"Akış {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        if (create.StatusCode != HttpStatusCode.Created)
        {
            return (create, null);
        }

        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        return (await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null)), created.Rule.Id);
    }

    private static async Task<Guid> PublishAsync(HttpClient client, AutomationDefinition definition)
    {
        var (response, ruleId) = await PublishRuleAsync(client, definition);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return ruleId!.Value;
    }

    private static async Task<Guid> StartAsync(Setup setup, Guid ruleId, Guid recordId)
    {
        var response = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{ruleId}/run", new RunAutomationRuleRequest(recordId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AutomationRunDetailDto>())!.Run.Id;
    }

    // Her okumada yeniden giriş: saat günlerce ilerleyince önceki oturum süresi dolar.
    private async Task<AutomationRunDetailDto> DetailAsync(Setup setup, Guid runId)
    {
        using var client = await LoginAsync(setup.Admin);
        var response = await client.GetAsync($"/api/automation/runs/{runId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AutomationRunDetailDto>())!;
    }

    private async Task RoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.NotNull(round);
    }
}
