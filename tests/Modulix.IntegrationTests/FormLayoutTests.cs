using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0021: form düzeni (sekme / bölüm / alan yerleşimi, zorunluluk, görünürlük). Düzen firmaya aittir, yetkisiz kullanıcı
/// yazamaz, eski sürüm ezmez; firmanın zorunlu kıldığı alan sunucuda denetlenir; özel alanlar düzende yer alır.
/// </summary>
public class FormLayoutTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] SalesAccess = [CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite];

    private const string Company = "crm.company";

    [Fact]
    public async Task Layout_is_default_until_customized_then_persistent_versioned_and_tenant_scoped()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        using var rep = await LoginAsync(a.Rep);
        using var otherAdmin = await LoginAsync(b.Admin);

        var initial = await LayoutAsync(rep, Company);
        Assert.False(initial.Customized);
        Assert.Equal(0, initial.Version);
        Assert.Equal(["genel"], initial.Tabs.Select(t => t.Key));
        Assert.Contains(initial.Sections, s => s.Key == "temel");
        var name = initial.Fields.Single(f => f.Key == "name");
        Assert.True(name.Required && name.Fixed && name.Visible);
        Assert.False(initial.Fields.Single(f => f.Key == "industry").Required);

        // Yetkisiz kullanıcı düzeni değiştiremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PutAsJsonAsync(Url(Company), ToRequest(initial))).StatusCode);

        // Yeni sekme + bölüm, alan taşıma, genişlik, zorunluluk ve gizleme.
        var request = new SaveFormLayoutRequest(
            [.. initial.Tabs, new FormTabDto("finans", "Finans")],
            [.. initial.Sections, new FormSectionDto("finans-bilgi", "Finansal bilgiler", "coins", "finans")],
            [.. initial.Fields.Select(f => f.Key switch
            {
                "taxNumber" => new SaveFormFieldRequest(f.Key, "finans-bilgi", 4, f.Required, true),
                "industry" => new SaveFormFieldRequest(f.Key, f.Section, f.Span, true, true),
                "tags" => new SaveFormFieldRequest(f.Key, f.Section, f.Span, f.Required, false),
                _ => new SaveFormFieldRequest(f.Key, f.Section, f.Span, f.Required, f.Visible),
            })],
            initial.Version);
        var saved = await PutAsync(admin, Company, request);
        Assert.True(saved.Customized);
        Assert.Equal(1, saved.Version);
        Assert.Equal(["genel", "finans"], saved.Tabs.Select(t => t.Key));
        Assert.Equal("finans", saved.Sections.Single(s => s.Key == "finans-bilgi").Tab);
        var taxNumber = saved.Fields.Single(f => f.Key == "taxNumber");
        Assert.Equal(("finans-bilgi", 4), (taxNumber.Section, taxNumber.Span));
        Assert.True(saved.Fields.Single(f => f.Key == "industry").Required);
        Assert.False(saved.Fields.Single(f => f.Key == "tags").Visible);

        // Denetim kaydı: kim, neyi zorunlu kıldı.
        var audit = (await admin.GetFromJsonAsync<Modulix.Shared.Audit.AuditPageResponse>("/api/audit?entityKey=core.form-layout"))!;
        Assert.Single(audit.Items);
        Assert.Equal(a.Admin.Id, audit.Items[0].UserId);
        Assert.Contains(audit.Items[0].Changes, c => c.Field == "required" && c.New == "Sektör");

        // Eski sürümle yazım sessizce ezmez; tanımsız form 404.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(Url(Company), request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(Url("crm.uydurma"), request with { Version = 0 })).StatusCode);

        // Yeni oturum düzeni veritabanından okur; başka firma etkilenmez.
        using var freshRep = await LoginAsync(a.Rep);
        Assert.True((await LayoutAsync(freshRep, Company)).Customized);
        Assert.False((await LayoutAsync(otherAdmin, Company)).Customized);

        // Varsayılana dön: satır kalır, sürüm ilerler, tanım geçerli olur.
        var reset = await admin.DeleteAsync($"{Url(Company)}?version=1");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var back = (await reset.Content.ReadFromJsonAsync<FormLayoutDto>())!;
        Assert.False(back.Customized);
        Assert.Equal(2, back.Version);
        Assert.Equal(["genel"], back.Tabs.Select(t => t.Key));
    }

    [Fact]
    public async Task Required_field_from_layout_is_enforced_by_the_server_on_create_and_update()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        using var rep = await LoginAsync(a.Rep);
        using var otherRep = await LoginAsync(b.Rep);

        var initial = await LayoutAsync(admin, Company);
        await PutAsync(admin, Company, ToRequest(initial, f => f.Key == "industry" ? new SaveFormFieldRequest(f.Key, f.Section, f.Span, true, true) : null));

        var missing = await rep.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Sektörsüz A.Ş."));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("industry", await missing.Content.ReadAsStringAsync());

        var created = await rep.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Sektörlü A.Ş.", Industry: "Lojistik"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<CompanyDto>())!;

        // Güncellemede zorunlu alan boşaltılamaz; dolu güncelleme çalışır.
        var cleared = await rep.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCompanyRequest(company.Name, Version: company.Version, Industry: null, OwnerId: a.Rep.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cleared.StatusCode);
        Assert.Contains("industry", await cleared.Content.ReadAsStringAsync());
        var kept = await rep.PutAsJsonAsync($"/api/crm/companies/{company.Id}", new SaveCompanyRequest(company.Name, Version: company.Version, Industry: "Taşımacılık", OwnerId: a.Rep.Id));
        Assert.True(kept.StatusCode == HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());

        // Başka firmanın düzeni etkilenmez: sektör orada zorunlu değildir.
        Assert.Equal(HttpStatusCode.Created, (await otherRep.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Serbest A.Ş."))).StatusCode);
    }

    [Fact]
    public async Task Invalid_layouts_are_rejected_with_field_errors()
    {
        var a = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        var initial = await LayoutAsync(admin, Company);

        async Task ExpectInvalidAsync(SaveFormLayoutRequest request, string expected)
        {
            var response = await admin.PutAsJsonAsync(Url(Company), request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        }

        await ExpectInvalidAsync(ToRequest(initial) with { Fields = [.. ToRequest(initial).Fields, new SaveFormFieldRequest("uydurma", "temel", 6, false, true)] }, "Bilinmeyen alan");
        await ExpectInvalidAsync(ToRequest(initial) with { Fields = [.. ToRequest(initial).Fields.Where(f => f.Key != "city")] }, "eksik");
        await ExpectInvalidAsync(ToRequest(initial, f => f.Key == "city" ? new SaveFormFieldRequest(f.Key, f.Section, 5, false, true) : null), "span");
        await ExpectInvalidAsync(ToRequest(initial, f => f.Key == "name" ? new SaveFormFieldRequest(f.Key, f.Section, f.Span, true, false) : null), "Sabit alan");
        await ExpectInvalidAsync(ToRequest(initial, f => f.Key == "name" ? new SaveFormFieldRequest(f.Key, f.Section, f.Span, false, true) : null), "Sabit alan");
        await ExpectInvalidAsync(ToRequest(initial) with { Sections = [.. initial.Sections, new FormSectionDto("yeni", "Yeni", null, "olmayan-sekme")] }, "sekme");
        await ExpectInvalidAsync(ToRequest(initial) with { Tabs = [] }, "sekme");

        // Hiçbiri kaydedilmedi.
        Assert.False((await LayoutAsync(admin, Company)).Customized);
    }

    [Fact]
    public async Task Custom_fields_take_part_in_the_layout_and_can_be_made_required()
    {
        var a = await SetUpAsync();
        using var admin = await LoginAsync(a.Admin);
        using var rep = await LoginAsync(a.Rep);

        var createdField = await admin.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(Company, "Araç tipi", CustomFieldType.Text));
        Assert.Equal(HttpStatusCode.Created, createdField.StatusCode);
        var field = (await createdField.Content.ReadFromJsonAsync<CustomFieldDto>())!;
        var layoutKey = $"custom.{field.Key}";

        var layout = await LayoutAsync(admin, Company);
        var placed = layout.Fields.Single(f => f.Key == layoutKey);
        Assert.True(placed.IsCustom);
        Assert.Equal("ek", placed.Section);

        var saved = await PutAsync(admin, Company, ToRequest(layout, f => f.Key == layoutKey ? new SaveFormFieldRequest(f.Key, "temel", 4, true, true) : null));
        var moved = saved.Fields.Single(f => f.Key == layoutKey);
        Assert.Equal(("temel", 4, true), (moved.Section, moved.Span, moved.Required));

        var missing = await rep.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Araçsız A.Ş."));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains(layoutKey, await missing.Content.ReadAsStringAsync());

        var custom = new System.Text.Json.Nodes.JsonObject { [field.Key] = "Tır" };
        var ok = await rep.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Araçlı A.Ş.", Custom: custom));
        Assert.True(ok.StatusCode == HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
    }

    private static string Url(string entityKey) => $"/api/customization/form-layouts/{entityKey}";

    private static SaveFormLayoutRequest ToRequest(FormLayoutDto layout, Func<FormFieldDto, SaveFormFieldRequest?>? map = null) => new(
        layout.Tabs,
        layout.Sections,
        [.. layout.Fields.Select(f => map?.Invoke(f) ?? new SaveFormFieldRequest(f.Key, f.Section, f.Span, f.Required, f.Visible))],
        layout.Version);

    private static async Task<FormLayoutDto> LayoutAsync(HttpClient client, string entityKey) =>
        (await client.GetFromJsonAsync<List<FormLayoutDto>>("/api/form-layouts"))!.Single(l => l.EntityKey == entityKey);

    private static async Task<FormLayoutDto> PutAsync(HttpClient client, string entityKey, SaveFormLayoutRequest request)
    {
        var response = await client.PutAsJsonAsync(Url(entityKey), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FormLayoutDto>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep)> SetUpAsync()
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

        await factory.CreateRoleAsync(tenant.Id, SalesAccess, Scope.Own, rep);
        return (admin, rep);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new Modulix.Shared.Auth.LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
