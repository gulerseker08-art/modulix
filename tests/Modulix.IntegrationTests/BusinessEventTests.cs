using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Automation;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Logistics;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Testlere özel modül (ADR-0039): CRM'in iş olaylarına izinsiz abone. Tutar iznini bildirmediği için <c>wonAmount</c> ona gizlenir.
/// </summary>
public sealed class BusinessEventTestModule : IModule
{
    public const string ModuleKey = "bizevtest";
    public const string Subscription = "bizevtest.recorder";

    public string Key => ModuleKey;

    public string DisplayName => "İş olayı testi";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } = [];

    public IReadOnlyList<EventSubscription> EventSubscriptions { get; } =
    [
        EventSubscription.For<BusinessEventRecorder>(
            Subscription,
            [CrmModule.Events.OpportunityWon, CrmModule.Events.OpportunityLost, CrmModule.Events.LeadConverted],
            [CrmModule.Entities.Opportunity, CrmModule.Entities.Lead],
            []) with { Ordered = false },
    ];

    public void ConfigureServices(IServiceCollection services) => services.AddScoped<BusinessEventRecorder>();
}

public sealed class BusinessEventRecorder : IEventSubscriber
{
    public static ConcurrentQueue<EventEnvelope> Received { get; } = new();

    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Enqueue(envelope);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ADR-0039 Dilim 1: CRM'in açık iş komutları (<c>win</c>, <c>lose</c>, <c>convert</c>) iş olayını kayıt değişikliğiyle aynı işlemde
/// yayınlar; olay abonelere hassas alan süzgeciyle gider ve otomasyonun <c>DomainEvent</c> tetikleyicisi bu olayla uçtan uca çalışır.
/// Olay işleyicisi bütün tenant'ların olaylarını işlediği için sınıf olay koleksiyonunda sırayla koşar.
/// </summary>
[Collection(EventTestCollection.Name)]
public sealed class BusinessEventTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private const string Won = CrmModule.Events.OpportunityWon;

    private static readonly string[] SalesPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite, OpportunityAmountsRead,
        ActivitiesRead, ActivitiesWrite, OpportunitiesClose, LeadsRead, LeadsCreate, LeadsUpdate, LeadsQualify, LeadsConvert,
    ];

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;
    private readonly List<Guid> tenantIds = [];

    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser User, HttpClient Client);

    public BusinessEventTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        BusinessEventRecorder.Received.Clear();
        host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var module = new BusinessEventTestModule();
            services.AddSingleton(ModuleCatalog.Create(new CrmModule(), new LogisticsModule(), module));
            module.ConfigureServices(services);
        }));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Winning_publishes_one_versioned_event_in_the_same_transaction_and_a_domain_event_rule_runs_once()
    {
        var setup = await SetUpAsync(administrator: true);
        await PublishRuleAsync(setup, CrmModule.Entities.Opportunity, WonRule("Kazanılan fırsatı devret"));
        var company = await CompanyAsync(setup.Client);
        var opp = await OpportunityAsync(setup.Client, company.Id, "Kazanılacak", amount: 80_000m);

        var won = (await ReadAsync<OpportunityActionResultDto>(await setup.Client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win",
            new WinOpportunityRequestDto(opp.Version, WonAmount: 75_000m, WonCurrency: "TRY")))).Opportunity;

        // Outbox: beyanlı tür ve sürüm, kaydın sürümü sıra numarası, kazanan kişi, yalnızca beyan edilmiş alanlar; tek olay.
        var message = Assert.Single(await OutboxAsync(setup.Tenant.Id, opp.Id, Won));
        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal(CrmModule.Entities.Opportunity, message.EntityKey);
        Assert.Equal(won.Version, message.Sequence);
        Assert.Equal(setup.User.Id, message.UserId);
        var payload = JsonNode.Parse(message.Payload)!.AsObject();
        Assert.Equal(75_000m, payload["wonAmount"]!.GetValue<decimal>());
        Assert.Equal(company.Id.ToString(), payload["companyId"]!.GetValue<string>());
        Assert.All(payload.Select(p => p.Key), key => Assert.Contains(key, CrmModule.Events.OpportunityWonFields));

        // Teslim: izinsiz test aboneliği olayı alır ama hassas tutarı görmez; otomasyon aboneliği bekleyen çalışma açar.
        await DrainAsync();
        var received = Assert.Single(BusinessEventRecorder.Received.Where(e => e.RecordId == opp.Id));
        Assert.Equal(Won, received.EventType);
        Assert.Contains("wonAmount", received.HiddenFields);
        Assert.False(received.Payload.ContainsKey("wonAmount"));
        Assert.Equal(company.Id.ToString(), received.Payload["companyId"]!.GetValue<string>());

        var deliveries = await DeliveriesAsync(setup.Tenant.Id, message.Id);
        Assert.True(deliveries.All(d => d.Status == EventDeliveryStatus.Succeeded), string.Join("; ", deliveries.Select(d => $"{d.Subscription}: {d.Status} {d.LastError}")));
        Assert.Contains(deliveries, d => d.Subscription == CoreModule.Subscriptions.Automation);
        var run = Assert.Single(await RunsAsync(setup.Tenant.Id));
        Assert.Equal(AutomationRunOrigin.Event, run.Origin);
        Assert.Equal(message.Id, run.EventId);
        Assert.Equal(opp.Id, run.RecordId);

        // Çalıştırıcı: kural bir kez çalışır ve takip aktivitesi açar; olay yeniden teslim edilse ikinci çalışma ve aktivite olmaz.
        var round = await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(1, round.StepsRun);
        var activity = Assert.Single(await ActivitiesAsync(setup.Tenant.Id));
        Assert.Equal("Kazanılan fırsatı devret", activity.Subject);
        Assert.Equal(AutomationRunStatus.Succeeded, Assert.Single(await RunsAsync(setup.Tenant.Id)).Status);

        await ReplayAllDeliveriesAsync(setup.Tenant.Id);
        await DrainAsync();
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Single(await RunsAsync(setup.Tenant.Id));
        Assert.Single(await ActivitiesAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Losing_and_converting_publish_their_events_with_declared_fields_only()
    {
        var setup = await SetUpAsync();
        var company = await CompanyAsync(setup.Client);
        var opp = await OpportunityAsync(setup.Client, company.Id, "Kaybedilecek");
        var lost = (await ReadAsync<OpportunityActionResultDto>(await setup.Client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/lose",
            new LoseOpportunityRequestDto(opp.Version, "Price")))).Opportunity;

        var lostEvent = Assert.Single(await OutboxAsync(setup.Tenant.Id, opp.Id, CrmModule.Events.OpportunityLost));
        Assert.Equal(lost.Version, lostEvent.Sequence);
        var lostPayload = JsonNode.Parse(lostEvent.Payload)!.AsObject();
        Assert.Equal("Price", lostPayload["lossReason"]!.GetValue<string>());
        Assert.All(lostPayload.Select(p => p.Key), key => Assert.Contains(key, CrmModule.Events.OpportunityLostFields));
        Assert.Empty(await OutboxAsync(setup.Tenant.Id, opp.Id, Won));

        var lead = await QualifiedLeadAsync(setup.Client, "Yıllık sözleşme");
        var converted = await ReadAsync<ConvertLeadResultDto>(await setup.Client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/convert",
            new ConvertLeadRequest(lead.Version, CreateCompany: true, CreateContact: true, OpportunityTitle: "Delta yıllık bakım")));

        var convertedEvent = Assert.Single(await OutboxAsync(setup.Tenant.Id, lead.Id, CrmModule.Events.LeadConverted));
        Assert.Equal(CrmModule.Entities.Lead, convertedEvent.EntityKey);
        Assert.Equal(converted.Lead.Version, convertedEvent.Sequence);
        var convertedPayload = JsonNode.Parse(convertedEvent.Payload)!.AsObject();
        Assert.Equal(converted.OpportunityId.ToString(), convertedPayload["convertedOpportunityId"]!.GetValue<string>());
        Assert.Equal(converted.CompanyId.ToString(), convertedPayload["convertedCompanyId"]!.GetValue<string>());
        Assert.True(convertedPayload["companyCreated"]!.GetValue<bool>());
        Assert.All(convertedPayload.Select(p => p.Key), key => Assert.Contains(key, CrmModule.Events.LeadConvertedFields));

        await DrainAsync();
        Assert.Contains(BusinessEventRecorder.Received, e => e.EventType == CrmModule.Events.LeadConverted && e.RecordId == lead.Id);
        Assert.Contains(BusinessEventRecorder.Received, e => e.EventType == CrmModule.Events.OpportunityLost && e.RecordId == opp.Id);
    }

    [Fact]
    public async Task A_rule_on_a_record_event_key_or_unknown_key_is_refused_and_a_rejected_win_leaves_no_event()
    {
        var setup = await SetUpAsync(administrator: true);

        // Kayıt olayı anahtarı iş olayı tetikleyicisi değildir; bilinmeyen anahtar da yayınlanamaz (422 + issues).
        var recordKey = await TryPublishAsync(setup, CrmModule.Entities.Opportunity, new AutomationDefinition(new DomainEventTrigger("crm.opportunity.updated"), null, [FollowUp()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, recordKey.StatusCode);
        Assert.Contains("eventKey", await recordKey.Content.ReadAsStringAsync());
        var unknown = await TryPublishAsync(setup, CrmModule.Entities.Opportunity, new AutomationDefinition(new DomainEventTrigger("crm.opportunity.nope"), null, [FollowUp()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        // Müşteri adayı kaydına fırsat olayı konamaz.
        var wrongEntity = await TryPublishAsync(setup, CrmModule.Entities.Lead, WonRule());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongEntity.StatusCode);
        Assert.True((await TryPublishAsync(setup, CrmModule.Entities.Opportunity, WonRule())).IsSuccessStatusCode);

        // Doğrulamadan dönen komut olay bırakmaz: gelecekte kapanış tarihi reddedilir.
        var company = await CompanyAsync(setup.Client);
        var opp = await OpportunityAsync(setup.Client, company.Id, "Reddedilecek");
        var rejected = await setup.Client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win",
            new WinOpportunityRequestDto(opp.Version, ActualCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Empty(await OutboxAsync(setup.Tenant.Id, opp.Id, Won));
        await DrainAsync();
        Assert.Empty(await RunsAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task Events_and_rules_stay_within_their_own_tenant()
    {
        var first = await SetUpAsync(administrator: true);
        var second = await SetUpAsync(administrator: true);
        await PublishRuleAsync(first, CrmModule.Entities.Opportunity, WonRule("Birinci"));
        await PublishRuleAsync(second, CrmModule.Entities.Opportunity, WonRule("İkinci"));
        var company = await CompanyAsync(first.Client);
        var opp = await OpportunityAsync(first.Client, company.Id, "Birinci firmanın fırsatı", amount: 10_000m);

        await ReadAsync<OpportunityActionResultDto>(await first.Client.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version)));
        await DrainAsync();
        await RunRoundAsync(first.Tenant.Id);
        await RunRoundAsync(second.Tenant.Id);

        // İkinci tenant'ın bağlamında olay görünmez (sorgu süzgeci + RLS), kuralı çalışmaz, aktivitesi açılmaz.
        Assert.Empty(await OutboxAsync(second.Tenant.Id, opp.Id, Won));
        Assert.Single(await OutboxAsync(first.Tenant.Id, opp.Id, Won));
        Assert.Empty(await RunsAsync(second.Tenant.Id));
        Assert.Empty(await ActivitiesAsync(second.Tenant.Id));
        Assert.Single(await RunsAsync(first.Tenant.Id));
        Assert.Single(await ActivitiesAsync(first.Tenant.Id));
        Assert.All(BusinessEventRecorder.Received.Where(e => e.RecordId == opp.Id), e => Assert.Equal(first.Tenant.Id, e.TenantId));
    }

    [Fact]
    public async Task A_user_without_close_permission_cannot_win_and_no_event_is_written()
    {
        var setup = await SetUpAsync();
        var company = await CompanyAsync(setup.Client);
        var opp = await OpportunityAsync(setup.Client, company.Id, "Yetkisiz kapanış");

        var reader = await factory.CreateUserAsync(setup.Tenant);
        await factory.CreateRoleAsync(setup.Tenant.Id, [OpportunitiesRead, CompaniesRead], reader);
        using var readerClient = await LoginAsync(reader);
        var response = await readerClient.PostAsJsonAsync($"/api/crm/opportunities/{opp.Id}/win", new WinOpportunityRequestDto(opp.Version));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await OutboxAsync(setup.Tenant.Id, opp.Id, Won));
        Assert.Equal(Modulix.Shared.Crm.OpportunityStatus.Open, (await setup.Client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opp.Id}"))!.Status);
    }

    // --- yardımcılar ---

    private static AutomationActionStep FollowUp(string subject = "Takip") =>
        new("followup", CrmAutomation.FollowUpAction, new JsonObject { ["subject"] = subject, ["dueInDays"] = 1 });

    private static AutomationDefinition WonRule(string subject = "Takip") => new(new DomainEventTrigger(Won), null, [FollowUp(subject)]);

    private async Task<Setup> SetUpAsync(bool administrator = false)
    {
        var tenant = await factory.CreateTenantAsync();
        tenantIds.Add(tenant.Id);
        await SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        await SetModuleAsync(tenant.Id, BusinessEventTestModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        if (administrator)
        {
            await factory.MakeAdministratorAsync(tenant.Id, user);
        }
        else
        {
            await factory.CreateRoleAsync(tenant.Id, SalesPermissions, user);
        }

        return new Setup(tenant, user, await LoginAsync(user));
    }

    private async Task SetModuleAsync(Guid tenantId, string moduleKey, bool enabled)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModuleEntitlements>().SetEnabledAsync(tenantId, moduleKey, enabled, CancellationToken.None);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<CompanyDto> CompanyAsync(HttpClient client) =>
        await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Firma {Guid.NewGuid():N}"[..14])));

    private static async Task<OpportunityDto> OpportunityAsync(HttpClient client, Guid companyId, string title, decimal? amount = null)
    {
        var created = await ReadAsync<OpportunityDto>(await client.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest(title, companyId)));
        // Tipli PUT kaydın tamamını yazar: sorumlu korunur, yoksa kural "sorumlu yok" diye takip aktivitesi açmaz.
        return amount is null
            ? created
            : await ReadAsync<OpportunityDto>(await client.PutAsJsonAsync($"/api/crm/opportunities/{created.Id}",
                new SaveOpportunityRequest(created.Title, created.CompanyId, Amount: amount, Version: created.Version, OwnerId: created.OwnerId)));
    }

    private static async Task<LeadDto> QualifiedLeadAsync(HttpClient client, string subject)
    {
        var lead = await ReadAsync<LeadDto>(await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest(
            subject, FirstName: "Mehmet", LastName: "Kaya", CompanyName: "Delta Ltd.", Email: "kaya@firma.test", NeedDescription: "Kapsam netleşti",
            EstimatedBudget: 75_000m, Source: "Referral")));
        return await ReadAsync<LeadDto>(await client.PostAsJsonAsync($"/api/crm/leads/{lead.Id}/qualify", new QualifyLeadRequest(lead.Version)));
    }

    private static async Task<HttpResponseMessage> TryPublishAsync(Setup setup, string entityKey, AutomationDefinition definition)
    {
        var create = await setup.Client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            CrmModule.ModuleKey, entityKey, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        if (create.StatusCode != HttpStatusCode.Created)
        {
            return create;
        }

        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        return await setup.Client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
    }

    private static async Task PublishRuleAsync(Setup setup, string entityKey, AutomationDefinition definition)
    {
        var response = await TryPublishAsync(setup, entityKey, definition);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.Empty, Guid.Empty);
        return scope;
    }

    private async Task DrainAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            var dispatched = 0;
            var delivered = 0;
            foreach (var id in tenantIds.Distinct())
            {
                using var scope = TenantScope(id);
                var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
                dispatched += round.Dispatched;
                delivered += round.Delivered;
            }

            if (dispatched == 0 && delivered == 0)
            {
                return;
            }
        }
    }

    /// <summary>Tüm teslimleri yeniden "bekliyor" yapar: en az bir kez teslim garantisinin tekrarını taklit eder.</summary>
    private async Task ReplayAllDeliveriesAsync(Guid tenantId)
    {
        using var scope = TenantScope(tenantId);
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Database
            .ExecuteSqlRawAsync("DELETE FROM platform.EventDeliveries; UPDATE platform.OutboxMessages SET DispatchedAt = NULL");
    }

    private async Task<AutomationRound> RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await host.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        return round ?? throw new InvalidOperationException("Tur çalışmadı.");
    }

    private async Task<List<OutboxMessage>> OutboxAsync(Guid tenantId, Guid recordId, string eventType)
    {
        using var scope = TenantScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().OutboxMessages.AsNoTracking()
            .Where(o => o.RecordId == recordId && o.EventType == eventType).ToListAsync();
    }

    private async Task<List<EventDelivery>> DeliveriesAsync(Guid tenantId, Guid eventId)
    {
        using var scope = TenantScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EventDeliveries.AsNoTracking().Where(d => d.EventId == eventId).ToListAsync();
    }

    private async Task<List<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = TenantScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<List<Activity>> ActivitiesAsync(Guid tenantId)
    {
        using var scope = TenantScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Set<Activity>().AsNoTracking()
            .Where(a => a.ExternalSource == "automation").ToListAsync();
    }
}
