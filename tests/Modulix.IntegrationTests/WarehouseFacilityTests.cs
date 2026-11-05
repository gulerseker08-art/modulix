using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Customization;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 2b: tesis kaydı. Mutlu yol (oluşturma, okuma, güncelleme, süzme, silme, denetim kaydı), doğrulama (zorunlu alan,
/// kod biçimi ve benzersizliği, ülke kodu, sürüm), tenant izolasyonu (A'nın tesisi B'ye görünmez; kod tenant başına benzersizdir)
/// ve yetki (modül kapalı, izinsiz, yalnızca okuyan).
/// </summary>
public class WarehouseFacilityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Facilities = "/api/records/warehouse.facility";

    [Fact]
    public async Task Facility_is_created_listed_updated_deleted_and_every_change_is_audited()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var created = await PostAsync(admin, new JsonObject { ["code"] = "ant", ["name"] = "Mersin Antrepo", ["countryCode"] = "tr" });
        Assert.Equal("ANT", created.Values["code"]!.GetValue<string>());
        Assert.Equal("TR", created.Values["countryCode"]!.GetValue<string>());
        Assert.True(created.Values["isActive"]!.GetValue<bool>());
        Assert.False(created.Values["customsEnabled"]!.GetValue<bool>());

        var other = await PostAsync(admin, new JsonObject { ["code"] = "DEP", ["name"] = "Genel Depo", ["isActive"] = false });

        var active = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Facilities}?isActive=true"))!;
        Assert.Equal([created.Id], active.Items.Select(i => i.Id));
        var searched = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Facilities}?search=genel"))!;
        Assert.Equal([other.Id], searched.Items.Select(i => i.Id));

        var update = await admin.PutAsJsonAsync($"{Facilities}/{created.Id}", new RecordWriteRequest(
            new JsonObject { ["name"] = "Mersin Gümrüklü Antrepo", ["customsEnabled"] = true }, created.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.True(updated.Version > created.Version);
        Assert.True(updated.Values["customsEnabled"]!.GetValue<bool>());
        Assert.Equal("ANT", updated.Values["code"]!.GetValue<string>());

        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Facilities}/{created.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Created && h.UserId == s.Admin.Id);
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "customsEnabled"));

        var delete = await admin.DeleteAsync($"{Facilities}/{other.Id}?version={other.Version}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Facilities}/{other.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_facilities_are_rejected_with_field_errors_and_stale_versions_conflict()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Facilities, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("code"));
        Assert.True(missing.Errors.ContainsKey("name"));

        var badCode = await ValidationProblemAsync(await admin.PostAsJsonAsync(Facilities,
            new RecordWriteRequest(new JsonObject { ["code"] = "A-B", ["name"] = "Hatalı" })));
        Assert.Equal(["Kod yalnızca harf, rakam ve alt çizgi içerebilir."], badCode.Errors["code"]);

        var badCountry = await ValidationProblemAsync(await admin.PostAsJsonAsync(Facilities,
            new RecordWriteRequest(new JsonObject { ["code"] = "X1", ["name"] = "Hatalı", ["countryCode"] = "1A" })));
        Assert.Equal(["İki harfli ülke kodu girin (ör. TR)."], badCountry.Errors["countryCode"]);

        var unknown = await ValidationProblemAsync(await admin.PostAsJsonAsync(Facilities,
            new RecordWriteRequest(new JsonObject { ["code"] = "X2", ["name"] = "Hatalı", ["bilinmeyen"] = 1 })));
        Assert.True(unknown.Errors.ContainsKey("bilinmeyen"));

        var first = await PostAsync(admin, new JsonObject { ["code"] = "TKR", ["name"] = "İlk" });
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Facilities,
            new RecordWriteRequest(new JsonObject { ["code"] = "tkr", ["name"] = "Çift" })));
        Assert.Equal(["Bu kod başka bir tesiste kullanılıyor."], duplicate.Errors["code"]);

        // Kendi kodunu koruyarak güncelleme çakışma sayılmaz; eski sürümle güncelleme 409, sürümsüz 428 alır.
        var renamed = await admin.PutAsJsonAsync($"{Facilities}/{first.Id}", new RecordWriteRequest(new JsonObject { ["code"] = "TKR", ["name"] = "Yeni ad" }, first.Version));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var stale = await admin.PutAsJsonAsync($"{Facilities}/{first.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Eski" }, first.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var noVersion = await admin.PutAsJsonAsync($"{Facilities}/{first.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Sürümsüz" }));
        Assert.Equal(HttpStatusCode.PreconditionRequired, noVersion.StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_change_each_others_facilities_and_codes_are_unique_per_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var facility = await PostAsync(adminA, new JsonObject { ["code"] = "ORT", ["name"] = "A'nın tesisi" });

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Facilities}/{facility.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Facilities}/{facility.Id}/history")).StatusCode);
        var listB = (await adminB.GetFromJsonAsync<RecordPageResponse>(Facilities))!;
        Assert.DoesNotContain(listB.Items, i => i.Id == facility.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminB.PutAsJsonAsync($"{Facilities}/{facility.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Sızıntı" }, facility.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}")).StatusCode);

        // Aynı kod başka tenant'ta serbesttir (UNIQUE(TenantId, Code)); A'nın kaydı etkilenmez.
        var sameCode = await PostAsync(adminB, new JsonObject { ["code"] = "ORT", ["name"] = "B'nin tesisi" });
        Assert.NotEqual(facility.Id, sameCode.Id);
        var untouched = (await adminA.GetFromJsonAsync<RecordResponse>($"{Facilities}/{facility.Id}"))!;
        Assert.Equal("A'nın tesisi", untouched.Values["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Facilities_need_the_module_and_the_matching_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var outsider = await LoginAsync(s.Outsider);
        var facility = await PostAsync(admin, new JsonObject { ["code"] = "YET", ["name"] = "Yetki denemesi" });

        // Rolü olmayan hiçbir şey yapamaz; okuyan yazamaz ve silemez; yazan oluşturabilir.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Facilities)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Facilities}/{facility.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Facilities}/{facility.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync(Facilities, new RecordWriteRequest(new JsonObject { ["code"] = "RDR", ["name"] = "Okuyucu" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PutAsJsonAsync($"{Facilities}/{facility.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Okuyucu" }, facility.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}")).StatusCode);
        var written = await writer.PostAsJsonAsync(Facilities, new RecordWriteRequest(new JsonObject { ["code"] = "WRT", ["name"] = "Yazıcı" }));
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        // Modülü kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Facilities)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await closedClient.PostAsJsonAsync(Facilities, new RecordWriteRequest(new JsonObject { ["code"] = "KPL", ["name"] = "Kapalı" }))).StatusCode);
    }

    [Fact]
    public async Task Facility_form_layout_is_served_to_the_tenant_with_tabs_and_the_identity_fields_on_the_first_tab()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var layouts = (await admin.GetFromJsonAsync<List<FormLayoutDto>>("/api/form-layouts"))!;
        var layout = Assert.Single(layouts, l => l.EntityKey == "warehouse.facility");

        Assert.True(layout.Tabs.Count >= 2);
        var firstTabSections = layout.Sections.Where(section => section.Tab == layout.Tabs[0].Key).Select(section => section.Key).ToHashSet();
        foreach (var key in new[] { "code", "name" })
        {
            var field = Assert.Single(layout.Fields, f => f.Key == key);
            Assert.True(field.Required && field.Visible);
            Assert.Contains(field.Section, firstTabSections);
        }
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser Outsider)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead, FacilitiesWrite], Scope.All, writer);
        return (admin, reader, writer, outsider);
    }

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(Facilities, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
