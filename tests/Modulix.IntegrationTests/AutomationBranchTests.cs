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
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 4c-5: akışta dallanma uçtan uca. Adım koşulu adıma sıra geldiğinde kaydın güncel değerleriyle sorulur; sağlanmayan
/// adım atlanır, "Akışı bitir" akışı bilerek bitirir. Gerçek veritabanı, kayıt boru hattı ve RLS altında; saat elle ilerler.
/// </summary>
public class AutomationBranchTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private const string Amount = $"{CrmModule.Entities.Opportunity}.amount";
    private const string Stage = $"{CrmModule.Entities.Opportunity}.stage";

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, HttpClient Client);

    private static ConditionLeaf AmountAtLeast(decimal value) =>
        new(Amount, AutomationOperator.Gte, [value.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { Currency = "TRY" };

    private static ConditionLeaf AmountBelow(decimal value) =>
        new(Amount, AutomationOperator.Lt, [value.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { Currency = "TRY" };

    private static AutomationActionStep FollowUp(string id, string subject, ConditionNode? when = null) =>
        new(id, CrmAutomation.FollowUpAction, new JsonObject { ["subject"] = subject, ["dueInDays"] = 1 }) { When = when };

    private static AutomationActionStep Stop(string reason, ConditionNode? when) =>
        new("bitir", FlowAutomation.StopAction, new JsonObject { ["reason"] = reason }) { When = when };

    private static AutomationActionStep Wait(int days) =>
        new("bekle", FlowAutomation.WaitAction, new JsonObject { ["days"] = days });

    private static AutomationDefinition Flow(params AutomationActionStep[] steps) => new(new ManualTrigger(), null, steps);

    [Fact]
    public async Task Step_conditions_pick_the_branch_with_the_record_values_at_the_time_the_step_runs()
    {
        var setup = await SetUpAsync();
        var opportunity = await CreateOpportunityAsync(setup, "Dallanan akış", 1000m);
        var rule = await PublishAsync(setup.Client, Flow(
            Wait(1),
            FollowUp("buyuk", "Büyük fırsat: yöneticiyle görüşün", AmountAtLeast(50_000m)),
            FollowUp("kucuk", "Küçük fırsat: teklifi gönderin", AmountBelow(50_000m))));

        var run = await StartAsync(setup, rule, opportunity);
        await RoundAsync(setup.Tenant.Id);
        Assert.Equal("Running", (await DetailAsync(setup, run)).Run.Status);

        // Akış beklerken tutar büyür: dal, adıma sıra geldiğindeki değere göre seçilir (akışın başındaki değere göre değil).
        await UpdateAmountAsync(setup, opportunity, 80_000m);
        fixture.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await RoundAsync(setup.Tenant.Id);

        var done = await DetailAsync(setup, run);
        Assert.Equal("Succeeded", done.Run.Status);
        Assert.Equal(["Succeeded", "Succeeded", "Skipped"], done.Steps.Select(s => s.Status));
        Assert.NotNull(done.Steps[1].EffectRecordId);
        Assert.Null(done.Steps[2].EffectRecordId);
        Assert.Contains("Adım koşulu sağlanmadı", done.Steps[2].Summary);
    }

    [Fact]
    public async Task Stop_flow_with_a_condition_ends_only_the_matching_branch_and_invalid_step_conditions_are_refused_at_publish()
    {
        var setup = await SetUpAsync();
        var rule = await PublishAsync(setup.Client, Flow(
            Stop("Küçük tutar, takip gerekmiyor", AmountBelow(50_000m)),
            FollowUp("takip", "Büyük fırsatı takip edin")));

        var small = await StartAsync(setup, rule, await CreateOpportunityAsync(setup, "Küçük", 1000m));
        var big = await StartAsync(setup, rule, await CreateOpportunityAsync(setup, "Büyük", 90_000m));
        await RoundAsync(setup.Tenant.Id);

        var stopped = await DetailAsync(setup, small);
        Assert.Equal("Succeeded", stopped.Run.Status); // bilerek bitti, hata değil
        Assert.Contains("Küçük tutar", stopped.Run.Note);
        Assert.Equal(["Succeeded", "Skipped"], stopped.Steps.Select(s => s.Status));
        Assert.Null(stopped.Steps[1].EffectRecordId);

        var continued = await DetailAsync(setup, big);
        Assert.Equal("Succeeded", continued.Run.Status);
        Assert.Equal(["Skipped", "Succeeded"], continued.Steps.Select(s => s.Status));
        Assert.NotNull(continued.Steps[1].EffectRecordId);

        // Yayın: adım koşulu güncel kayda bakar; değişiklik koşulu ve bilinmeyen alan reddedilir.
        var changed = await TryPublishAsync(setup.Client, new AutomationDefinition(
            new FieldChangedTrigger([Stage]), null, [FollowUp("a", "x", new ConditionLeaf(Stage, AutomationOperator.Changed, []))]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Contains("güncel değerine bakar", await changed.Content.ReadAsStringAsync());
        var unknown = await TryPublishAsync(setup.Client, Flow(FollowUp("a", "x", new ConditionLeaf("crm.opportunity.yok", AutomationOperator.IsEmpty, []))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("Bilinmeyen alan", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_branching_run_is_not_visible_or_runnable_from_another_tenant()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        var opportunity = await CreateOpportunityAsync(first, "İzolasyon", 70_000m);
        var rule = await PublishAsync(first.Client, Flow(FollowUp("buyuk", "Takip", AmountAtLeast(50_000m))));
        var run = await StartAsync(first, rule, opportunity);
        await RoundAsync(first.Tenant.Id);
        await RoundAsync(second.Tenant.Id);

        Assert.Equal("Succeeded", (await DetailAsync(first, run)).Run.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await second.Client.GetAsync($"/api/automation/runs/{run}")).StatusCode);
        var foreign = await second.Client.PostAsJsonAsync($"/api/automation/rules/{rule}/run", new RunAutomationRuleRequest(opportunity));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task A_step_condition_on_a_field_the_publisher_cannot_see_is_refused()
    {
        var setup = await SetUpAsync();
        var withoutAmounts = new[]
        {
            CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CoreModule.Permissions.PublishAutomation,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite,
        };
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, withoutAmounts, member);
        using var client = await LoginAsync(member);

        // Tutarı göremeyen yayınlayan, tutara bakan adım koşuluyla dal kuramaz (yetki yükseltme yok).
        var hidden = await TryPublishAsync(client, Flow(FollowUp("a", "x", AmountAtLeast(1m))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hidden.StatusCode);
        Assert.Contains("görme izniniz yok", await hidden.Content.ReadAsStringAsync());

        // Görebildiği alanla (aşama) dal kurabilir.
        var visible = await TryPublishAsync(client, Flow(FollowUp("a", "x", new ConditionLeaf(Stage, AutomationOperator.Eq, ["Lead"]))));
        Assert.True(visible.IsSuccessStatusCode, await visible.Content.ReadAsStringAsync());
    }

    // --- yardımcılar ---

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        return new Setup(tenant, admin, await LoginAsync(admin));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
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

    private async Task UpdateAmountAsync(Setup setup, Guid opportunity, decimal amount)
    {
        using var client = await LoginAsync(setup.Admin);
        var current = (await client.GetFromJsonAsync<RecordResponse>($"/api/records/{CrmModule.Entities.Opportunity}/{opportunity}"))!;
        var response = await client.PutAsJsonAsync(
            $"/api/records/{CrmModule.Entities.Opportunity}/{opportunity}", new RecordWriteRequest(new JsonObject { ["amount"] = amount }, current.Version));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateRecordAsync(HttpClient client, string entityKey, JsonObject values)
    {
        var response = await client.PostAsJsonAsync($"/api/records/{entityKey}", new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> TryPublishAsync(HttpClient client, AutomationDefinition definition) =>
        (await PublishRuleAsync(client, definition)).Response;

    private static async Task<(HttpResponseMessage Response, Guid? RuleId)> PublishRuleAsync(HttpClient client, AutomationDefinition definition)
    {
        var create = await client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            CrmModule.ModuleKey, CrmModule.Entities.Opportunity, $"Dal {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
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
