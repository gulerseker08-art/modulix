using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 2b-4: lokasyon ağacı. Mutlu yol (örnek kod ANT-A-02-04-C'nin üretilmesi, alan kalıtımı, süzme, silme), doğrulama
/// (seviye tanımı, karakter kümesi, uzunluk, kod benzersizliği, alan kuralları, değişmez alanlar), kullanımdaki tesiste seviye tanımı
/// denetimi ve silme engelleri, tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseLocationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Facilities = "/api/records/warehouse.facility";
    private const string Zones = "/api/records/warehouse.zone";
    private const string Levels = "/api/records/warehouse.location-level";
    private const string Locations = "/api/records/warehouse.location";

    [Fact]
    public async Task Location_codes_are_generated_from_the_level_definitions_and_zones_are_inherited_down_the_tree()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var facility = await CreateFacilityWithLevelsAsync(admin, "ant", customs: true);
        var controlled = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["code"] = "G1", ["name"] = "Gümrük alanı", ["kindKey"] = "Controlled" });

        // ANT-A-02-04-C: tesis → koridor → raf → kat → göz. Değerler küçük harfle gelir, büyük harfe çevrilir; kod sunucuda üretilir.
        var root = await PostLocationAsync(admin, facility.Id, null, "ant", zoneId: controlled.Id);
        Assert.Equal(("ANT", 1), (root.Values["code"]!.GetValue<string>(), root.Values["depth"]!.GetValue<int>()));
        var aisle = await PostLocationAsync(admin, facility.Id, root.Id, "a");
        var rack = await PostLocationAsync(admin, facility.Id, aisle.Id, "02");
        var floor = await PostLocationAsync(admin, facility.Id, rack.Id, "04");
        var cell = await PostLocationAsync(admin, facility.Id, floor.Id, "c", name: "Palet yeri");
        Assert.Equal("ANT-A-02-04-C", cell.Values["code"]!.GetValue<string>());
        Assert.Equal(5, cell.Values["depth"]!.GetValue<int>());

        // Alan üst düğümden kalıtılır.
        Assert.Equal(controlled.Id.ToString(), cell.Values["zoneId"]!.GetValue<string>());

        var children = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Locations}?parentId={aisle.Id}"))!;
        Assert.Equal([rack.Id], children.Items.Select(i => i.Id));
        var roots = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Locations}?facilityId={facility.Id}&root=true"))!;
        Assert.Equal([root.Id], roots.Items.Select(i => i.Id));
        var searched = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Locations}?search=02-04-C"))!;
        Assert.Equal([cell.Id], searched.Items.Select(i => i.Id));

        // Ad ve etkinlik değişir; kod ve değer değişmez, sistem alanları yok sayılır.
        var update = await admin.PutAsJsonAsync($"{Locations}/{cell.Id}", new RecordWriteRequest(
            new JsonObject { ["name"] = "Palet yeri 1", ["isActive"] = false, ["code"] = "HACK-1", ["depth"] = 9 }, cell.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(("ANT-A-02-04-C", 5, false), (updated.Values["code"]!.GetValue<string>(), updated.Values["depth"]!.GetValue<int>(), updated.Values["isActive"]!.GetValue<bool>()));

        // Alt düğümü olan düğüm silinemez; yapraktan başlayarak silinir.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Locations}/{floor.Id}?version={floor.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Locations}/{cell.Id}?version={updated.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Locations}/{floor.Id}?version={floor.Version}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_locations_are_rejected_and_levels_of_a_facility_in_use_only_grow_and_get_renamed()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var facility = await CreateFacilityWithLevelsAsync(admin, "ant", customs: true);
        var other = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "DEP", ["name"] = "Seviyesiz depo" });
        var free = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["code"] = "S1", ["name"] = "Serbest", ["kindKey"] = "Free" });
        var controlled = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["code"] = "G1", ["name"] = "Gümrük", ["kindKey"] = "Controlled" });
        var root = await PostLocationAsync(admin, facility.Id, null, "ANT", zoneId: controlled.Id);

        // Seviye tanımı: tesiste tanım yoksa, derinlik tanımı aşıyorsa, karakter ya da uzunluk uymuyorsa.
        var noLevels = await ValidationProblemAsync(await PostRawAsync(admin, other.Id, null, "X"));
        Assert.Equal(["Tesiste lokasyon seviyesi tanımlı değil."], noLevels.Errors["facilityId"]);
        var badChars = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, root.Id, "1"));
        Assert.Contains("yalnızca harf", badChars.Errors["value"][0]);
        var tooLong = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, root.Id, "ABC"));
        Assert.Contains("1 ile 2 karakter", tooLong.Errors["value"][0]);
        var aisle = await PostLocationAsync(admin, facility.Id, root.Id, "A");
        var rack = await PostLocationAsync(admin, facility.Id, aisle.Id, "02");
        var floor = await PostLocationAsync(admin, facility.Id, rack.Id, "04");
        var cell = await PostLocationAsync(admin, facility.Id, floor.Id, "C");
        var tooDeep = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, cell.Id, "X"));
        Assert.Equal(["Tesiste 6. seviye tanımlı değil."], tooDeep.Errors["parentId"]);

        // Kod tesis içinde benzersizdir; üst düğüm başka tesisten olamaz; değer, üst düğüm ve tesis sonradan değişmez.
        var duplicate = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, root.Id, "a"));
        Assert.Equal(["ANT-A kodlu lokasyon zaten var."], duplicate.Errors["value"]);
        var foreignParent = await ValidationProblemAsync(await PostRawAsync(admin, other.Id, root.Id, "B"));
        Assert.Equal(["Üst lokasyon başka bir tesise ait."], foreignParent.Errors["parentId"]);
        var renameValue = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Locations}/{aisle.Id}",
            new RecordWriteRequest(new JsonObject { ["value"] = "B" }, aisle.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], renameValue.Errors["value"]);
        var reparent = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Locations}/{aisle.Id}",
            new RecordWriteRequest(new JsonObject { ["parentId"] = floor.Id.ToString() }, aisle.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], reparent.Errors["parentId"]);

        // Alan: üst düğümün alanından farklı olamaz, başka tesisin alanı olamaz, alt düğümü olan düğümün alanı değişmez.
        var differentZone = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, root.Id, "B", zoneId: free.Id));
        Assert.Equal(["Alt lokasyon, üst lokasyonun alanından farklı bir alanda olamaz."], differentZone.Errors["zoneId"]);
        var otherZone = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = other.Id.ToString(), ["code"] = "S1", ["name"] = "Başka tesis alanı" });
        var foreignZone = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, null, "ZZ", zoneId: otherZone.Id));
        Assert.Equal(["Alan bu tesise ait değil."], foreignZone.Errors["zoneId"]);
        var rootNow = (await admin.GetFromJsonAsync<RecordResponse>($"{Locations}/{root.Id}"))!;
        var changeZone = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Locations}/{root.Id}",
            new RecordWriteRequest(new JsonObject { ["zoneId"] = free.Id.ToString() }, rootNow.Version)));
        Assert.Equal(["Alt lokasyonları olan lokasyonun alanı değiştirilemez."], changeZone.Errors["zoneId"]);

        // Lokasyonu olan tesiste seviye tanımı yalnızca eklenir ve yeniden adlandırılır.
        var levels = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Levels}?facilityId={facility.Id}"))!;
        var rackLevel = levels.Items.Single(l => l.Values["key"]!.GetValue<string>() == "rack");
        var changeSeparator = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Levels}/{rackLevel.Id}",
            new RecordWriteRequest(new JsonObject { ["separator"] = "." }, rackLevel.Version)));
        Assert.Equal(["Lokasyonu olan tesiste seviye tanımı değiştirilemez; yalnızca ad değişir."], changeSeparator.Errors["separator"]);
        var rename = await admin.PutAsJsonAsync($"{Levels}/{rackLevel.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Raf sırası" }, rackLevel.Version));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var insertInMiddle = await ValidationProblemAsync(await PostLevelRawAsync(admin, facility.Id, 1, "extra", "Ara seviye"));
        Assert.Contains("Lokasyonu olan tesiste yeni seviye yalnızca mevcut seviyelerden sonra eklenebilir.", insertInMiddle.Errors["position"]);
        var deeper = await PostLevelRawAsync(admin, facility.Id, 6, "pallet", "Palet");
        Assert.Equal(HttpStatusCode.Created, deeper.StatusCode);

        // Kullanılan seviye silinemez; kullanılmayan en derin seviye silinebilir. Alanı, seviyesi ya da lokasyonu olan tesis silinemez.
        var cellLevel = levels.Items.Single(l => l.Values["key"]!.GetValue<string>() == "cell");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Levels}/{cellLevel.Id}?version={cellLevel.Version}")).StatusCode);
        var palletLevel = (await deeper.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Levels}/{palletLevel.Id}?version={palletLevel.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Zones}/{controlled.Id}?version={controlled.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}")).StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_build_on_each_others_locations()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var facilityA = await CreateFacilityWithLevelsAsync(adminA, "ort", customs: false);
        var rootA = await PostLocationAsync(adminA, facilityA.Id, null, "ORT");

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Locations}/{rootA.Id}")).StatusCode);
        var listB = (await adminB.GetFromJsonAsync<RecordPageResponse>(Locations))!;
        Assert.DoesNotContain(listB.Items, i => i.Id == rootA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Locations}/{rootA.Id}?version={rootA.Version}")).StatusCode);

        // B, A'nın tesisine ya da lokasyonuna bağlanamaz; varlıkları da belli edilmez.
        var foreignFacility = await ValidationProblemAsync(await PostRawAsync(adminB, facilityA.Id, null, "ORT"));
        Assert.Equal(["Tesis bulunamadı."], foreignFacility.Errors["facilityId"]);
        var facilityB = await CreateFacilityWithLevelsAsync(adminB, "ort", customs: false);
        var foreignParent = await ValidationProblemAsync(await PostRawAsync(adminB, facilityB.Id, rootA.Id, "A"));
        Assert.Equal(["Üst lokasyon bulunamadı."], foreignParent.Errors["parentId"]);

        // Aynı kod başka tenant'ta serbesttir.
        var rootB = await PostLocationAsync(adminB, facilityB.Id, null, "ORT");
        Assert.NotEqual(rootA.Id, rootB.Id);
    }

    [Fact]
    public async Task Locations_need_their_own_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var levelsOnly = await LoginAsync(s.LevelsOnly);
        var facility = await CreateFacilityWithLevelsAsync(admin, "yet", customs: false);
        var root = await PostLocationAsync(admin, facility.Id, null, "YET");

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Locations}/{root.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRawAsync(reader, facility.Id, root.Id, "A")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Locations}/{root.Id}?version={root.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostRawAsync(writer, facility.Id, root.Id, "A")).StatusCode);

        // Seviye tanımı izni lokasyon izni vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await levelsOnly.GetAsync(Locations)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRawAsync(levelsOnly, facility.Id, root.Id, "B")).StatusCode);
    }

    private async Task<RecordResponse> CreateFacilityWithLevelsAsync(HttpClient client, string code, bool customs)
    {
        var facility = await PostAsync(client, Facilities, new JsonObject { ["code"] = code, ["name"] = $"{code} tesisi", ["customsEnabled"] = customs, ["allowMixedZones"] = customs });
        (int Position, string Key, string Name, string Set, int Min, int Max)[] levels =
        [
            (1, "facility", "Tesis", "letters", 2, 5), (2, "aisle", "Koridor", "letters", 1, 2),
            (3, "rack", "Raf", "digits", 2, 2), (4, "floor", "Kat", "digits", 2, 2), (5, "cell", "Göz", "letters", 1, 1),
        ];
        foreach (var (position, key, name, set, min, max) in levels)
        {
            var response = await PostLevelRawAsync(client, facility.Id, position, key, name, set, min, max);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }

        return facility;
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser LevelsOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var levelsOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [LocationsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [LocationsRead, LocationsWrite], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [LocationLevelsRead, LocationLevelsWrite], Scope.All, levelsOnly);
        return (admin, reader, writer, levelsOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> PostLevelRawAsync(
        HttpClient client, Guid facilityId, int position, string key, string name, string characterSet = "alphanumeric", int minLength = 1, int maxLength = 5) =>
        client.PostAsJsonAsync(Levels, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = facilityId.ToString(), ["position"] = position, ["key"] = key, ["name"] = name,
            ["characterSet"] = characterSet, ["minLength"] = minLength, ["maxLength"] = maxLength,
        }));

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, Guid facilityId, Guid? parentId, string value, Guid? zoneId = null, string? name = null)
    {
        var values = new JsonObject { ["facilityId"] = facilityId.ToString(), ["value"] = value };
        if (parentId is not null) { values["parentId"] = parentId.ToString(); }
        if (zoneId is not null) { values["zoneId"] = zoneId.ToString(); }
        if (name is not null) { values["name"] = name; }
        return client.PostAsJsonAsync(Locations, new RecordWriteRequest(values));
    }

    private static async Task<RecordResponse> PostLocationAsync(HttpClient client, Guid facilityId, Guid? parentId, string value, Guid? zoneId = null, string? name = null)
    {
        var response = await PostRawAsync(client, facilityId, parentId, value, zoneId, name);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
