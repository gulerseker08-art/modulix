using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// CRM modülü: firma, kişi, fırsat, aktivite. Uç noktalar kayıt boru hattından geçer; modül tenant için
/// kapalıysa veya izin yoksa 403, başka tenant'ın kaydı 404, başka tenant'ın kaydına bağlanmak doğrulama hatasıdır.
/// </summary>
public class CrmTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] AllCrmPermissions =
    [
        CrmModule.Permissions.CompaniesRead, CrmModule.Permissions.CompaniesWrite,
        CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
        CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite, CrmModule.Permissions.OpportunityAmountsRead,
        CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite,
    ];

    [Fact]
    public async Task Company_crud_goes_through_the_pipeline_with_audit()
    {
        var (client, tenantId, userId) = await CrmClientAsync();
        using var _ = client;

        var create = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("  Acme A.Ş. ", City: "İstanbul", Email: "info@acme.test"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal("Acme A.Ş.", created.Name);
        Assert.Equal("İstanbul", created.City);
        Assert.Equal(1, created.Version);

        var update = await client.PutAsJsonAsync($"/api/crm/companies/{created.Id}",
            new SaveCompanyRequest("Acme A.Ş.", City: "Ankara", Version: created.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal("Ankara", updated.City);
        Assert.Null(updated.Email);
        Assert.Equal(2, updated.Version);

        var list = (await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=Acme"))!;
        Assert.Equal(created.Id, Assert.Single(list.Items).Id);
        Assert.Empty((await client.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies?search=yok-boyle-firma"))!.Items);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/crm/companies/{created.Id}?version=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/crm/companies/{created.Id}")).StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.Where(a => a.RecordId == created.Id).OrderBy(a => a.OccurredAt).ThenBy(a => a.Action).ToListAsync();
        Assert.Equal([AuditAction.Created, AuditAction.Updated, AuditAction.Deleted], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal(CrmModule.Entities.Company, a.EntityKey));
        Assert.All(audit, a => Assert.Equal(userId, a.UserId));

        var changes = JsonNode.Parse(audit[1].Changes)!.AsObject();
        Assert.Equal("İstanbul", changes["city"]!["old"]!.GetValue<string>());
        Assert.Equal("Ankara", changes["city"]!["new"]!.GetValue<string>());
        Assert.False(changes.ContainsKey("name"));
    }

    [Fact]
    public async Task Contacts_opportunities_and_activities_link_to_a_company()
    {
        var (client, _, _) = await CrmClientAsync();
        using var _ = client;
        var company = await CreateCompanyAsync(client, "Beta Ltd.");

        var contact = await PostAsync<ContactDto>(client, "/api/crm/contacts",
            new SaveContactRequest("Ayşe", "Yılmaz", company.Id, Title: "Satın alma"));
        var opportunity = await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest("Yıllık bakım", company.Id, contact.Id, OpportunityStage.Proposal, 125_000.50m, "try", new DateOnly(2026, 12, 31)));
        var due = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(3));
        var activity = await PostAsync<ActivityDto>(client, "/api/crm/activities",
            new SaveActivityRequest("Teklif görüşmesi", ActivityType.Meeting, due, CompanyId: company.Id, OpportunityId: opportunity.Id));

        Assert.Equal(company.Id, contact.CompanyId);
        Assert.Equal(OpportunityStage.Proposal, opportunity.Stage);
        Assert.Equal(125_000.50m, opportunity.Amount);
        Assert.Equal("TRY", opportunity.Currency);
        Assert.Equal(new DateOnly(2026, 12, 31), opportunity.ExpectedCloseDate);
        Assert.Equal(due, activity.DueAt);
        Assert.Equal(ActivityType.Meeting, activity.Type);

        var won = await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest("Yıllık bakım", company.Id, contact.Id, OpportunityStage.Won, 130_000m, Version: opportunity.Version));
        Assert.Equal(OpportunityStage.Won, (await won.Content.ReadFromJsonAsync<OpportunityDto>())!.Stage);

        // Bağlı kayıtları olan firma silinemez.
        var delete = await client.DeleteAsync($"/api/crm/companies/{company.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/crm/companies/{company.Id}")).StatusCode);
    }

    [Fact]
    public async Task Other_tenant_records_are_not_found_and_cannot_be_linked()
    {
        var (a, _, _) = await CrmClientAsync();
        var (b, _, _) = await CrmClientAsync();
        using var _a = a;
        using var _b = b;
        var companyOfA = await CreateCompanyAsync(a, "A Firması");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/crm/companies/{companyOfA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/crm/companies/{companyOfA.Id}", new SaveCompanyRequest("Ele geçirildi"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/crm/companies/{companyOfA.Id}")).StatusCode);
        Assert.DoesNotContain((await b.GetFromJsonAsync<CrmPage<CompanyDto>>("/api/crm/companies"))!.Items, c => c.Id == companyOfA.Id);

        var link = await b.PostAsJsonAsync("/api/crm/contacts", new SaveContactRequest("Mehmet", "Kaya", companyOfA.Id));
        var problem = await ValidationProblemAsync(link);
        Assert.Equal(["Firma bulunamadı."], problem.Errors["companyId"]);
    }

    [Fact]
    public async Task Invalid_values_return_field_errors()
    {
        var (client, _, _) = await CrmClientAsync();
        using var _ = client;

        var problem = await ValidationProblemAsync(await client.PostAsJsonAsync("/api/crm/companies",
            new SaveCompanyRequest(" ", Email: "gecersiz")));
        Assert.Contains("name", problem.Errors.Keys);
        Assert.Contains("email", problem.Errors.Keys);

        var generic = await ValidationProblemAsync(await client.PostAsJsonAsync($"/api/records/{CrmModule.Entities.Opportunity}",
            new RecordWriteRequest(new JsonObject { ["title"] = "x", ["stage"] = "Bilinmiyor", ["amount"] = -5, ["extra"] = 1 })));
        Assert.Equal(new[] { "amount", "companyId", "extra", "stage" }, generic.Errors.Keys.Order());
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        var (client, _, _) = await CrmClientAsync();
        using var _ = client;
        var company = await CreateCompanyAsync(client, "Gamma");
        await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCompanyRequest("Gamma 2", Version: 1));

        var stale = await client.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCompanyRequest("Gamma 3", Version: 1));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Disabled_module_is_forbidden_on_crm_and_generic_endpoints()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, AllCrmPermissions, user);
        using var client = await LoginAsync(user.Email!);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/records/{CrmModule.Entities.Company}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/records/{CrmModule.Entities.Company}",
            new RecordWriteRequest(new JsonObject { ["name"] = "Yasak" }))).StatusCode);

        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/crm/companies")).StatusCode);
    }

    [Fact]
    public async Task Read_only_role_can_list_but_not_write()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CrmModule.Permissions.CompaniesRead], user);
        using var client = await LoginAsync(user.Email!);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/crm/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/crm/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Yasak"))).StatusCode);

        // Genel kayıt uç noktası da aynı izni ister (boru hattının yetki adımı).
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/records/{CrmModule.Entities.Company}",
            new RecordWriteRequest(new JsonObject { ["name"] = "Yasak" }))).StatusCode);
    }

    [Fact]
    public async Task Owner_defaults_to_the_creator_and_must_be_a_member_of_the_tenant()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var ayse = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(await factory.CreateTenantAsync());
        await factory.CreateRoleAsync(tenant.Id, AllCrmPermissions, ayse);
        using var client = await LoginAsync(ayse.Email!);

        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest(
            "Delta A.Ş.", Address: "Atatürk Cad. 1", Industry: "Gıda", Status: CompanyStatus.Customer,
            Source: CompanySource.Referral, Tags: "öncelikli, ihracat"));
        Assert.Equal(ayse.Id, company.OwnerId);
        Assert.Equal(CompanyStatus.Customer, company.Status);
        Assert.Equal(CompanySource.Referral, company.Source);
        Assert.Equal("öncelikli, ihracat", company.Tags);

        var opportunity = await PostAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest(
            "Depo projesi", company.Id, Probability: 40, OwnerId: colleague.Id));
        Assert.Equal(colleague.Id, opportunity.OwnerId);
        Assert.Equal(40, opportunity.Probability);

        var members = (await client.GetFromJsonAsync<List<TenantMemberResponse>>("/api/tenant/members"))!;
        Assert.Equal(new[] { ayse.Id, colleague.Id }.Order(), members.Select(m => m.UserId).Order());

        var problem = await ValidationProblemAsync(await client.PostAsJsonAsync("/api/crm/activities",
            new SaveActivityRequest("Ara", OwnerId: outsider.Id)));
        Assert.Equal(["Kullanıcı bu kuruluşun üyesi değil."], problem.Errors["ownerId"]);

        await factory.DisableMembershipAsync(colleague.Id, tenant.Id);
        problem = await ValidationProblemAsync(await client.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}",
            new SaveOpportunityRequest("Depo projesi", company.Id, OwnerId: colleague.Id, Version: opportunity.Version)));
        Assert.Contains("ownerId", problem.Errors.Keys);
    }

    [Fact]
    public async Task Lists_filter_by_company_owner_stage_and_due_time()
    {
        var (client, _, userId) = await CrmClientAsync();
        using var _ = client;
        var company = await CreateCompanyAsync(client, "Filtre A.Ş.");
        var other = await CreateCompanyAsync(client, "Başka A.Ş.");

        var contact = await PostAsync<ContactDto>(client, "/api/crm/contacts", new SaveContactRequest("Ali", "Can", company.Id));
        await PostAsync<ContactDto>(client, "/api/crm/contacts", new SaveContactRequest("Veli", "Can", other.Id));
        var open = await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest("Açık", company.Id, Stage: OpportunityStage.Proposal));
        await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest("Kazanılan", company.Id, Stage: OpportunityStage.Won));
        await PostAsync<OpportunityDto>(client, "/api/crm/opportunities", new SaveOpportunityRequest("Başka", other.Id));

        var now = DateTimeOffset.UtcNow;
        var overdue = await PostAsync<ActivityDto>(client, "/api/crm/activities",
            new SaveActivityRequest("Geciken", DueAt: now.AddDays(-1), CompanyId: company.Id));
        await PostAsync<ActivityDto>(client, "/api/crm/activities",
            new SaveActivityRequest("Yarın", DueAt: now.AddDays(1), CompanyId: company.Id));
        await PostAsync<ActivityDto>(client, "/api/crm/activities",
            new SaveActivityRequest("Bitti", DueAt: now.AddDays(-2), CompletedAt: now, CompanyId: company.Id));

        Assert.Equal([contact.Id], (await ListAsync<ContactDto>(client, $"contacts?companyId={company.Id}")).Select(c => c.Id));
        Assert.Equal(["Açık", "Kazanılan"], (await ListAsync<OpportunityDto>(client, $"opportunities?companyId={company.Id}")).Select(o => o.Title).Order());
        Assert.Equal([open.Id], (await ListAsync<OpportunityDto>(client, $"opportunities?companyId={company.Id}&open=true")).Select(o => o.Id));
        Assert.Equal(["Kazanılan"], (await ListAsync<OpportunityDto>(client, $"opportunities?companyId={company.Id}&stage=won")).Select(o => o.Title));

        var dueBefore = Uri.EscapeDataString(now.ToString("O"));
        var mine = await ListAsync<ActivityDto>(client, $"activities?ownerId=me&open=true&dueBefore={dueBefore}");
        Assert.Equal([overdue.Id], mine.Select(a => a.Id));
        Assert.All(mine, a => Assert.Equal(userId, a.OwnerId));
        Assert.Equal(3, (await ListAsync<ActivityDto>(client, $"activities?companyId={company.Id}")).Count);

        var unknown = await ValidationProblemAsync(await client.GetAsync("/api/crm/companies?renk=mavi"));
        Assert.Equal(["Bilinmeyen filtre."], unknown.Errors["renk"]);
        var invalid = await ValidationProblemAsync(await client.GetAsync("/api/crm/opportunities?stage=kazanildi&companyId=x"));
        Assert.Equal(new[] { "companyId", "stage" }, invalid.Errors.Keys.Order());
    }

    [Fact]
    public void Shared_access_keys_match_the_module()
    {
        // Arayüz menüsü bu anahtarlara bakar; modüldeki bir değişiklik arayüzü sessizce bozmasın.
        Assert.Equal(CrmModule.ModuleKey, CrmAccess.ModuleKey);
        Assert.Equal(
            ((IModule)new CrmModule()).Permissions.Select(p => p.Key).Order(),
            typeof(CrmAccess).GetFields().Where(f => f.Name != nameof(CrmAccess.ModuleKey)).Select(f => (string)f.GetValue(null)!).Order());
        Assert.Contains(((IModule)new CrmModule()).AuditedEntities, e => e.EntityKey == CrmEntityKeys.Company);
    }

    [Fact]
    public async Task Unknown_api_route_is_not_found()
    {
        using var client = factory.CreateBrowserClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/crm/yok")).StatusCode);
    }

    private async Task<(HttpClient Client, Guid TenantId, Guid UserId)> CrmClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, AllCrmPermissions, user);
        return (await LoginAsync(user.Email!), tenant.Id, user.Id);
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name) =>
        PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest(name));

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object request)
    {
        var response = await client.PostAsJsonAsync(url, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<IReadOnlyList<T>> ListAsync<T>(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<CrmPage<T>>($"/api/crm/{path}"))!.Items;

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
