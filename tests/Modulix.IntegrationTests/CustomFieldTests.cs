using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y9a: özel alanlar. Tanımlar <c>core.customization.manage</c> ile yönetilir ve firmaya aittir; değerler kayıt boru
/// hattında doğrulanır, birleştirilir, kayıt geçmişinde alanın adıyla görünür; arşivlenen alanın değeri kaybolmaz.
/// </summary>
public class CustomFieldTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, OpportunitiesRead, OpportunitiesWrite];

    [Fact]
    public async Task Choice_field_is_required_validated_filterable_and_shown_in_history()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/customization/fields")).StatusCode);
        var entities = await adminClient.GetFromJsonAsync<List<CustomizableEntityDto>>("/api/customization/entities");
        Assert.Equal(["crm.company", "crm.contact", "crm.opportunity", "crm.activity", "crm.lead"], entities!.Select(e => e.Key));

        var field = await CreateFieldAsync(adminClient, new SaveCustomFieldRequest(
            "crm.opportunity", "Araç tipi", CustomFieldType.Choice, Required: true, ShowInList: true,
            Options: [new(null, "Tır"), new(null, "Kamyon")]));
        Assert.Equal("arac_tipi", field.Key);
        Assert.Equal(["tir", "kamyon"], field.Options.Select(o => o.Key));
        await CreateFieldAsync(adminClient, new SaveCustomFieldRequest("crm.opportunity", "Mesafe (km)", CustomFieldType.Number));

        // Her üye formlar için etkin alanları görür.
        var visible = await repClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields");
        Assert.Equal(["arac_tipi", "mesafe_km"], visible!.Select(f => f.Key));

        var company = await PostAsync<CompanyDto>(repClient, "/api/crm/companies", new SaveCompanyRequest("Nakliyat A.Ş."));
        var missing = await repClient.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest("Ankara hattı", company.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("custom.arac_tipi", await missing.Content.ReadAsStringAsync());
        var wrong = await repClient.PostAsJsonAsync("/api/crm/opportunities", new SaveOpportunityRequest(
            "Ankara hattı", company.Id, Custom: new JsonObject { ["arac_tipi"] = "gemi", ["mesafe_km"] = "uzak" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);

        var tir = await PostAsync<OpportunityDto>(repClient, "/api/crm/opportunities", new SaveOpportunityRequest(
            "Ankara hattı", company.Id, Custom: new JsonObject { ["arac_tipi"] = "tir", ["mesafe_km"] = 450 }));
        Assert.Equal("tir", tir.Custom!["arac_tipi"]!.GetValue<string>());
        await PostAsync<OpportunityDto>(repClient, "/api/crm/opportunities", new SaveOpportunityRequest(
            "İzmir hattı", company.Id, Custom: new JsonObject { ["arac_tipi"] = "kamyon" }));

        // Liste süzgeci veritabanında uygulanır.
        var filtered = await repClient.GetFromJsonAsync<CrmPage<OpportunityDto>>($"/api/crm/opportunities?companyId={company.Id}&custom.arac_tipi=tir");
        Assert.Equal(["Ankara hattı"], filtered!.Items.Select(o => o.Title));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repClient.GetAsync("/api/crm/opportunities?custom.mesafe_km=450")).StatusCode);

        // Güncelleme: verilmeyen özel alan korunur, değişiklik geçmişte alanın ve seçeneğin adıyla görünür.
        var moved = await repClient.PutAsJsonAsync($"/api/crm/opportunities/{tir.Id}", new SaveOpportunityRequest(
            tir.Title, company.Id, Version: tir.Version, Custom: new JsonObject { ["arac_tipi"] = "kamyon" }));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var updated = (await moved.Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal(450, updated.Custom!["mesafe_km"]!.GetValue<decimal>());
        var history = (await repClient.GetFromJsonAsync<AuditPageResponse>($"/api/records/crm.opportunity/{tir.Id}/history"))!;
        var change = history.Items[0].Changes.Single(c => c.Field == "custom.arac_tipi");
        Assert.Equal(("Araç tipi", "Tır", "Kamyon"), (change.Label, change.Old, change.New));
    }

    [Fact]
    public async Task Archived_field_keeps_values_and_removed_options_become_inactive()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        var field = await CreateFieldAsync(adminClient, new SaveCustomFieldRequest(
            "crm.company", "Bölge", CustomFieldType.Choice, Options: [new(null, "Marmara"), new(null, "Ege")]));
        var company = await PostAsync<CompanyDto>(repClient, "/api/crm/companies",
            new SaveCompanyRequest("Bölgesel A.Ş.", Custom: new JsonObject { ["bolge"] = "ege" }));

        // "Ege" listeden çıkarılınca pasif kalır: yeni kayıtta seçilemez, eski kayıtta korunur.
        var renamed = await adminClient.PutAsJsonAsync($"/api/customization/fields/{field.Id}", new SaveCustomFieldRequest(
            "crm.company", "Satış bölgesi", CustomFieldType.Choice, Options: [new("marmara", "Marmara Bölgesi"), new(null, "Akdeniz")]));
        var options = (await renamed.Content.ReadFromJsonAsync<CustomFieldDto>())!.Options;
        Assert.Equal(["marmara:True", "akdeniz:True", "ege:False"], options.Select(o => $"{o.Key}:{o.Active}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await repClient.PostAsJsonAsync("/api/crm/companies",
            new SaveCompanyRequest("Yeni", Custom: new JsonObject { ["bolge"] = "ege" }))).StatusCode);
        var kept = await repClient.PutAsJsonAsync($"/api/crm/companies/{company.Id}",
            new SaveCompanyRequest("Bölgesel A.Ş.", Version: company.Version, Custom: new JsonObject { ["bolge"] = "ege" }));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsync($"/api/customization/fields/{field.Id}/archive", null)).StatusCode);
        var archived = (await repClient.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Empty(archived.Custom!);
        Assert.Empty((await repClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsync($"/api/customization/fields/{field.Id}/restore", null)).StatusCode);
        var restored = (await repClient.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{company.Id}"))!;
        Assert.Equal("ege", restored.Custom!["bolge"]!.GetValue<string>());
    }

    [Fact]
    public async Task Definitions_are_validated_ordered_and_kept_per_tenant()
    {
        var (_, admin, _) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var first = await CreateFieldAsync(adminClient, new SaveCustomFieldRequest("crm.contact", "Doğum günü", CustomFieldType.Date));
        var second = await CreateFieldAsync(adminClient, new SaveCustomFieldRequest("crm.contact", "Bülten", CustomFieldType.YesNo));

        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync("/api/customization/fields",
            new SaveCustomFieldRequest("crm.contact", " doğum GÜNÜ ", CustomFieldType.Text))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync("/api/customization/fields",
            new SaveCustomFieldRequest("crm.contact", "Seçim", CustomFieldType.Choice))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync("/api/customization/fields",
            new SaveCustomFieldRequest("core.role", "Rol notu", CustomFieldType.Text))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.PutAsJsonAsync("/api/customization/fields/order",
            new ReorderCustomFieldsRequest("crm.contact", [second.Id, first.Id]))).StatusCode);
        var fields = await adminClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/customization/fields?entityKey=crm.contact");
        Assert.Equal(["bulten", "dogum_gunu"], fields!.Select(f => f.Key));

        // Başka firma bu tanımları görmez ve değiştiremez.
        var (_, otherAdmin, _) = await SetUpAsync();
        using var otherClient = await LoginAsync(otherAdmin);
        Assert.Empty((await otherClient.GetFromJsonAsync<List<CustomFieldDto>>("/api/customization/fields"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync($"/api/customization/fields/{first.Id}",
            new SaveCustomFieldRequest("crm.contact", "Ele geçir", CustomFieldType.Date))).StatusCode);
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Rep)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, SalesAccess, rep);
        return (tenant, admin, rep);
    }

    private static async Task<CustomFieldDto> CreateFieldAsync(HttpClient client, SaveCustomFieldRequest request) =>
        await PostAsync<CustomFieldDto>(client, "/api/customization/fields", request);

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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
