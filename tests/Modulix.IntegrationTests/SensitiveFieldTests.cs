using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y8b: hassas alanlar. Fırsat tutarı ve olasılığı "crm.opportunity-amounts.read" olmadan gönderilmez, yazılamaz,
/// süzülemez; ekranın boş gönderdiği değer kayıttaki değeri silmez; denetim kaydında ve kayıt geçmişinde gizlidir.
/// </summary>
public class SensitiveFieldTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite, ReadAudit];

    [Fact]
    public async Task Amount_is_hidden_kept_and_not_writable_without_permission()
    {
        var (_, admin, clerk) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var clerkClient = await LoginAsync(clerk);

        var company = await PostAsync<CompanyDto>(adminClient, "/api/crm/companies", new SaveCompanyRequest("Gizli A.Ş."));
        var opportunity = await PostAsync<OpportunityDto>(adminClient, "/api/crm/opportunities",
            new SaveOpportunityRequest("Büyük iş", company.Id, Amount: 250_000m, Currency: "EUR", Probability: 60));

        // Okuma: tutar ve olasılık gelmez; para birimi gelir.
        var read = (await clerkClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        Assert.Null(read.Amount);
        Assert.Null(read.Probability);
        Assert.Equal("EUR", read.Currency);
        var raw = (await clerkClient.GetFromJsonAsync<JsonObject>($"/api/records/crm.opportunity/{opportunity.Id}"))!;
        Assert.DoesNotContain("250000", raw.ToJsonString());
        var list = (await clerkClient.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?companyId={company.Id}"))!;
        Assert.Null(Assert.Single(list.Items).Amount);

        // Ekranın boş gönderdiği tutar kayıttaki değeri silmez.
        var renamed = await clerkClient.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest("Büyük iş (2026)", company.Id, Currency: "EUR", Version: read.Version));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var afterRename = (await adminClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!;
        Assert.Equal("Büyük iş (2026)", afterRename.Title);
        Assert.Equal(250_000m, afterRename.Amount);
        Assert.Equal(60, afterRename.Probability);

        // Yazma: tutar veya olasılık verilemez.
        var write = await clerkClient.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest("Büyük iş", company.Id, Amount: 1m, Version: afterRename.Version));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var create = await clerkClient.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Yeni", company.Id, Probability: 10));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await clerkClient.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Tutarsız", company.Id))).StatusCode);

        // Süzme: hassas alana göre süzmek reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clerkClient.GetAsync("/api/records/crm.opportunity?amount=1")).StatusCode);

        Assert.Equal(250_000m, (await adminClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!.Amount);
    }

    [Fact]
    public async Task Audit_log_and_history_hide_sensitive_values_from_viewers_without_permission()
    {
        var (_, admin, clerk) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var clerkClient = await LoginAsync(clerk);
        var company = await PostAsync<CompanyDto>(adminClient, "/api/crm/companies", new SaveCompanyRequest("Denetim A.Ş."));
        var opportunity = await PostAsync<OpportunityDto>(adminClient, "/api/crm/opportunities",
            new SaveOpportunityRequest("Denetlenen", company.Id, Amount: 99_000m, Probability: 30));

        var history = (await clerkClient.GetFromJsonAsync<AuditPageResponse>($"/api/records/crm.opportunity/{opportunity.Id}/history"))!;
        var created = Assert.Single(history.Items);
        var amount = created.Changes.Single(c => c.Field == "amount");
        Assert.True(amount.Hidden);
        Assert.Null(amount.New);
        Assert.False(created.Changes.Single(c => c.Field == "title").Hidden);
        Assert.Equal("Denetlenen", created.Changes.Single(c => c.Field == "title").New);

        var log = (await clerkClient.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=crm.opportunity"))!;
        Assert.All(log.Items.SelectMany(i => i.Changes).Where(c => c.Field is "amount" or "probability"), c => Assert.True(c.Hidden));
        Assert.DoesNotContain("99000", await clerkClient.GetStringAsync($"/api/records/crm.opportunity/{opportunity.Id}/history"));

        var adminHistory = (await adminClient.GetFromJsonAsync<AuditPageResponse>($"/api/records/crm.opportunity/{opportunity.Id}/history"))!;
        var adminAmount = Assert.Single(adminHistory.Items).Changes.Single(c => c.Field == "amount");
        Assert.False(adminAmount.Hidden);
        Assert.NotNull(adminAmount.New);
    }

    [Fact]
    public async Task Permission_shows_amounts_and_is_in_the_sales_template()
    {
        var (tenant, admin, _) = await SetUpAsync();
        var viewer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead], viewer);
        using var adminClient = await LoginAsync(admin);
        using var viewerClient = await LoginAsync(viewer);
        var company = await PostAsync<CompanyDto>(adminClient, "/api/crm/companies", new SaveCompanyRequest("Görünür A.Ş."));
        var opportunity = await PostAsync<OpportunityDto>(adminClient, "/api/crm/opportunities",
            new SaveOpportunityRequest("Görünür", company.Id, Amount: 5_000m));

        Assert.Equal(5_000m, (await viewerClient.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{opportunity.Id}"))!.Amount);

        var templates = (await adminClient.GetFromJsonAsync<List<Modulix.Shared.Access.RoleTemplateDto>>("/api/roles/templates"))!;
        Assert.Contains(OpportunityAmountsRead, templates.Single(t => t.Key == "crm.sales-rep").Permissions);
        Assert.Contains(OpportunityAmountsRead, templates.Single(t => t.Key == "core.read-only").Permissions);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Clerk)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var clerk = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, SalesAccess, clerk);
        return (tenant, admin, clerk);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
