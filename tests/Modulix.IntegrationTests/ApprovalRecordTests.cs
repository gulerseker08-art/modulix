using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
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
/// ADR-0027 Dilim 4c: kayda bağlı onay. CRM fırsatı onaya gönderilir; tutar kayıttan okunur, kayıtta tutar değişince bekleyen talep
/// geçersizleşir. Kayıt yetkisi, tenant izolasyonu ve tek bekleyen talep kuralı.
/// </summary>
[Collection(EventTestCollection.Name)]
public class ApprovalRecordTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Opportunity = CrmModule.Entities.Opportunity;

    private static readonly string[] SalesRep =
    [
        CoreModule.Permissions.RequestApprovals, CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite, CrmModule.Permissions.OpportunityAmountsRead,
    ];

    private sealed record Team(Tenant Tenant, ModulixUser Admin, ModulixUser Rep, ModulixUser Manager);

    [Fact]
    public async Task Opportunity_goes_to_approval_with_its_own_amount_and_shows_status_on_the_record()
    {
        Team team = await SetUpAsync();
        using HttpClient rep = await LoginAsync(team.Rep);
        using HttpClient manager = await LoginAsync(team.Manager);
        ApprovalDefinitionDto process = await CreateProcessAsync(team);
        OpportunityDto opportunity = await CreateOpportunityAsync(rep, 250_000m);

        RecordApprovalViewDto before = await GetAsync<RecordApprovalViewDto>(rep, $"/api/approvals/records/{Opportunity}/{opportunity.Id}");
        Assert.Equal(process.Id, Assert.Single(before.Processes).Id);
        Assert.Null(before.Latest);

        // Girilen tutar yok sayılır: kayıttaki tutar geçer.
        ApprovalRequestDto opened = await ReadAsync<ApprovalRequestDto>(await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, 1m, "TRY", opportunity.Id)), HttpStatusCode.Created);
        Assert.Equal(250_000m, opened.Amount);
        Assert.Equal("TRY", opened.Currency);
        Assert.Equal("Depo projesi", opened.Title);
        Assert.Equal($"crm/firsatlar/{opportunity.Id}", opened.TargetLink);

        // Aynı kayıt için ikinci bekleyen talep açılmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id))).StatusCode);

        ApprovalRequestDto approved = await ReadAsync<ApprovalRequestDto>(await manager.PostAsJsonAsync(
            $"/api/approvals/requests/{opened.Id}/approve", new DecideApprovalRequest(null, opened.Version)), HttpStatusCode.OK);
        Assert.Equal("Approved", approved.Status);

        RecordApprovalViewDto after = await GetAsync<RecordApprovalViewDto>(rep, $"/api/approvals/records/{Opportunity}/{opportunity.Id}");
        Assert.Equal("Approved", after.Latest!.Status);
    }

    [Fact]
    public async Task Changing_the_amount_invalidates_the_pending_request()
    {
        Team team = await SetUpAsync();
        using HttpClient rep = await LoginAsync(team.Rep);
        ApprovalDefinitionDto process = await CreateProcessAsync(team);
        OpportunityDto opportunity = await CreateOpportunityAsync(rep, 250_000m);
        ApprovalRequestDto opened = await ReadAsync<ApprovalRequestDto>(await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id)), HttpStatusCode.Created);

        // Tutara dokunmayan değişiklik talebi bozmaz.
        OpportunityDto renamed = await UpdateAsync(rep, opportunity, "Depo projesi (revize)", 250_000m);
        await DrainAsync(team.Tenant.Id);
        Assert.Equal("Pending", (await GetAsync<ApprovalRequestDto>(rep, $"/api/approvals/requests/{opened.Id}")).Status);

        await UpdateAsync(rep, renamed, "Depo projesi (revize)", 400_000m);
        ApprovalRequestDto invalidated = await WaitForStatusAsync(rep, team.Tenant.Id, opened.Id, "Invalidated");
        Assert.Equal("Invalidated", invalidated.Status);
        Assert.All(invalidated.Steps, s => Assert.Equal("Cancelled", s.Status));

        // Yeni tutarla yeniden gönderilebilir.
        ApprovalRequestDto again = await ReadAsync<ApprovalRequestDto>(await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id)), HttpStatusCode.Created);
        Assert.Equal(400_000m, again.Amount);
    }

    [Fact]
    public async Task Record_the_user_cannot_read_cannot_be_sent_or_inspected()
    {
        Team team = await SetUpAsync();
        ModulixUser stranger = await factory.CreateUserAsync(team.Tenant);
        await factory.CreateRoleAsync(team.Tenant.Id, [CoreModule.Permissions.RequestApprovals], stranger);
        using HttpClient rep = await LoginAsync(team.Rep);
        using HttpClient other = await LoginAsync(stranger);
        ApprovalDefinitionDto process = await CreateProcessAsync(team);
        OpportunityDto opportunity = await CreateOpportunityAsync(rep, 10_000m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await other.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, null, null, null, null, opportunity.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/approvals/records/{Opportunity}/{opportunity.Id}")).StatusCode);

        // Kayda bağlı süreç kayıtsız açılmaz; serbest süreçte kayıt verilmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rep.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(process.Id, "Kayıtsız", null, 5m, "TRY"))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_record_cannot_be_sent_for_approval()
    {
        Team a = await SetUpAsync();
        Team b = await SetUpAsync();
        using HttpClient repA = await LoginAsync(a.Rep);
        using HttpClient repB = await LoginAsync(b.Rep);
        OpportunityDto foreign = await CreateOpportunityAsync(repA, 10_000m);
        ApprovalDefinitionDto processB = await CreateProcessAsync(b);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repB.PostAsJsonAsync("/api/approvals/requests",
            new OpenApprovalRequest(processB.Id, null, null, null, null, foreign.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await repB.GetAsync($"/api/approvals/records/{Opportunity}/{foreign.Id}")).StatusCode);
    }

    [Fact]
    public async Task Process_can_only_target_a_declared_record_type()
    {
        Team team = await SetUpAsync();
        using HttpClient admin = await LoginAsync(team.Admin);
        using HttpClient rep = await LoginAsync(team.Rep);

        List<ApprovalTargetDto> targets = await GetAsync<List<ApprovalTargetDto>>(admin, "/api/approvals/targets");
        Assert.Contains(targets, t => t.EntityKey == Opportunity);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/approvals/targets")).StatusCode);

        HttpResponseMessage unknown = await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            "Bilinmeyen tür", null, null, [new ApprovalStepDefinitionDto("Yönetici", 1, team.Manager.Id, null, null, null)], null, "crm.company-secret"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

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

    private async Task<ApprovalDefinitionDto> CreateProcessAsync(Team team)
    {
        using HttpClient admin = await LoginAsync(team.Admin);
        return await ReadAsync<ApprovalDefinitionDto>(await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Fırsat onayı {Guid.NewGuid():N}"[..24], null, "TRY", [new ApprovalStepDefinitionDto("Satış müdürü", 1, team.Manager.Id, null, null, null)],
            null, Opportunity)), HttpStatusCode.Created);
    }

    private static async Task<OpportunityDto> CreateOpportunityAsync(HttpClient client, decimal amount)
    {
        CompanyDto company = await ReadAsync<CompanyDto>(await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest($"Müşteri {Guid.NewGuid():N}"[..20])), HttpStatusCode.Created);
        return await ReadAsync<OpportunityDto>(await client.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Depo projesi", company.Id, Amount: amount)), HttpStatusCode.Created);
    }

    private static async Task<OpportunityDto> UpdateAsync(HttpClient client, OpportunityDto opportunity, string title, decimal amount) =>
        await ReadAsync<OpportunityDto>(await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest(title, opportunity.CompanyId, Amount: amount, Version: opportunity.Version)), HttpStatusCode.OK);

    // Paylaşımlı yerel veritabanında başka testlerin teslim kuyruğu birikebilir; dağıtıcı turları durum değişene kadar sürer.
    private async Task<ApprovalRequestDto> WaitForStatusAsync(HttpClient client, Guid tenantId, Guid requestId, string status)
    {
        ApprovalRequestDto current = await GetAsync<ApprovalRequestDto>(client, $"/api/approvals/requests/{requestId}");
        for (int i = 0; i < 10 && current.Status != status; i++)
        {
            await DrainAsync(tenantId);
            current = await GetAsync<ApprovalRequestDto>(client, $"/api/approvals/requests/{requestId}");
        }

        return current;
    }

    private async Task DrainAsync(Guid tenantId)
    {
        for (int i = 0; i < 20; i++)
        {
            using IServiceScope scope = factory.OpenScope(tenantId);
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round.Dispatched == 0 && round.Delivered == 0)
            {
                return;
            }
        }
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
