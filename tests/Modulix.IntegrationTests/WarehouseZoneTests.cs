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
/// ADR-0032 dilim 2b-2: alan kaydı ve alan türü kategorisine bağlı kurallar. Mutlu yol (oluşturma, varsayılan tür, süzme, güncelleme,
/// silme), doğrulama (kod, tesis, gümrük denetimli alanın gümrük uzantısı ister olması, karma alan kuralı, tesis ayarlarının
/// mevcut alanlarla çelişmesi, tesis silme engeli), tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseZoneTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Facilities = "/api/records/warehouse.facility";
    private const string Zones = "/api/records/warehouse.zone";

    [Fact]
    public async Task Zone_is_created_with_default_kind_listed_updated_and_deleted_and_the_facility_cannot_be_removed_while_it_has_zones()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var facility = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "dep", ["name"] = "Genel Depo" });

        // Tür verilmezse listenin varsayılanı (serbest) atanır; kod büyük harfe çevrilir.
        var free = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["code"] = "a1", ["name"] = "Serbest A" });
        Assert.Equal("A1", free.Values["code"]!.GetValue<string>());
        Assert.Equal("Free", free.Values["kindKey"]!.GetValue<string>());

        var filtered = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Zones}?facilityId={facility.Id}&kindKey=Free"))!;
        Assert.Equal([free.Id], filtered.Items.Select(i => i.Id));
        var searched = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Zones}?search=serbest"))!;
        Assert.Contains(searched.Items, i => i.Id == free.Id);

        var update = await admin.PutAsJsonAsync($"{Zones}/{free.Id}", new RecordWriteRequest(new JsonObject { ["name"] = "Serbest Alan A" }, free.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal("Serbest Alan A", updated.Values["name"]!.GetValue<string>());

        // Alanı olan tesis silinemez; alan silinince tesis silinir.
        var blocked = await admin.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Zones}/{free.Id}?version={updated.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Facilities}/{facility.Id}?version={facility.Version}")).StatusCode);
    }

    [Fact]
    public async Task Zone_kind_category_rules_control_customs_zones_and_mixing_in_a_facility()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var closed = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "KPL", ["name"] = "Gümrüksüz tesis" });
        var customs = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "ANT", ["name"] = "Antrepo", ["customsEnabled"] = true });

        // Gümrük uzantısı kapalı tesiste gümrük denetimli alan tanımlanamaz.
        var notAllowed = await ValidationProblemAsync(await admin.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = closed.Id.ToString(), ["code"] = "G1", ["name"] = "Denetimli", ["kindKey"] = "Controlled",
        })));
        Assert.Equal(["Gümrük denetimli alan yalnızca gümrük uzantısı açık tesiste tanımlanabilir."], notAllowed.Errors["kindKey"]);

        // Açık tesiste gümrük denetimli alan açılır; aynı tesiste serbest alan ancak tesis karmaya izin veriyorsa açılır (varsayılan kapalı).
        var controlled = await PostAsync(admin, Zones, new JsonObject
        {
            ["facilityId"] = customs.Id.ToString(), ["code"] = "G1", ["name"] = "Denetimli", ["kindKey"] = "Controlled",
        });
        var mixedBlocked = await ValidationProblemAsync(await admin.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = customs.Id.ToString(), ["code"] = "S1", ["name"] = "Serbest", ["kindKey"] = "Free",
        })));
        Assert.Equal(["Bu tesiste serbest ve gümrük denetimli alanlar birlikte bulunamaz (tesis ayarı kapalı)."], mixedBlocked.Errors["kindKey"]);

        var allow = await admin.PutAsJsonAsync($"{Facilities}/{customs.Id}", new RecordWriteRequest(new JsonObject { ["allowMixedZones"] = true }, customs.Version));
        Assert.Equal(HttpStatusCode.OK, allow.StatusCode);
        var allowed = (await allow.Content.ReadFromJsonAsync<RecordResponse>())!;
        await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = customs.Id.ToString(), ["code"] = "S1", ["name"] = "Serbest", ["kindKey"] = "Free" });

        // Mevcut alanlarla çelişen tesis ayarı kaydedilemez.
        var disallow = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Facilities}/{customs.Id}",
            new RecordWriteRequest(new JsonObject { ["allowMixedZones"] = false }, allowed.Version)));
        Assert.True(disallow.Errors.ContainsKey("allowMixedZones"));
        var closeCustoms = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Facilities}/{customs.Id}",
            new RecordWriteRequest(new JsonObject { ["customsEnabled"] = false }, allowed.Version)));
        Assert.True(closeCustoms.Errors.ContainsKey("customsEnabled"));

        // Kod tesis içinde benzersizdir; başka tesiste serbesttir. Tesis sonradan değiştirilemez; bilinmeyen tür reddedilir.
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = customs.Id.ToString(), ["code"] = "g1", ["name"] = "Çift", ["kindKey"] = "Controlled",
        })));
        Assert.Equal(["Bu kod tesisteki başka bir alanda kullanılıyor."], duplicate.Errors["code"]);
        await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = closed.Id.ToString(), ["code"] = "G1", ["name"] = "Serbest ama aynı kod" });
        var moved = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Zones}/{controlled.Id}",
            new RecordWriteRequest(new JsonObject { ["facilityId"] = closed.Id.ToString() }, controlled.Version)));
        Assert.Equal(["Oluşturulduktan sonra değiştirilemez."], moved.Errors["facilityId"]);
        var unknownKind = await ValidationProblemAsync(await admin.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = customs.Id.ToString(), ["code"] = "X1", ["name"] = "Bilinmeyen", ["kindKey"] = "Uydurma",
        })));
        Assert.Equal(["Listede olmayan değer."], unknownKind.Errors["kindKey"]);
        var noFacility = await ValidationProblemAsync(await admin.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = Guid.NewGuid().ToString(), ["code"] = "X2", ["name"] = "Tesissiz",
        })));
        Assert.Equal(["Tesis bulunamadı."], noFacility.Errors["facilityId"]);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_use_each_others_zones_and_facilities()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var facilityA = await PostAsync(adminA, Facilities, new JsonObject { ["code"] = "ORT", ["name"] = "A tesisi" });
        var zoneA = await PostAsync(adminA, Zones, new JsonObject { ["facilityId"] = facilityA.Id.ToString(), ["code"] = "Z1", ["name"] = "A alanı" });

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Zones}/{zoneA.Id}")).StatusCode);
        var listB = (await adminB.GetFromJsonAsync<RecordPageResponse>(Zones))!;
        Assert.DoesNotContain(listB.Items, i => i.Id == zoneA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Zones}/{zoneA.Id}?version={zoneA.Version}")).StatusCode);

        // B, A'nın tesisine alan ekleyemez; varlığı da belli edilmez.
        var foreign = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = facilityA.Id.ToString(), ["code"] = "Z1", ["name"] = "Sızıntı",
        })));
        Assert.Equal(["Tesis bulunamadı."], foreign.Errors["facilityId"]);
    }

    [Fact]
    public async Task Zones_need_their_own_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var facilityOnly = await LoginAsync(s.FacilityOnly);
        var facility = await PostAsync(admin, Facilities, new JsonObject { ["code"] = "YET", ["name"] = "Yetki" });
        var zone = await PostAsync(admin, Zones, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["code"] = "Z1", ["name"] = "Yetki alanı" });

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Zones}/{zone.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = facility.Id.ToString(), ["code"] = "R1", ["name"] = "Okuyucu",
        }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Zones}/{zone.Id}?version={zone.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Zones, new RecordWriteRequest(new JsonObject
        {
            ["facilityId"] = facility.Id.ToString(), ["code"] = "W1", ["name"] = "Yazıcı",
        }))).StatusCode);

        // Tesis izni alan izni vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await facilityOnly.GetAsync(Zones)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await facilityOnly.GetAsync(Facilities)).StatusCode);
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser FacilityOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var facilityOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ZonesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [ZonesRead, ZonesWrite, FacilitiesRead], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead, FacilitiesWrite], Scope.All, facilityOnly);
        return (admin, reader, writer, facilityOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
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
