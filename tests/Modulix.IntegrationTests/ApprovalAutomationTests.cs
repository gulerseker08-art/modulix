using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Automation;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 4c-2: kural aksiyonu "Onaya gönder" uçtan uca. Süreç kayda bağlıdır (Dilim 4c): tutar kayıttan okunur. Talep sahibi
/// kaydın sorumlusudur (dört göz onun için), aynı adım ya da aynı kayıt ikinci talep açmaz, süreç adı ve kayıt türü yayında doğrulanır.
/// Gerçek veritabanı, kayıt boru hattı ve RLS altında.
/// </summary>
public class ApprovalAutomationTests(CrmAutomationFixture fixture) : IClassFixture<CrmAutomationFixture>
{
    private sealed record Setup(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Approver, HttpClient Client);

    private const string RequestAction = "core.approval.request";

    private static AutomationDefinition ApprovalRule(string process, string title = "İndirim onayı: {{crm.opportunity.title}}") =>
        new(
            new ScheduledTrigger("00:00"),
            null,
            [
                new AutomationActionStep("onay", RequestAction, new JsonObject
                {
                    ["process"] = process,
                    ["title"] = title,
                }),
            ]);

    [Fact]
    public async Task A_rule_opens_one_request_linked_to_the_record_with_the_owner_as_requester_and_never_duplicates_it()
    {
        var setup = await SetUpAsync();
        var process = await CreateProcessAsync(setup, approver: setup.Approver);
        var opportunity = await CreateOpportunityAsync(setup, "Anka Lojistik anlaşması", amount: 5000m);
        await PublishAsync(setup.Client, ApprovalRule(process.Name));

        await RunRoundAsync(setup.Tenant.Id);

        var run = Assert.Single(await RunsAsync(setup.Tenant.Id));
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        var request = Assert.Single((await ListRequestsAsync(setup.Client)).Items);
        Assert.Equal("İndirim onayı: Anka Lojistik anlaşması", request.Title);
        Assert.Equal(CrmModule.Entities.Opportunity, request.TargetEntityKey);
        Assert.Equal(opportunity, request.TargetRecordId);
        Assert.Equal("Anka Lojistik anlaşması", request.TargetLabel);
        Assert.Equal(setup.Admin.Id, request.RequesterId); // kaydın sorumlusu (oluşturan)
        Assert.Equal((5000m, "TRY"), (request.Amount, request.Currency)); // kayıttan okundu
        Assert.Equal("Pending", request.Status);

        // Onaycı kendi kararını bekleyen talebi görür.
        using var approver = await LoginAsync(setup.Approver);
        Assert.Single((await ListRequestsAsync(approver, "Waiting")).Items);

        // Aynı gün ikinci tur ve aynı adımın yeniden teslimi ikinci talep açmaz.
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Single((await ListRequestsAsync(setup.Client)).Items);
    }

    [Fact]
    public async Task Unknown_unbound_or_closed_processes_are_refused_at_publish_and_fail_clearly_when_they_close_later_or_leave_no_other_approver()
    {
        var setup = await SetUpAsync();

        // Yayın: bu firmada böyle bir etkin süreç yok.
        var unknown = await TryPublishAsync(setup.Client, ApprovalRule("Olmayan süreç"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("etkin bir onay süreci yok", await unknown.Content.ReadAsStringAsync());

        // Yayın: süreç var ama kuralın kayıt türüne bağlı değil (serbest süreç).
        var unbound = await CreateProcessAsync(setup, approver: setup.Approver, targetEntityKey: null);
        var notBound = await TryPublishAsync(setup.Client, ApprovalRule(unbound.Name));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notBound.StatusCode);
        Assert.Contains("bu kayıt türüne bağlı değil", await notBound.Content.ReadAsStringAsync());

        // Süreç yayından sonra kapanırsa kural açıkça başarısız olur, sessizce atlamaz.
        var process = await CreateProcessAsync(setup, approver: setup.Approver);
        await CreateOpportunityAsync(setup, "Kapanan süreç", amount: 1000m);
        await PublishAsync(setup.Client, ApprovalRule(process.Name));
        Assert.Equal(HttpStatusCode.OK, (await setup.Client.PutAsJsonAsync($"/api/approvals/definitions/{process.Id}/active", new SetApprovalDefinitionActiveRequest(false))).StatusCode);
        await RunRoundAsync(setup.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Failed, Assert.Single(await RunsAsync(setup.Tenant.Id)).Status);
        Assert.Empty((await ListRequestsAsync(setup.Client)).Items);

        // Dört göz: sürecin tek onaycısı talep sahibi (kaydın sorumlusu) ise talep açılmaz.
        var other = await SetUpAsync();
        var ownOnly = await CreateProcessAsync(other, approver: other.Admin);
        await CreateOpportunityAsync(other, "Tek onaycı sahibi", amount: 1000m);
        await PublishAsync(other.Client, ApprovalRule(ownOnly.Name));
        await RunRoundAsync(other.Tenant.Id);
        Assert.Equal(AutomationRunStatus.Failed, Assert.Single(await RunsAsync(other.Tenant.Id)).Status);
        Assert.Empty((await ListRequestsAsync(other.Client)).Items);
    }

    [Fact]
    public async Task Each_tenants_rule_opens_requests_only_from_its_own_records_and_processes()
    {
        var first = await SetUpAsync();
        var second = await SetUpAsync();
        var sharedName = $"Ortak onay {Guid.NewGuid():N}"[..24];
        await CreateProcessAsync(first, approver: first.Approver, name: sharedName);
        await CreateProcessAsync(second, approver: second.Approver, name: sharedName);
        await CreateOpportunityAsync(first, "Birinci firmanın fırsatı", amount: 100m);
        await CreateOpportunityAsync(second, "İkinci firmanın fırsatı", amount: 200m);
        await PublishAsync(first.Client, ApprovalRule(sharedName));
        await PublishAsync(second.Client, ApprovalRule(sharedName));

        await RunRoundAsync(first.Tenant.Id);
        await RunRoundAsync(second.Tenant.Id);

        Assert.Equal("İndirim onayı: Birinci firmanın fırsatı", Assert.Single((await ListRequestsAsync(first.Client)).Items).Title);
        Assert.Equal("İndirim onayı: İkinci firmanın fırsatı", Assert.Single((await ListRequestsAsync(second.Client)).Items).Title);
    }

    [Fact]
    public async Task Publishing_the_approval_action_needs_the_request_approvals_permission()
    {
        var setup = await SetUpAsync();
        var process = await CreateProcessAsync(setup, approver: setup.Approver);
        var withoutRequest = new[]
        {
            CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, CoreModule.Permissions.PublishAutomation,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunityAmountsRead,
        };
        var member = await fixture.Factory.CreateUserAsync(setup.Tenant);
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, withoutRequest, member);
        using var client = await LoginAsync(member);

        // Onay talebi açma izni olmayan, bu aksiyonla kural yayınlayamaz (yetki yükseltme yok).
        var denied = await TryPublishAsync(client, ApprovalRule(process.Name));
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);

        // İzni verilince aynı kural yayınlanır (rol atama kullanıcının rolünü değiştirir; bu yüzden izinlerin tamamı yeni rolde).
        await fixture.Factory.CreateRoleAsync(setup.Tenant.Id, [.. withoutRequest, CoreModule.Permissions.RequestApprovals], member);
        Assert.Equal(HttpStatusCode.OK, (await TryPublishAsync(client, ApprovalRule(process.Name))).StatusCode);
    }

    // --- yardımcılar ---

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

    // Süreç varsayılan olarak fırsata bağlıdır (Dilim 4c): tutar kayıttan okunur, kural aksiyonu yalnızca kayda bağlı süreçle çalışır.
    private async Task<ApprovalDefinitionDto> CreateProcessAsync(
        Setup setup, ModulixUser approver, string? name = null, string? targetEntityKey = CrmModule.Entities.Opportunity)
    {
        var response = await setup.Client.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            name ?? $"İndirim {Guid.NewGuid():N}"[..24], "Fırsat indirimleri", "TRY", [new ApprovalStepDefinitionDto("Yönetici", 1, approver.Id, null, null, null)],
            null, targetEntityKey));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApprovalDefinitionDto>())!;
    }

    private async Task<Guid> CreateOpportunityAsync(Setup setup, string title, decimal amount)
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

    private static async Task<HttpResponseMessage> TryPublishAsync(HttpClient client, AutomationDefinition definition)
    {
        var create = await client.PostAsJsonAsync("/api/automation/rules", new CreateAutomationRuleRequest(
            CrmModule.ModuleKey, CrmModule.Entities.Opportunity, $"Kural {Guid.NewGuid():N}"[..20], null, 100, false, null, null,
            JsonSerializer.SerializeToElement(definition, AutomationJson.Options)));
        if (create.StatusCode != HttpStatusCode.Created)
        {
            return create;
        }

        var created = (await create.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
        return await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));
    }

    private static async Task PublishAsync(HttpClient client, AutomationDefinition definition)
    {
        var response = await TryPublishAsync(client, definition);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<ApprovalRequestPageDto> ListRequestsAsync(HttpClient client, string view = "All")
    {
        var response = await client.GetAsync($"/api/approvals/requests?view={view}&pageSize=50");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestPageDto>())!;
    }

    private async Task RunRoundAsync(Guid tenantId)
    {
        var (round, skipped) = await fixture.Extended.Services.GetRequiredService<AutomationRunner>().RunTenantAsync(tenantId, CancellationToken.None);
        Assert.False(skipped);
        Assert.NotNull(round);
    }

    private async Task<IReadOnlyList<AutomationRun>> RunsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AutomationRuns.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync();
    }
}
