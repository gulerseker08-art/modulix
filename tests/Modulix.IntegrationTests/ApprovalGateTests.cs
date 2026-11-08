using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Crm;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Ç6a.3: onay kapısı. Firma fırsatı kazanıldı olarak kapatmayı bir onay sürecine bağlar; onaylanmamış fırsat kazanılamaz.
/// Onaydan sonra tutar değişirse onay geçersizleşir ve kapı yeniden kapanır. Bağlanmamış işlem serbesttir; kapı tenant'a özeldir.
/// </summary>
[Collection(EventTestCollection.Name)]
public class ApprovalGateTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Opportunity = CrmModule.Entities.Opportunity;
    private const string Win = CrmModule.ApprovalCommands.Win;

    private static readonly string[] SalesRep =
    [
        CoreModule.Permissions.RequestApprovals, CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite, CrmModule.Permissions.OpportunityAmountsRead,
    ];

    private sealed record Team(Tenant Tenant, ModulixUser Admin, ModulixUser Rep, ModulixUser Manager);

    [Fact]
    public async Task A_gated_opportunity_cannot_be_won_until_its_approval_is_granted()
    {
        Team team = await SetUpAsync();
        using HttpClient rep = await LoginAsync(team.Rep);
        using HttpClient manager = await LoginAsync(team.Manager);
        ApprovalDefinitionDto process = await CreateProcessAsync(team, [Win]);
        Assert.Equal([Win], process.GatedCommands);
        OpportunityDto opportunity = await CreateOpportunityAsync(rep, 250_000m);

        RecordApprovalViewDto view = await GetAsync<RecordApprovalViewDto>(rep, $"/api/approvals/records/{Opportunity}/{opportunity.Id}");
        Assert.Equal(["Kazanıldı olarak kapatma"], view.RequiredFor);

        string refused = await RefusedWinAsync(rep, opportunity);
        Assert.Contains("onay gerekiyor", refused);

        ApprovalRequestDto opened = await ReadAsync<ApprovalRequestDto>(await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id)), HttpStatusCode.Created);
        Assert.Contains("onay bekleniyor", await RefusedWinAsync(rep, opportunity));

        await ReadAsync<ApprovalRequestDto>(await manager.PostAsJsonAsync(
            $"/api/approvals/requests/{opened.Id}/approve", new DecideApprovalRequest(null, opened.Version)), HttpStatusCode.OK);

        OpportunityActionResultDto won = await WinAsync(rep, opportunity);
        Assert.Equal("Won", won.Opportunity.Status);
    }

    [Fact]
    public async Task Changing_the_amount_after_approval_invalidates_it_and_closes_the_gate_again()
    {
        Team team = await SetUpAsync();
        using HttpClient rep = await LoginAsync(team.Rep);
        using HttpClient manager = await LoginAsync(team.Manager);
        ApprovalDefinitionDto process = await CreateProcessAsync(team, [Win]);
        OpportunityDto opportunity = await CreateOpportunityAsync(rep, 250_000m);
        ApprovalRequestDto opened = await ReadAsync<ApprovalRequestDto>(await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id)), HttpStatusCode.Created);
        await ReadAsync<ApprovalRequestDto>(await manager.PostAsJsonAsync(
            $"/api/approvals/requests/{opened.Id}/approve", new DecideApprovalRequest(null, opened.Version)), HttpStatusCode.OK);

        // Onaylanan tutar 250 bindi; fırsat 900 bine çıkınca verilen onay geçersizleşir.
        OpportunityDto raised = await ReadAsync<OpportunityDto>(await rep.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest("Depo projesi", opportunity.CompanyId, Amount: 900_000m, Version: opportunity.Version)), HttpStatusCode.OK);
        Assert.Equal("Invalidated", (await WaitForStatusAsync(rep, team.Tenant.Id, opened.Id, "Invalidated")).Status);

        Assert.Contains("geçersizleşti", await RefusedWinAsync(rep, raised));
    }

    [Fact]
    public async Task Gating_needs_a_record_bound_process_and_a_declared_command()
    {
        Team team = await SetUpAsync();
        using HttpClient admin = await LoginAsync(team.Admin);

        HttpResponseMessage unbound = await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Serbest {Guid.NewGuid():N}"[..20], null, null, [Step(team)], null, null, [Win]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unbound.StatusCode);

        HttpResponseMessage unknown = await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Bilinmeyen {Guid.NewGuid():N}"[..20], null, "TRY", [Step(team)], null, Opportunity, ["delete"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        // Kayda bağlı ama işlemi bağlamayan süreç kazanmayı engellemez.
        await CreateProcessAsync(team, []);
        using HttpClient rep = await LoginAsync(team.Rep);
        OpportunityDto free = await CreateOpportunityAsync(rep, 10_000m);
        Assert.Equal("Won", (await WinAsync(rep, free)).Opportunity.Status);
    }

    [Fact]
    public async Task One_tenants_gate_never_blocks_another_tenant()
    {
        Team first = await SetUpAsync();
        Team second = await SetUpAsync();
        await CreateProcessAsync(first, [Win]);

        using HttpClient firstRep = await LoginAsync(first.Rep);
        using HttpClient secondRep = await LoginAsync(second.Rep);
        Assert.Contains("onay gerekiyor", await RefusedWinAsync(firstRep, await CreateOpportunityAsync(firstRep, 50_000m)));
        Assert.Equal("Won", (await WinAsync(secondRep, await CreateOpportunityAsync(secondRep, 50_000m))).Opportunity.Status);
    }

    [Fact]
    public async Task Only_a_process_manager_can_gate_an_operation()
    {
        Team team = await SetUpAsync();
        using HttpClient rep = await LoginAsync(team.Rep);

        HttpResponseMessage denied = await rep.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Yetkisiz {Guid.NewGuid():N}"[..20], null, "TRY", [Step(team)], null, Opportunity, [Win]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Won", (await WinAsync(rep, await CreateOpportunityAsync(rep, 5_000m))).Opportunity.Status);
    }

    // --- yardımcılar ---

    private static ApprovalStepDefinitionDto Step(Team team) => new("Satış müdürü", 1, team.Manager.Id, null, null, null);

    private async Task<Team> SetUpAsync()
    {
        Tenant tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        ModulixUser admin = await factory.CreateUserAsync(tenant);
        ModulixUser rep = await factory.CreateUserAsync(tenant);
        ModulixUser manager = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, SalesRep, RecordScope.All, rep);
        await factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.DecideApprovals], manager);
        return new Team(tenant, admin, rep, manager);
    }

    private async Task<ApprovalDefinitionDto> CreateProcessAsync(Team team, IReadOnlyList<string> gated)
    {
        using HttpClient admin = await LoginAsync(team.Admin);
        return await ReadAsync<ApprovalDefinitionDto>(await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Fırsat onayı {Guid.NewGuid():N}"[..24], null, "TRY", [Step(team)], null, Opportunity, gated)), HttpStatusCode.Created);
    }

    private static async Task<OpportunityDto> CreateOpportunityAsync(HttpClient client, decimal amount)
    {
        CompanyDto company = await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Müşteri {Guid.NewGuid():N}"[..20])), HttpStatusCode.Created);
        return await ReadAsync<OpportunityDto>(await client.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Depo projesi", company.Id, Amount: amount)), HttpStatusCode.Created);
    }

    private static async Task<OpportunityActionResultDto> WinAsync(HttpClient client, OpportunityDto opportunity) =>
        await ReadAsync<OpportunityActionResultDto>(
            await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(opportunity.Version)), HttpStatusCode.OK);

    /// <summary>Kazanma kapıya takılır: 422 ve kullanıcıya gösterilen neden.</summary>
    private static async Task<string> RefusedWinAsync(HttpClient client, OpportunityDto opportunity)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/win", new WinOpportunityRequestDto(opportunity.Version));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        return body;
    }

    // Paylaşımlı yerel veritabanında başka testlerin teslim kuyruğu birikebilir; dağıtıcı turları durum değişene kadar sürer.
    private async Task<ApprovalRequestDto> WaitForStatusAsync(HttpClient client, Guid tenantId, Guid requestId, string status)
    {
        ApprovalRequestDto current = await GetAsync<ApprovalRequestDto>(client, $"/api/approvals/requests/{requestId}");
        for (int i = 0; i < 10 && current.Status != status; i++)
        {
            for (int round = 0; round < 20; round++)
            {
                using IServiceScope scope = factory.OpenScope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
                if (result.Dispatched == 0 && result.Delivered == 0)
                {
                    break;
                }
            }

            current = await GetAsync<ApprovalRequestDto>(client, $"/api/approvals/requests/{requestId}");
        }

        return current;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        HttpClient client = factory.CreateBrowserClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url) => await ReadAsync<T>(await client.GetAsync(url), HttpStatusCode.OK);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
