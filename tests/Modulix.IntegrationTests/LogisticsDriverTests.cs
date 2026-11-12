using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 3: sürücü kartı. Mutlu yol (kişi açıp sürücü kartı, taşeron sürücü, Filo aracı atama, ada göre arama ve sıralama,
/// süzgeç, denetim kaydı), doğrulama (kişi başına tek kart, taşıyıcı zorunluluğu ve rolü, dorse ve pasif araç atanamaz, Filo kapalıyken
/// araç seçilemez, silme yasağı), tenant izolasyonu (başka tenant'ın kişisi, aracı ve sürücüsü görünmez) ve yetki (okuyan yazamaz,
/// izinsiz göremez, sürücü yazma izni kişi açmaya yeter).
/// </summary>
public class LogisticsDriverTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Drivers = "/api/records/logistics.driver";
    private const string People = "/api/records/core.person";
    private const string Parties = "/api/records/core.party";
    private const string Vehicles = "/api/records/fleet.vehicle";

    [Fact]
    public async Task Drivers_are_people_with_a_type_carrier_and_fleet_vehicle_and_are_searchable_by_name()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var ahmet = await PersonAsync(admin, "Ahmet", "Yılmaz", "0532 111 22 33");
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34SRC01", ["kindKey"] = "Tractor" });
        var employee = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = ahmet.ToString(), ["defaultVehicleId"] = truck.Id.ToString() });
        Assert.Equal("employee", employee.Values["driverType"]!.GetValue<string>());
        Assert.Equal("active", employee.Values["status"]!.GetValue<string>());

        var carrier = await PartyAsync(admin, "Kartal Nakliyat", "tasiyici");
        var zeynep = await PersonAsync(admin, "Zeynep", "Akın", null);
        var subcontracted = await PostAsync(admin, Drivers, new JsonObject
        {
            ["personId"] = zeynep.ToString(), ["driverType"] = "subcontracted", ["carrierPartyId"] = carrier.ToString(),
        });

        Assert.Equal([subcontracted.Id, employee.Id], (await ListAsync(admin, "sort=name")).Select(d => d.Id)); // Akın, Yılmaz
        Assert.Equal([employee.Id], (await ListAsync(admin, "search=0532")).Select(d => d.Id));
        Assert.Equal([employee.Id], (await ListAsync(admin, "search=Ahmet Yıl")).Select(d => d.Id));
        Assert.Equal([subcontracted.Id], (await ListAsync(admin, "driverType=subcontracted")).Select(d => d.Id));
        Assert.Equal([employee.Id], (await ListAsync(admin, $"defaultVehicleId={truck.Id}")).Select(d => d.Id));

        var update = await admin.PutAsJsonAsync($"{Drivers}/{employee.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "onLeave" }, employee.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Drivers}/{employee.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));

        // Kişi kaydı sürücü kartında kullanıldığı için silinemez.
        var person = (await admin.GetFromJsonAsync<RecordResponse>($"{People}/{ahmet}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{People}/{ahmet}?version={person.Version}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_driver_cards_are_rejected_and_drivers_are_never_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var person = await PersonAsync(admin, "Mehmet", "Demir", null);
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.ToString() });

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("personId"));
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject { ["personId"] = person.ToString() })));
        Assert.Equal(["Bu kişinin zaten bir sürücü kartı var."], duplicate.Errors["personId"]);

        var other = await PersonAsync(admin, "Can", "Er", null);
        var noCarrier = await ValidationProblemAsync(await admin.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = other.ToString(), ["driverType"] = "subcontracted",
        })));
        Assert.Equal(["Taşeron sürücüde taşıyıcı firma seçilmelidir."], noCarrier.Errors["carrierPartyId"]);
        var customer = await PartyAsync(admin, "Müşteri Ltd.", "musteri");
        var wrongRole = await ValidationProblemAsync(await admin.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = other.ToString(), ["driverType"] = "subcontracted", ["carrierPartyId"] = customer.ToString(),
        })));
        Assert.Contains("Taşıyıcı", wrongRole.Errors["carrierPartyId"].Single());

        // Dorse ve pasif araç sürücüye atanamaz; bilinmeyen araç bulunamaz.
        var trailer = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34DRS01", ["kindKey"] = "SemiTrailer" });
        var passive = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34PSF01", ["kindKey"] = "Truck", ["status"] = "passive" });
        foreach (var (vehicleId, expected) in new[] { (trailer.Id, "Dorse"), (passive.Id, "Pasif araç"), (Guid.NewGuid(), "Araç bulunamadı") })
        {
            var rejected = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Drivers}/{driver.Id}",
                new RecordWriteRequest(new JsonObject { ["defaultVehicleId"] = vehicleId.ToString() }, driver.Version)));
            Assert.StartsWith(expected, rejected.Errors["defaultVehicleId"].Single());
        }

        // Filo kapalıyken araç seçilemez ama sürücü kartı çalışmaya devam eder (yumuşak bağımlılık).
        await factory.SetModuleAsync(s.TenantId, "fleet", enabled: false);
        var fleetClosed = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Drivers}/{driver.Id}",
            new RecordWriteRequest(new JsonObject { ["defaultVehicleId"] = Guid.NewGuid().ToString() }, driver.Version)));
        Assert.Contains("Filo modülü", fleetClosed.Errors["defaultVehicleId"].Single());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Drivers}/{driver.Id}",
            new RecordWriteRequest(new JsonObject { ["notes"] = "Filo kapalıyken güncellendi" }, driver.Version))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Drivers}/{driver.Id}?version={driver.Version + 1}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_people_vehicles_and_drivers_cannot_be_used_or_seen()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var personA = await PersonAsync(adminA, "Ali", "Kaya", null);
        var vehicleA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "06ISO01", ["kindKey"] = "Truck" });
        var driverA = await PostAsync(adminA, Drivers, new JsonObject { ["personId"] = personA.ToString() });

        var foreignPerson = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject { ["personId"] = personA.ToString() })));
        Assert.Equal(["Kişi bulunamadı."], foreignPerson.Errors["personId"]);

        var personB = await PersonAsync(adminB, "Ali", "Kaya", null); // aynı kişi adı başka tenant'ta serbest
        var foreignVehicle = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject
        {
            ["personId"] = personB.ToString(), ["defaultVehicleId"] = vehicleA.Id.ToString(),
        })));
        Assert.Equal(["Araç bulunamadı."], foreignVehicle.Errors["defaultVehicleId"]);

        Assert.Empty(await ListAsync(adminB, null));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Drivers}/{driverA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Drivers}/{driverA.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "passive" }, driverA.Version))).StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_write_outsiders_cannot_read_and_driver_writers_can_open_the_person()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var outsider = await LoginAsync(s.Outsider);
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = (await PersonAsync(admin, "Okan", "Tan", null)).ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Drivers)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Drivers}/{driver.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Drivers}/{driver.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "passive" }, driver.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(People,
            new RecordWriteRequest(new JsonObject { ["firstName"] = "Yetkisiz", ["lastName"] = "Kişi" }))).StatusCode);

        // Sürücü yazma izni, CRM izni olmadan sürücü için kişi açmaya ve kartı oluşturmaya yeter.
        var person = await PersonAsync(writer, "Selin", "Ak", "0555 000 00 00");
        Assert.Equal(HttpStatusCode.Created,
            (await writer.PostAsJsonAsync(Drivers, new RecordWriteRequest(new JsonObject { ["personId"] = person.ToString() }))).StatusCode);

        // Modül kapalı tenant'ta yönetici bile kullanamaz.
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Drivers)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // taşıyıcı firmayı kayıt ucundan açmak için (firma yazma izni CRM ile gelir)
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [DriversRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [DriversRead, DriversWrite], Scope.All, writer);
        return new Setup(tenant.Id, admin, reader, writer, outsider);
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

    private static async Task<Guid> PersonAsync(HttpClient client, string first, string last, string? phone) =>
        (await PostAsync(client, People, new JsonObject { ["firstName"] = first, ["lastName"] = last, ["phone"] = phone })).Id;

    private static async Task<Guid> PartyAsync(HttpClient client, string name, params string[] roles) =>
        (await PostAsync(client, Parties, new JsonObject
        {
            ["name"] = name,
            ["taxNumber"] = Guid.NewGuid().ToString("N")[..10],
            ["relationshipRoles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        })).Id;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? Drivers : $"{Drivers}?{query}"))!.Items;

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
