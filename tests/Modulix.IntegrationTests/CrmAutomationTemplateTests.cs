using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Automation;
using Modulix.Modules.Crm.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 3b: CRM başlangıç şablonları uçtan uca. Şablon kullanmak yalnızca taslak kural açar; taslak çalışmaz,
/// firma yayınlayana kadar hiçbir aktivite açılmaz. Gerçek veritabanı, kayıt boru hattı ve Row-Level Security altında.
/// </summary>
public class CrmAutomationTemplateTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, HttpClient Client);

    [Fact]
    public async Task Using_a_template_opens_an_editable_draft_that_never_runs_until_the_company_publishes_it()
    {
        var setup = await SetUpAsync();
        var lead = await CreateLeadAsync(setup, "Sessiz aday");

        var templates = (await setup.Client.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!;
        Assert.Equal(CrmAutomationTemplates.All.Select(t => t.Key), templates.Select(t => t.Key));
        Assert.All(templates, t => Assert.True(t.Available, string.Join(";", t.Issues.Select(i => i.Message))));

        var use = await setup.Client.PostAsync($"/api/automation/templates/{CrmAutomationTemplates.LeadInactivity}/use", null);
        Assert.Equal(HttpStatusCode.Created, use.StatusCode);
        var detail = (await use.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        Assert.Equal("Draft", detail.Rule.Status);
        Assert.True(detail.Rule.HasDraft);
        Assert.Null(detail.Published);
        Assert.Empty(detail.DraftIssues);
        Assert.Equal(CrmModule.Entities.Lead, detail.Rule.EntityKey);
        Assert.Equal("Hareketsiz müşteri adayı için takip", detail.Draft!.Name);

        // Taslak çalışmaz: tarama turu hiçbir kaydı işlemez, hiçbir aktivite açılmaz.
        var round = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(0, round.Scanned);
        Assert.Empty(await RunsAsync(setup.Tenant.Id));
        Assert.Empty(await ActivitiesAsync(setup.Tenant.Id));

        // Firma gözden geçirip yayınlayınca aynı kural çalışır.
        var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{detail.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(1, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Equal(lead, Assert.Single(await RunsAsync(setup.Tenant.Id)).RecordId);
        Assert.Single(await ActivitiesAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Every_crm_template_passes_real_publish_validation()
    {
        var setup = await SetUpAsync();

        foreach (var template in CrmAutomationTemplates.All)
        {
            var use = await setup.Client.PostAsync($"/api/automation/templates/{template.Key}/use", null);
            Assert.Equal(HttpStatusCode.Created, use.StatusCode);
            var rule = (await use.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;

            // Yayın tam doğrulamayı çalıştırır; kapalı yayınlanır, böylece sonraki şablonlar etkilenmez.
            var publish = await setup.Client.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));
            Assert.True(publish.StatusCode == HttpStatusCode.OK, $"{template.Key}: {await publish.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Unknown_templates_and_templates_of_a_disabled_module_are_not_found()
    {
        var setup = await SetUpAsync();
        var unknown = await setup.Client.PostAsync("/api/automation/templates/crm.yok/use", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // CRM modülü kapalı firmada şablon listede yoktur ve kullanılamaz.
        var closed = await SetUpAsync(crmEnabled: false);
        Assert.Empty((await closed.Client.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!);
        var use = await closed.Client.PostAsync($"/api/automation/templates/{CrmAutomationTemplates.LeadInactivity}/use", null);
        Assert.Equal(HttpStatusCode.NotFound, use.StatusCode);
        Assert.Empty(await RulesAsync(closed.Tenant.Id));
    }

    [Fact]
    public async Task A_template_used_in_one_tenant_creates_no_rule_in_another()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();

        var use = await first.Client.PostAsync($"/api/automation/templates/{CrmAutomationTemplates.OpportunityInactivity}/use", null);
        Assert.Equal(HttpStatusCode.Created, use.StatusCode);
        var rule = (await use.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;

        Assert.Single(await RulesAsync(first.Tenant.Id));
        Assert.Empty(await RulesAsync(second.Tenant.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await second.Client.GetAsync($"/api/automation/rules/{rule.Rule.Id}")).StatusCode);
    }

    [Fact]
    public async Task Listing_needs_read_using_needs_manage_and_a_template_whose_action_is_not_permitted_is_refused()
    {
        var setup = await SetUpAsync();

        // Yalnızca okuma izni: listeler, kullanamaz.
        var reader = await MemberAsync(setup, CoreModule.Permissions.ReadAutomation, CrmModule.Permissions.LeadsRead, CrmModule.Permissions.OpportunitiesRead);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/automation/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/automation/templates/{CrmAutomationTemplates.LeadInactivity}/use", null)).StatusCode);

        // Hiç otomasyon izni yok: listeleyemez.
        var outsider = await MemberAsync(setup, CrmModule.Permissions.LeadsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/automation/templates")).StatusCode);

        // Aktivite yazma izni yok: şablon "kullanılamıyor" görünür ve taslağı açılmaz (yetki yükseltme yok).
        var manager = await MemberAsync(
            setup, CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CrmModule.Permissions.LeadsRead, CrmModule.Permissions.OpportunitiesRead);
        var templates = (await manager.GetFromJsonAsync<List<AutomationTemplateDto>>("/api/automation/templates"))!;
        Assert.All(templates, t => Assert.False(t.Available));
        Assert.All(templates, t => Assert.NotEmpty(t.Issues));
        var use = await manager.PostAsync($"/api/automation/templates/{CrmAutomationTemplates.LeadInactivity}/use", null);
        Assert.Equal(HttpStatusCode.BadRequest, use.StatusCode);
        Assert.Empty(await RulesAsync(setup.Tenant.Id));
    }

    // --- yardımcılar ---

    private async Task<Setup> SetUpAsync(bool crmEnabled = true)
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        await fixture.Factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: crmEnabled);
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        return new Setup(tenant, admin, await LoginAsync(admin));
    }

    private async Task<HttpClient> MemberAsync(Setup setup, params string[] permissions)
    {
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, permissions, RecordScope.All, member);
        return await LoginAsync(member);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<Guid> CreateLeadAsync(Setup setup, string subject)
    {
        var response = await setup.Client.PostAsJsonAsync(
            $"/api/records/{CrmModule.Entities.Lead}", new RecordWriteRequest(new JsonObject { ["subject"] = subject, ["lastName"] = "Kaya" }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
    }

    private async Task<AutomationRound> RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return round ?? throw new InvalidOperationException("Tur çalışmadı.");
    }

    private async Task<IReadOnlyList<AutomationRule>> RulesAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<AutomationRule>().AsNoTracking().ToListAsync();
    }

    private async Task<IReadOnlyList<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<IReadOnlyList<Activity>> ActivitiesAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Activity>().AsNoTracking()
            .Where(a => a.ExternalSource == "automation").ToListAsync();
    }
}
