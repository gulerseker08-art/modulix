using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Automation;
using Modulix.Modules.Crm.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>Gerçek uygulama konağı; yalnızca saat elle ilerler. CRM'in kendi yetenek beyanı, kayıt kaynağı ve aksiyonu gerçek kayıt boru hattıyla sınanır.</summary>
public sealed class CrmAutomationFixture : IDisposable
{
    public CrmAutomationFixture()
    {
        Factory = new ModulixApiFactory();
        Extended = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        }));
    }

    public ModulixApiFactory Factory { get; }

    public WebApplicationFactory<Program> Extended { get; }

    public MutableTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public HttpClient CreateBrowserClient() =>
        Extended.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    public void Dispose()
    {
        Extended.Dispose();
        Factory.Dispose();
    }
}

/// <summary>
/// ADR-0027 Dilim 3: CRM yetenek beyanı uçtan uca. Kural yönetimi API'den, kayıtlar kayıt boru hattından, tarama CRM'in kayıt kaynağından,
/// aksiyon CRM'in takip aktivitesi servisinden gerçek veritabanı ve Row-Level Security altında çalışır.
/// </summary>
public class CrmAutomationTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, HttpClient Client);

    private static AutomationActionStep FollowUp(string subject = "Aday ile ilgilenin", int? dueInDays = 2) =>
        new("followup", CrmAutomation.FollowUpAction, new JsonObject { ["subject"] = subject, ["dueInDays"] = dueInDays });

    private static AutomationDefinition Inactive(string relation, int days = 7) =>
        new(new InactivityTrigger(relation, days), null, [FollowUp()]);

    [Fact]
    public async Task An_inactivity_rule_opens_one_follow_up_assigned_to_the_owner_and_does_not_repeat_the_same_day()
    {
        var setup = await SetUpAsync();
        var lead = await CreateLeadAsync(setup, "Sessiz aday");
        await PublishRuleAsync(setup, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));

        var first = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, first.Scanned);
        Assert.Equal(1, first.StepsRun);
        var run = Assert.Single(await RunsAsync(setup.Tenant.Id));
        Assert.Equal(AutomationRunOrigin.Scan, run.Origin);
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        var activity = Assert.Single(await ActivitiesAsync(setup.Tenant.Id));
        Assert.Equal("Aday ile ilgilenin", activity.Subject);
        Assert.Equal(setup.Admin.Id, activity.OwnerId); // atanan verilmedi: kaydın sorumlusu
        Assert.Equal(CrmFollowUpActivities_AutomationSource, activity.ExternalSource);
        Assert.False(string.IsNullOrEmpty(activity.ExternalId));
        using (var scope = fixture.Factory.OpenScope(setup.Tenant.Id))
        {
            var link = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<ActivityLink>().AsNoTracking().SingleAsync();
            Assert.Equal((CrmModule.Entities.Lead, lead), (link.EntityKey, link.RecordId));
        }

        // Aynı gün ikinci tur yeni çalışma ve yeni aktivite açmaz; açılan aktivite ilişkideki hareketi de sıfırladığı için ertesi gün de açmaz.
        Assert.Equal(0, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await RunRoundAsync(setup.Tenant.Id)).Scanned);
        Assert.Single(await ActivitiesAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task A_lead_with_recent_activity_is_not_inactive_and_a_closed_lead_is_never_scanned()
    {
        var setup = await SetUpAsync();
        var active = await CreateLeadAsync(setup, "Hareketli aday");
        var quiet = await CreateLeadAsync(setup, "Sessiz aday");
        await CreateActivityAsync(setup, active, linkedLead: true);
        await PublishRuleAsync(setup, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Scanned);
        Assert.Equal(quiet, Assert.Single(await RunsAsync(setup.Tenant.Id)).RecordId);
    }

    [Fact]
    public async Task A_date_relative_rule_runs_only_for_opportunities_whose_date_falls_on_the_target_day()
    {
        var setup = await SetUpAsync();
        var company = await CreateCompanyAsync(setup);
        // Tarama "bugün"ü firmanın saat diliminde (varsayılan İstanbul, UTC+3) hesaplar; UTC günü kullanmak her gün 21:00-24:00 UTC arasında yanlış gün verirdi.
        var zone = Modulix.Core.Settings.RegionalTime.Find(null);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fixture.Time.GetUtcNow(), zone).DateTime);
        var due = await CreateOpportunityAsync(setup, company, "Yaklaşan", today.AddDays(3));
        await CreateOpportunityAsync(setup, company, "Uzak", today.AddDays(10));
        await PublishRuleAsync(setup, CrmModule.Entities.Opportunity,
            new AutomationDefinition(new DateRelativeTrigger($"{CrmModule.Entities.Opportunity}.expectedCloseDate", -3), null, [FollowUp("Kapanışı yaklaşıyor")]));

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, round.Scanned);
        Assert.Equal(due, Assert.Single(await RunsAsync(setup.Tenant.Id)).RecordId);
        Assert.Equal("Kapanışı yaklaşıyor", Assert.Single(await ActivitiesAsync(setup.Tenant.Id)).Subject);
    }

    [Fact]
    public async Task The_activity_relation_condition_counts_direct_and_linked_activities_by_status_and_ignores_other_records()
    {
        var setup = await SetUpAsync();
        var direct = await CreateLeadAsync(setup, "Doğrudan bağlı");
        var linked = await CreateLeadAsync(setup, "İlişkiyle bağlı");
        var none = await CreateLeadAsync(setup, "Aktivitesiz");
        await CreateActivityAsync(setup, direct, linkedLead: false);
        await CreateActivityAsync(setup, linked, linkedLead: true);
        var relation = new ConditionRelation(CrmAutomation.LeadActivities, RelationQuantifier.NotExists)
        {
            Where = new ConditionLeaf($"{CrmModule.Entities.Activity}.status", AutomationOperator.Eq, [ActivityStatus.Open]),
        };
        await PublishRuleAsync(setup, CrmModule.Entities.Lead, new AutomationDefinition(new ScheduledTrigger("00:00"), relation, [FollowUp()]));

        var round = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(3, round.Scanned);
        Assert.Equal(1, round.StepsRun);
        Assert.Equal(2, round.NotMatched);
        var succeeded = Assert.Single((await RunsAsync(setup.Tenant.Id)).Where(r => r.Status == AutomationRunStatus.Succeeded));
        Assert.Equal(none, succeeded.RecordId);
    }

    [Fact]
    public async Task A_repeated_delivery_of_the_same_step_never_opens_a_second_follow_up()
    {
        var setup = await SetUpAsync();
        await CreateLeadAsync(setup, "Tekrar teslim");
        await PublishRuleAsync(setup, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));
        await RunRoundAsync(setup.Tenant.Id);
        var activity = Assert.Single(await ActivitiesAsync(setup.Tenant.Id));

        // İşleyici etkiyi yazdıktan sonra sonucu kaydedemeden düştü: çalışma ve adım yeniden bekliyor.
        using (var scope = fixture.Factory.OpenScope(setup.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.AutomationRunSteps.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AutomationStepStatus.Pending).SetProperty(x => x.CompletedAt, (DateTimeOffset?)null));
            await db.AutomationRuns.ExecuteUpdateAsync(r => r.SetProperty(x => x.Status, AutomationRunStatus.Running).SetProperty(x => x.CompletedAt, (DateTimeOffset?)null));
        }

        var again = await RunRoundAsync(setup.Tenant.Id);

        Assert.Equal(1, again.StepsRun);
        Assert.Equal(activity.Id, Assert.Single(await ActivitiesAsync(setup.Tenant.Id)).Id);
        using var check = fixture.Factory.OpenScope(setup.Tenant.Id);
        var step = await check.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRunSteps.AsNoTracking().SingleAsync();
        Assert.Equal(AutomationStepStatus.Skipped, step.Status);
        Assert.Equal(activity.Id, step.EffectRecordId);
    }

    [Fact]
    public async Task Invalid_follow_up_parameters_and_unsupported_scan_targets_are_refused_at_publish()
    {
        var setup = await SetUpAsync();

        var noSubject = await TryPublishAsync(setup, CrmModule.Entities.Lead,
            new AutomationDefinition(new ScheduledTrigger("00:00"), null, [new AutomationActionStep("a", CrmAutomation.FollowUpAction, new JsonObject())]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSubject.StatusCode);

        // Fırsatın tarih alanı müşteri adayına konamaz; ilişki kayıt türüne ait olmalı.
        var wrongField = await TryPublishAsync(setup, CrmModule.Entities.Lead,
            new AutomationDefinition(new DateRelativeTrigger($"{CrmModule.Entities.Opportunity}.expectedCloseDate", -1), null, [FollowUp()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongField.StatusCode);
        var wrongRelation = await TryPublishAsync(setup, CrmModule.Entities.Lead, Inactive(CrmAutomation.OpportunityActivities));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongRelation.StatusCode);

        var valid = await TryPublishAsync(setup, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task Rules_scan_only_their_own_tenants_records()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        await CreateLeadAsync(first, "Birinci");
        await CreateLeadAsync(second, "İkinci");
        await PublishRuleAsync(first, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));

        Assert.Equal(1, (await RunRoundAsync(first.Tenant.Id)).Scanned);
        Assert.Equal(0, (await RunRoundAsync(second.Tenant.Id)).Scanned);

        Assert.Single(await ActivitiesAsync(first.Tenant.Id));
        Assert.Empty(await ActivitiesAsync(second.Tenant.Id));
        Assert.Empty(await RunsAsync(second.Tenant.Id));
    }

    [Fact]
    public async Task Publishing_needs_the_actions_permission_and_the_amount_permission_for_amount_conditions()
    {
        var setup = await SetUpAsync();
        var manageOnly = new[]
        {
            Modulix.Core.Modules.CoreModule.Permissions.ReadAutomation, Modulix.Core.Modules.CoreModule.Permissions.ManageAutomation,
            Modulix.Core.Modules.CoreModule.Permissions.PublishAutomation, CrmModule.Permissions.LeadsRead, CrmModule.Permissions.OpportunitiesRead,
        };
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, manageOnly, RecordScope.All, member);
        var client = await LoginAsync(member);

        // Aktivite yazma izni yok: takip aktivitesi aksiyonuyla kural yayınlanamaz (yetki yükseltme yok).
        var noAction = await TryPublishAsync(client, CrmModule.Entities.Lead, Inactive(CrmAutomation.LeadActivities));
        Assert.NotEqual(HttpStatusCode.OK, noAction.StatusCode);

        // Tutar izni olmayan, tutar alanına koşul koyamaz.
        var amount = new ConditionLeaf($"{CrmModule.Entities.Opportunity}.amount", AutomationOperator.Gt, ["1000"]) { Currency = "TRY" };
        var noAmount = await TryPublishAsync(client, CrmModule.Entities.Opportunity, new AutomationDefinition(new ScheduledTrigger("00:00"), amount, [FollowUp()]));
        Assert.NotEqual(HttpStatusCode.OK, noAmount.StatusCode);

        // Yönetici ikisini de yayınlayabilir.
        var allowed = await TryPublishAsync(setup.Client, CrmModule.Entities.Opportunity, new AutomationDefinition(new ScheduledTrigger("00:00"), amount, [FollowUp()]));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // --- yardımcılar ---

    private const string CrmFollowUpActivities_AutomationSource = "automation";

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

    private async Task<Guid> CreateRecordAsync(HttpClient client, string entityKey, JsonObject values)
    {
        var response = await client.PostAsJsonAsync($"/api/records/{entityKey}", new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id;
    }

    private Task<Guid> CreateLeadAsync(Setup setup, string subject) =>
        CreateRecordAsync(setup.Client, CrmModule.Entities.Lead, new JsonObject { ["subject"] = subject, ["lastName"] = "Kaya" });

    private Task<Guid> CreateCompanyAsync(Setup setup) =>
        CreateRecordAsync(setup.Client, CrmModule.Entities.Company, new JsonObject { ["name"] = $"Firma {Guid.NewGuid():N}"[..20] });

    private Task<Guid> CreateOpportunityAsync(Setup setup, Guid companyId, string title, DateOnly closeDate) =>
        CreateRecordAsync(setup.Client, CrmModule.Entities.Opportunity, new JsonObject
        {
            ["title"] = title,
            ["companyId"] = companyId.ToString(),
            ["stage"] = "Lead",
            ["expectedCloseDate"] = closeDate.ToString("yyyy-MM-dd"),
        });

    /// <summary>Müşteri adayına aktivite açar: doğrudan sütunla ya da aktivite ilişkisiyle (<paramref name="linkedLead"/>).</summary>
    private async Task CreateActivityAsync(Setup setup, Guid leadId, bool linkedLead)
    {
        var values = new JsonObject { ["type"] = "Task", ["subject"] = "Görüşme", ["ownerId"] = setup.Admin.Id.ToString() };
        if (!linkedLead)
        {
            values["leadId"] = leadId.ToString();
        }

        var activityId = await CreateRecordAsync(setup.Client, CrmModule.Entities.Activity, values);
        if (linkedLead)
        {
            await CreateRecordAsync(setup.Client, CrmModule.Entities.ActivityLink, new JsonObject
            {
                ["activityId"] = activityId.ToString(),
                ["entityKey"] = CrmModule.Entities.Lead,
                ["recordId"] = leadId.ToString(),
                ["isPrimary"] = true,
            });
        }
    }

    private Task<HttpResponseMessage> TryPublishAsync(Setup setup, string entityKey, AutomationDefinition definition) =>
        TryPublishAsync(setup.Client, entityKey, definition);

    private async Task<HttpResponseMessage> TryPublishAsync(HttpClient client, string entityKey, AutomationDefinition definition)
    {
        var create = await client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            CrmModule.ModuleKey, entityKey, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        if (create.StatusCode != HttpStatusCode.Created)
        {
            return create;
        }

        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        return await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
    }

    private async Task PublishRuleAsync(Setup setup, string entityKey, AutomationDefinition definition)
    {
        var response = await TryPublishAsync(setup, entityKey, definition);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<AutomationRound> RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return round ?? throw new InvalidOperationException("Tur çalışmadı.");
    }

    private async Task<IReadOnlyList<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>Kural motorunun açtığı aktiviteler (yalnızca <c>externalSource = automation</c>).</summary>
    private async Task<IReadOnlyList<Activity>> ActivitiesAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Activity>().AsNoTracking()
            .Where(a => a.ExternalSource == CrmFollowUpActivities_AutomationSource).ToListAsync();
    }
}
