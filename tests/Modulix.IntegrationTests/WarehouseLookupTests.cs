using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Lookups;
using Modulix.Shared.Records;
using Modulix.Shared.Warehouse;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 2c-2: depo seçim kutuları (ADR-0016). Mutlu yol (tanımlar listelenir, tesis/alan/üst lokasyon aranır, alan ve üst
/// lokasyon tesise bağlıdır), tenant izolasyonu ve yetki (hedef kaydı okuma izni olmayan kişiye sonuç sızmaz). Doğrulama:
/// başka tesisin alanı ya da lokasyonu kayıt sırasında zaten reddedilir (WarehouseLocationTests).
/// </summary>
public class WarehouseLookupTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Warehouse_lookups_are_listed_and_search_tenant_data_with_zone_and_parent_bound_to_the_facility()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var lookups = (await admin.GetFromJsonAsync<List<LookupDto>>("/api/lookups"))!;
        foreach (var key in new[]
        {
            WarehouseAccess.ZoneFacilityLookup, WarehouseAccess.LocationLevelFacilityLookup, WarehouseAccess.LocationFacilityLookup,
            WarehouseAccess.LocationParentLookup, WarehouseAccess.LocationZoneLookup,
        })
        {
            Assert.Equal(WarehouseAccess.ModuleKey, Assert.Single(lookups, l => l.Key == key).ModuleKey);
        }

        var ant = await PostAsync(admin, WarehouseAccess.FacilityEntityKey, new JsonObject { ["code"] = "ant", ["name"] = "Mersin Antrepo" });
        var dep = await PostAsync(admin, WarehouseAccess.FacilityEntityKey, new JsonObject { ["code"] = "dep", ["name"] = "Genel Depo" });
        var antZone = await PostAsync(admin, WarehouseAccess.ZoneEntityKey, new JsonObject { ["facilityId"] = ant.Id.ToString(), ["code"] = "z1", ["name"] = "Ant alanı" });
        await PostAsync(admin, WarehouseAccess.ZoneEntityKey, new JsonObject { ["facilityId"] = dep.Id.ToString(), ["code"] = "z2", ["name"] = "Dep alanı" });
        foreach (var facility in new[] { ant, dep })
        {
            await PostAsync(admin, WarehouseAccess.LocationLevelEntityKey, new JsonObject
            {
                ["facilityId"] = facility.Id.ToString(), ["position"] = 1, ["key"] = "root", ["name"] = "Kök", ["characterSet"] = "letters", ["minLength"] = 2, ["maxLength"] = 5,
            });
        }

        var antRoot = await PostAsync(admin, WarehouseAccess.LocationEntityKey, new JsonObject { ["facilityId"] = ant.Id.ToString(), ["value"] = "aa" });
        await PostAsync(admin, WarehouseAccess.LocationEntityKey, new JsonObject { ["facilityId"] = dep.Id.ToString(), ["value"] = "bb" });

        // Tesis: kod ve adla aranır; etiket kod + ad.
        var facilities = await SearchAsync(admin, WarehouseAccess.LocationFacilityLookup, "antrepo");
        var found = Assert.Single(facilities.Items);
        Assert.Equal((ant.Id, "ANT Mersin Antrepo"), (found.Id, found.Label));

        // Alan ve üst lokasyon seçilen tesise bağlıdır; tesis verilmezse kullanıcının görebildiği hepsi gelir.
        Assert.Equal([antZone.Id], (await SearchAsync(admin, WarehouseAccess.LocationZoneLookup, null, ant.Id)).Items.Select(i => i.Id));
        Assert.Equal(2, (await SearchAsync(admin, WarehouseAccess.LocationZoneLookup, null)).Items.Count);
        var parents = await SearchAsync(admin, WarehouseAccess.LocationParentLookup, null, ant.Id);
        var parent = Assert.Single(parents.Items);
        Assert.Equal((antRoot.Id, "AA"), (parent.Id, parent.Label));

        var resolved = (await (await admin.PostAsJsonAsync($"/api/lookups/{WarehouseAccess.LocationParentLookup}/resolve", new ResolveLookupRequest([antRoot.Id])))
            .Content.ReadFromJsonAsync<LookupResolveDto>())!;
        Assert.Equal("AA", Assert.Single(resolved.Items).Label);
    }

    [Fact]
    public async Task Lookups_never_return_or_resolve_another_tenants_facilities()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var facilityA = await PostAsync(adminA, WarehouseAccess.FacilityEntityKey, new JsonObject { ["code"] = "gizli", ["name"] = "A'nın tesisi" });

        Assert.Empty((await SearchAsync(adminB, WarehouseAccess.ZoneFacilityLookup, "gizli")).Items);
        var resolved = (await (await adminB.PostAsJsonAsync($"/api/lookups/{WarehouseAccess.ZoneFacilityLookup}/resolve", new ResolveLookupRequest([facilityA.Id])))
            .Content.ReadFromJsonAsync<LookupResolveDto>())!;
        Assert.All(resolved.Items, item => Assert.True(item.Unavailable && item.Label is null));
    }

    [Fact]
    public async Task Facility_lookup_needs_permission_to_read_facilities()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var locationsOnly = await LoginAsync(s.LocationsOnly);
        await PostAsync(admin, WarehouseAccess.FacilityEntityKey, new JsonObject { ["code"] = "yet", ["name"] = "Yetkili tesis" });

        // Yalnızca lokasyon izni olan kişi tesis adlarını seçim kutusundan okuyamaz: erişim hedef kaydın izniyle belirlenir.
        var response = await locationsOnly.GetAsync($"/api/lookups/{WarehouseAccess.LocationFacilityLookup}/search?q=yet");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var page = (await response.Content.ReadFromJsonAsync<LookupPageDto>())!;
            Assert.DoesNotContain(page.Items, i => i.Label is { Length: > 0 });
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    private async Task<(ModulixUser Admin, ModulixUser LocationsOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var locationsOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [LocationsRead, LocationsWrite], Scope.All, locationsOnly);
        return (admin, locationsOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<LookupPageDto> SearchAsync(HttpClient client, string key, string? text, Guid? parentId = null)
    {
        var url = $"/api/lookups/{key}/search?page=1{(text is null ? string.Empty : $"&q={Uri.EscapeDataString(text)}")}{(parentId is null ? string.Empty : $"&parentId={parentId}")}";
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LookupPageDto>())!;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string entityKey, JsonObject values)
    {
        var response = await client.PostAsJsonAsync($"/api/records/{entityKey}", new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
