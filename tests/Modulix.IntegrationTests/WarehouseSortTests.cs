using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Depo kayıtlarında başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036 ile aynı kalıp): artan/azalan, boş değerler sonda, ilişkili
/// tesis/alan koduna göre sıralama, geçersiz anahtar reddi (sıralanamaz alanlar dahil), tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Facilities = "/api/records/warehouse.facility";
    private const string Zones = "/api/records/warehouse.zone";
    private const string Levels = "/api/records/warehouse.location-level";
    private const string Locations = "/api/records/warehouse.location";
    private const string Attorneys = "/api/records/warehouse.power-of-attorney";
    private const string StockItems = "/api/records/warehouse.stock-item";

    [Fact]
    public async Task Facilities_and_stock_items_sort_in_both_directions_with_empty_values_last()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await PostAsync(admin, Facilities, new JsonObject { ["code"] = "BRV", ["name"] = "Charlie tesis", ["countryCode"] = "TR" });
        await PostAsync(admin, Facilities, new JsonObject { ["code"] = "ALF", ["name"] = "Bravo tesis", ["countryCode"] = "DE", ["isActive"] = false });
        await PostAsync(admin, Facilities, new JsonObject { ["code"] = "CHR", ["name"] = "Alfa tesis" });

        Assert.Equal(["ALF", "BRV", "CHR"], await ValuesAsync(admin, Facilities, "code", null));
        Assert.Equal(["CHR", "BRV", "ALF"], await ValuesAsync(admin, Facilities, "code", "-code"));
        Assert.Equal(["CHR", "ALF", "BRV"], await ValuesAsync(admin, Facilities, "code", "name"));
        Assert.Equal(["ALF", "BRV", "CHR"], await ValuesAsync(admin, Facilities, "code", "countryCode"));
        Assert.Equal(["BRV", "ALF", "CHR"], await ValuesAsync(admin, Facilities, "code", "-countryCode"));
        Assert.Equal(["ALF", "BRV", "CHR"], await ValuesAsync(admin, Facilities, "code", "isActive"));

        await PostAsync(admin, StockItems, new JsonObject { ["code"] = "B-2", ["name"] = "Çay", ["gtip"] = "090230" });
        await PostAsync(admin, StockItems, new JsonObject { ["code"] = "A-1", ["name"] = "Kakao", ["gtip"] = "180500" });
        await PostAsync(admin, StockItems, new JsonObject { ["code"] = "C-3", ["name"] = "Ambalaj" });

        Assert.Equal(["C-3", "B-2", "A-1"], await ValuesAsync(admin, StockItems, "code", "-code"));
        Assert.Equal(["C-3", "B-2", "A-1"], await ValuesAsync(admin, StockItems, "code", "name"));
        Assert.Equal(["B-2", "A-1", "C-3"], await ValuesAsync(admin, StockItems, "code", "gtip"));
        Assert.Equal(["A-1", "B-2", "C-3"], await ValuesAsync(admin, StockItems, "code", "-gtip"));
        // Sıralama diğer süzgeçlerle birlikte çalışır.
        Assert.Equal(["B-2"], await ValuesAsync(admin, StockItems, "code", "-code", "&gtip=09"));
        Assert.Equal(["C-3", "B-2", "A-1"], await ValuesAsync(admin, StockItems, "code", "-code", "&isActive=true"));
    }

    [Fact]
    public async Task Zones_sort_by_the_facility_code_and_levels_and_locations_sort_by_their_columns()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var first = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "AAA", ["name"] = "İlk" });
        var second = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "ZZZ", ["name"] = "Son" });
        await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = second.Id.ToString(), ["code"] = "A1", ["name"] = "Z tesisi alanı" });
        await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = first.Id.ToString(), ["code"] = "B1", ["name"] = "A tesisi alanı" });

        Assert.Equal(["B1", "A1"], await ValuesAsync(admin, Zones, "code", "facility"));
        Assert.Equal(["A1", "B1"], await ValuesAsync(admin, Zones, "code", "-facility"));
        Assert.Equal(["A1", "B1"], await ValuesAsync(admin, Zones, "code", "code"));
        Assert.Equal(["B1", "A1"], await ValuesAsync(admin, Zones, "code", "-code"));

        await PostAsync(admin, Levels, new JsonObject { ["facilityId"] = first.Id.ToString(), ["position"] = 1, ["key"] = "raf", ["name"] = "Raf", ["characterSet"] = "letters", ["maxLength"] = 3 });
        await PostAsync(admin, Levels, new JsonObject { ["facilityId"] = first.Id.ToString(), ["position"] = 2, ["key"] = "goz", ["name"] = "Göz", ["characterSet"] = "digits", ["maxLength"] = 2 });
        Assert.Equal(["raf", "goz"], await ValuesAsync(admin, Levels, "key", "position"));
        Assert.Equal(["goz", "raf"], await ValuesAsync(admin, Levels, "key", "-position"));
        Assert.Equal(["goz", "raf"], await ValuesAsync(admin, Levels, "key", "name"));
        Assert.Equal(["raf", "goz"], await ValuesAsync(admin, Levels, "key", "-maxLength"));

        await PostAsync(admin, Locations, new JsonObject { ["facilityId"] = first.Id.ToString(), ["value"] = "B" });
        await PostAsync(admin, Locations, new JsonObject { ["facilityId"] = first.Id.ToString(), ["value"] = "A" });
        Assert.Equal(["A", "B"], await ValuesAsync(admin, Locations, "code", "code"));
        Assert.Equal(["B", "A"], await ValuesAsync(admin, Locations, "code", "-code"));
        Assert.Equal(["A", "B"], await ValuesAsync(admin, Locations, "code", "zone"));
    }

    [Fact]
    public async Task An_unknown_or_unsortable_key_is_rejected_for_every_warehouse_record_type()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        foreach (var (url, key) in new[]
        {
            (Facilities, "password"), (Facilities, "description"), (Zones, "description"), (Levels, "facilityId"),
            (Locations, "path"), (StockItems, "description"), (StockItems, "-nope"),
            // Vekâlet tarafları firma adına göre sıralanmaz (firma görünürlüğü sıralamayla dolanılmasın).
            (Attorneys, "importer"), (Attorneys, "importerPartyId"), (Attorneys, "broker"),
        })
        {
            var response = await admin.GetAsync($"{url}?sort={Uri.EscapeDataString(key)}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>())!;
            Assert.Contains("sort", problem.Errors.Keys);
        }

        // Geçerli anahtarlar vekâlet listesinde de kabul edilir.
        foreach (var key in new[] { "validFrom", "-validFrom", "validTo", "-isActive", "createdAt" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Attorneys}?sort={key}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_records()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        await PostAsync(adminA, Facilities, new JsonObject { ["code"] = "ONLYA", ["name"] = "Yalnızca A" });
        await PostAsync(adminB, Facilities, new JsonObject { ["code"] = "ONLYB", ["name"] = "Yalnızca B" });

        Assert.Equal(["ONLYA"], await ValuesAsync(adminA, Facilities, "code", "-name"));
        Assert.Equal(["ONLYB"], await ValuesAsync(adminB, Facilities, "code", "name"));
    }

    [Fact]
    public async Task Sorting_requires_the_read_permission_of_the_record_type()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var facilityOnly = await LoginAsync(s.FacilityOnly);
        await PostAsync(admin, StockItems, new JsonObject { ["code"] = "GIZLI", ["name"] = "Gizli kart" });

        Assert.Equal(HttpStatusCode.OK, (await facilityOnly.GetAsync($"{Facilities}?sort=code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync($"{StockItems}?sort=code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync($"{StockItems}?sort=-name")).StatusCode);
    }

    private async Task<(ModulixUser Admin, ModulixUser FacilityOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var facilityOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead], Scope.All, facilityOnly);
        return (admin, facilityOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<List<string>> ValuesAsync(HttpClient client, string url, string field, string? sort, string extra = "")
    {
        var sortPart = sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}";
        var response = await client.GetAsync($"{url}?pageSize=50{sortPart}{extra}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!;
        return [.. page.Items.Select(i => i.Values[field]!.GetValue<string>())];
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
