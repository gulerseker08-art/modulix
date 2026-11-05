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
/// ADR-0032 dilim 2b-3: lokasyon seviye tanımı. Mutlu yol (örnek koddaki beş seviye, varsayılanlar, sıralı liste, güncelleme, silme),
/// doğrulama (sıra, anahtar, uzunluk aralığı, ayraç, karakter kümesi, tesis), tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseLocationLevelTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Facilities = "/api/records/warehouse.facility";
    private const string Levels = "/api/records/warehouse.location-level";

    [Fact]
    public async Task Facility_defines_its_own_levels_in_order_and_the_facility_cannot_be_removed_while_it_has_levels()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var facility = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "ant", ["name"] = "Antrepo" });
        var other = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "dep", ["name"] = "Genel Depo" });

        // ANT-A-02-04-C biçimi: tesis, koridor, raf, kat, göz. Bilinçli olarak sırasız gönderilir; liste sıraya göre gelir.
        var cell = await PostLevelAsync(admin, facility.Id, 5, "cell", "Göz", "letters", 1, 1);
        await PostLevelAsync(admin, facility.Id, 1, "facility", "Tesis", "letters", 2, 5);
        await PostLevelAsync(admin, facility.Id, 3, "rack", "Raf", "digits", 2, 2);
        await PostLevelAsync(admin, facility.Id, 2, "aisle", "Koridor", "letters", 1, 2);
        await PostLevelAsync(admin, facility.Id, 4, "floor", "Kat", "digits", 2, 2);
        Assert.Equal("-", cell.Values["separator"]!.GetValue<string>());

        var list = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Levels}?facilityId={facility.Id}"))!;
        Assert.Equal(["facility", "aisle", "rack", "floor", "cell"], list.Items.Select(i => i.Values["key"]!.GetValue<string>()));

        // Başka tesis farklı yapı kurar: tek seviye, ayraçsız; ilk tesisin tanımını etkilemez.
        await PostLevelAsync(admin, other.Id, 1, "bin", "Göz", "alphanumeric", 1, 6, separator: "");
        var onlyOther = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Levels}?facilityId={other.Id}"))!;
        Assert.Single(onlyOther.Items);
        Assert.Equal(5, (await admin.GetFromJsonAsync<RecordPageResponse>($"{Levels}?facilityId={facility.Id}"))!.TotalCount);

        var rename = await admin.PutAsJsonAsync($"{Levels}/{cell.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Göz / Palet yeri" }, cell.Version));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        // Seviye tanımı olan tesis silinemez; tanımlar silinince silinir.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}")).StatusCode);
        var renamed = (await rename.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Levels}/{cell.Id}?version={renamed.Version}")).StatusCode);
        Assert.Equal(4, (await admin.GetFromJsonAsync<RecordPageResponse>($"{Levels}?facilityId={facility.Id}"))!.TotalCount);
    }

    [Fact]
    public async Task Invalid_level_definitions_are_rejected_with_field_errors()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var facility = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "ANT", ["name"] = "Antrepo" });
        var first = await PostLevelAsync(admin, facility.Id, 1, "aisle", "Koridor", "letters", 1, 2);

        var duplicatePosition = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 1, "rack", "Raf"));
        Assert.Equal(["Bu sırada başka bir seviye var."], duplicatePosition.Errors["position"]);

        var duplicateKey = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 2, "AISLE", "Başka koridor"));
        Assert.Equal(["Bu anahtar tesisteki başka bir seviyede kullanılıyor."], duplicateKey.Errors["key"]);

        var badKey = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 2, "1raf", "Raf"));
        Assert.True(badKey.Errors.ContainsKey("key"));

        var badRange = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 2, "rack", "Raf", minLength: 5, maxLength: 2));
        Assert.True(badRange.Errors.ContainsKey("minLength"));

        var badSeparator = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 2, "rack", "Raf", separator: "A"));
        Assert.True(badSeparator.Errors.ContainsKey("separator"));

        var badSet = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 2, "rack", "Raf", characterSet: "emoji"));
        Assert.True(badSet.Errors.ContainsKey("characterSet"));

        var badPosition = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 0, "rack", "Raf"));
        Assert.True(badPosition.Errors.ContainsKey("position"));
        var tooDeep = await ValidationProblemAsync(await PostRawAsync(admin, facility.Id, 11, "rack", "Raf"));
        Assert.True(tooDeep.Errors.ContainsKey("position"));

        var noFacility = await ValidationProblemAsync(await PostRawAsync(admin, Guid.NewGuid(), 2, "rack", "Raf"));
        Assert.Equal(["Tesis bulunamadı."], noFacility.Errors["facilityId"]);

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Levels, new RecordWriteRequest(new JsonObject())));
        Assert.All(new[] { "facilityId", "position", "key", "name" }, field => Assert.True(missing.Errors.ContainsKey(field)));

        // Tesis sonradan değiştirilemez; sıra başka seviyeyle çakışacak şekilde güncellenemez.
        var second = await PostLevelAsync(admin, facility.Id, 2, "rack", "Raf", "digits", 2, 2);
        var other = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "DEP", ["name"] = "Depo" });
        var moved = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Levels}/{second.Id}",
            new RecordWriteRequest(new JsonObject { ["facilityId"] = other.Id.ToString() }, second.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], moved.Errors["facilityId"]);
        var clash = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Levels}/{second.Id}",
            new RecordWriteRequest(new JsonObject { ["position"] = 1 }, second.Version)));
        Assert.Equal(["Bu sırada başka bir seviye var."], clash.Errors["position"]);
        Assert.Equal(first.Id, (await admin.GetFromJsonAsync<RecordResponse>($"{Levels}/{first.Id}"))!.Id);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_define_levels_on_each_others_facilities()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var facilityA = await PostAsync(adminA, Facilities, new JsonObject { ["code"] = "ORT", ["name"] = "A tesisi" });
        var levelA = await PostLevelAsync(adminA, facilityA.Id, 1, "aisle", "Koridor", "letters", 1, 2);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Levels}/{levelA.Id}")).StatusCode);
        var listB = (await adminB.GetFromJsonAsync<RecordPageResponse>(Levels))!;
        Assert.DoesNotContain(listB.Items, i => i.Id == levelA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Levels}/{levelA.Id}?version={levelA.Version}")).StatusCode);

        var foreign = await ValidationProblemAsync(await PostRawAsync(adminB, facilityA.Id, 2, "rack", "Sızıntı"));
        Assert.Equal(["Tesis bulunamadı."], foreign.Errors["facilityId"]);

        // Aynı tesis kodu ve aynı seviye tanımı başka tenant'ta serbesttir.
        var facilityB = await PostAsync(adminB, Facilities, new JsonObject { ["code"] = "ORT", ["name"] = "B tesisi" });
        await PostLevelAsync(adminB, facilityB.Id, 1, "aisle", "Koridor", "letters", 1, 2);
    }

    [Fact]
    public async Task Level_definitions_need_their_own_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var zonesOnly = await LoginAsync(s.ZonesOnly);
        var facility = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "YET", ["name"] = "Yetki" });
        var level = await PostLevelAsync(admin, facility.Id, 1, "aisle", "Koridor", "letters", 1, 2);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Levels}/{level.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRawAsync(reader, facility.Id, 2, "rack", "Raf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Levels}/{level.Id}?version={level.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostRawAsync(writer, facility.Id, 2, "rack", "Raf")).StatusCode);

        // Alan izni seviye tanımı izni vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await zonesOnly.GetAsync(Levels)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRawAsync(zonesOnly, facility.Id, 3, "floor", "Kat")).StatusCode);
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser ZonesOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var zonesOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [LocationLevelsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [LocationLevelsRead, LocationLevelsWrite, FacilitiesRead], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [ZonesRead, ZonesWrite], Scope.All, zonesOnly);
        return (admin, reader, writer, zonesOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> PostRawAsync(
        HttpClient client, Guid facilityId, int position, string key, string name,
        string? characterSet = null, int? minLength = null, int? maxLength = null, string? separator = null)
    {
        var values = new JsonObject { ["facilityId"] = facilityId.ToString(), ["position"] = position, ["key"] = key, ["name"] = name };
        if (characterSet is not null) { values["characterSet"] = characterSet; }
        if (minLength is not null) { values["minLength"] = minLength; }
        if (maxLength is not null) { values["maxLength"] = maxLength; }
        if (separator is not null) { values["separator"] = separator; }
        return client.PostAsJsonAsync(Levels, new RecordWriteRequest(values));
    }

    private static async Task<RecordResponse> PostLevelAsync(
        HttpClient client, Guid facilityId, int position, string key, string name, string characterSet, int minLength, int maxLength, string? separator = null)
    {
        var response = await PostRawAsync(client, facilityId, position, key, name, characterSet, minLength, maxLength, separator);
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
