using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Modules.Fleet.Contracts;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Customization;
using Modulix.Shared.Records;
using static Modulix.Modules.Fleet.FleetModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 1a: Filo modülü araç kaydı. Mutlu yol (oluşturma, plaka biçimi, süzme, arama, güncelleme, denetim kaydı, taşeron araç,
/// <see cref="IVehicleDirectory"/>), doğrulama (zorunlu alan, plaka/şasi benzersizliği ve biçimi, taşıyıcı rolü, silme yasağı, sürüm),
/// tenant izolasyonu (A'nın aracı B'ye görünmez; plaka tenant başına benzersiz; dizin yalnızca kendi tenant'ını okur) ve yetki (modül
/// kapalı, izinsiz, yalnızca okuyan).
/// </summary>
public class FleetVehicleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Vehicles = "/api/records/fleet.vehicle";
    private const string Parties = "/api/records/core.party";

    [Fact]
    public async Task Vehicles_and_trailers_are_created_listed_updated_audited_and_read_through_the_directory()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var truck = await PostAsync(admin, Vehicles, new JsonObject
        {
            ["plate"] = "34 abc 123", ["kindKey"] = "Tractor", ["brand"] = "Mercedes", ["model"] = "Actros", ["modelYear"] = 2022,
            ["vin"] = "wdb9634031l123456", ["payloadKg"] = 24000.5m,
        });
        Assert.Equal("34ABC123", truck.Values["plate"]!.GetValue<string>());
        Assert.Equal("WDB9634031L123456", truck.Values["vin"]!.GetValue<string>());
        Assert.Equal("owned", truck.Values["ownership"]!.GetValue<string>());
        Assert.Equal("active", truck.Values["status"]!.GetValue<string>());

        var carrier = await PartyAsync(admin, "Anadolu Nakliyat", "tasiyici");
        var trailer = await PostAsync(admin, Vehicles, new JsonObject
        {
            ["plate"] = "06-DR-555", ["kindKey"] = "SemiTrailer", ["ownership"] = "subcontracted", ["carrierPartyId"] = carrier.ToString(),
            ["loadingMeters"] = 13.6m, ["palletCapacity"] = 33,
        });
        Assert.Equal("06DR555", trailer.Values["plate"]!.GetValue<string>());

        var subcontracted = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Vehicles}?ownership=subcontracted"))!;
        Assert.Equal([trailer.Id], subcontracted.Items.Select(i => i.Id));
        var searched = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Vehicles}?search=34 ABC"))!;
        Assert.Equal([truck.Id], searched.Items.Select(i => i.Id));
        var sorted = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Vehicles}?sort=-plate"))!;
        Assert.Equal([truck.Id, trailer.Id], sorted.Items.Select(i => i.Id));

        var update = await admin.PutAsJsonAsync($"{Vehicles}/{truck.Id}", new RecordWriteRequest(new JsonObject { ["status"] = "maintenance" }, truck.Version));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var maintenance = (await admin.GetFromJsonAsync<RecordPageResponse>($"{Vehicles}?status=maintenance"))!;
        Assert.Equal([truck.Id], maintenance.Items.Select(i => i.Id));

        var history = (await admin.GetFromJsonAsync<AuditPageResponse>($"{Vehicles}/{truck.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Created && h.UserId == s.Admin.Id);
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status"));

        // Diğer modüller aracı yalnızca dizinden okur; dorse "çekilen araç" kategorisindedir.
        using var scope = factory.OpenScope(s.TenantId);
        var directory = scope.ServiceProvider.GetRequiredService<IVehicleDirectory>();
        Assert.True(await directory.IsAvailableAsync(CancellationToken.None));
        var summaries = await directory.GetAsync([truck.Id, trailer.Id, Guid.NewGuid()], CancellationToken.None);
        Assert.Equal(2, summaries.Count);
        var trailerSummary = Assert.Single(summaries, v => v.Id == trailer.Id);
        Assert.Equal("towed", trailerSummary.KindCategory);
        Assert.Equal(carrier, trailerSummary.CarrierPartyId);
        Assert.False(Assert.Single(summaries, v => v.Id == truck.Id).IsActive); // bakımda
    }

    [Fact]
    public async Task Invalid_vehicles_are_rejected_and_vehicles_are_never_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("plate"));

        var vehicle = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "35XYZ01", ["vin"] = "WDB9634031L654321" });
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "35 xyz 01", ["vin"] = "wdb9634031l654321" })));
        Assert.Equal(["Bu plaka başka bir araçta kayıtlı."], duplicate.Errors["plate"]);
        Assert.Equal(["Bu şasi numarası başka bir araçta kayıtlı."], duplicate.Errors["vin"]);

        var badFormat = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34*ABC", ["vin"] = "KISA", ["ownership"] = "rented", ["kindKey"] = "Bisiklet" })));
        Assert.True(badFormat.Errors.ContainsKey("ownership"));
        var badValues = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34*ABC", ["vin"] = "KISA" })));
        Assert.True(badValues.Errors.ContainsKey("plate"));
        Assert.True(badValues.Errors.ContainsKey("vin"));
        var badKind = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34KND01", ["kindKey"] = "Bisiklet" })));
        Assert.True(badKind.Errors.ContainsKey("kindKey"));

        // Taşeron araçta taşıyıcı zorunludur ve firmanın "Taşıyıcı" rolü olmalıdır; diğer mülkiyette taşıyıcı seçilmez.
        var noCarrier = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34TSR01", ["ownership"] = "subcontracted" })));
        Assert.Equal(["Taşeron araçta taşıyıcı firma seçilmelidir."], noCarrier.Errors["carrierPartyId"]);
        var customer = await PartyAsync(admin, "Müşteri A.Ş.", "musteri");
        var wrongRole = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34TSR02", ["ownership"] = "subcontracted", ["carrierPartyId"] = customer.ToString() })));
        Assert.Contains("Taşıyıcı", wrongRole.Errors["carrierPartyId"].Single());
        var carrier = await PartyAsync(admin, "Taşıyıcı Ltd.", "tasiyici");
        var ownedWithCarrier = await ValidationProblemAsync(await admin.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "34TSR03", ["carrierPartyId"] = carrier.ToString() })));
        Assert.Equal(["Taşıyıcı firma yalnızca taşeron araçta seçilir."], ownedWithCarrier.Errors["carrierPartyId"]);

        // Araç silinmez (ADR-0038 K12); eski sürümle güncelleme çakışır.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Vehicles}/{vehicle.Id}?version={vehicle.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Vehicles}/{vehicle.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Vehicles}/{vehicle.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "passive" }, vehicle.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Vehicles}/{vehicle.Id}",
            new RecordWriteRequest(new JsonObject { ["brand"] = "Eski sürüm" }, vehicle.Version))).StatusCode);
    }

    [Fact]
    public async Task One_tenants_vehicles_are_invisible_to_another_and_plates_are_unique_per_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var vehicleA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "16ISO01" });
        var vehicleB = await PostAsync(adminB, Vehicles, new JsonObject { ["plate"] = "16ISO01" }); // aynı plaka başka tenant'ta serbest

        Assert.Equal([vehicleB.Id], (await adminB.GetFromJsonAsync<RecordPageResponse>(Vehicles))!.Items.Select(i => i.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Vehicles}/{vehicleA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Vehicles}/{vehicleA.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "passive" }, vehicleA.Version))).StatusCode);

        // Başka tenant'ın taşıyıcısı seçilemez.
        var carrierA = await PartyAsync(adminA, "A Taşıyıcı", "tasiyici");
        var foreignCarrier = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Vehicles,
            new RecordWriteRequest(new JsonObject { ["plate"] = "16ISO02", ["ownership"] = "subcontracted", ["carrierPartyId"] = carrierA.ToString() })));
        Assert.True(foreignCarrier.Errors.ContainsKey("carrierPartyId"));

        // Dizin yalnızca kendi tenant'ının aracını döndürür.
        using var scope = factory.OpenScope(b.TenantId);
        var directory = scope.ServiceProvider.GetRequiredService<IVehicleDirectory>();
        Assert.Equal([vehicleB.Id], (await directory.GetAsync([vehicleA.Id, vehicleB.Id], CancellationToken.None)).Select(v => v.Id));
    }

    [Fact]
    public async Task Readers_cannot_write_outsiders_cannot_read_and_closed_module_hides_vehicles_and_the_directory()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var outsider = await LoginAsync(s.Outsider);

        var vehicle = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "07YTK01" });

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Vehicles)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Vehicles}/{vehicle.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync(Vehicles, new RecordWriteRequest(new JsonObject { ["plate"] = "07YTK02" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Vehicles}/{vehicle.Id}",
            new RecordWriteRequest(new JsonObject { ["status"] = "passive" }, vehicle.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await writer.PostAsJsonAsync(Vehicles, new RecordWriteRequest(new JsonObject { ["plate"] = "07YTK03" }))).StatusCode);

        // Modülü kapalı tenant'ta yönetici bile kullanamaz; dizin boş döner (lojistik araç seçimini sunmaz ama çalışır).
        var closed = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(closed);
        await MakeAdministratorAsync(closed.Id, closedAdmin);
        using var closedClient = await LoginAsync(closedAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await closedClient.GetAsync(Vehicles)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await closedClient.PostAsJsonAsync(Vehicles, new RecordWriteRequest(new JsonObject { ["plate"] = "07KPL01" }))).StatusCode);

        await factory.SetModuleAsync(s.TenantId, "fleet", enabled: false);
        using var scope = factory.OpenScope(s.TenantId);
        var directory = scope.ServiceProvider.GetRequiredService<IVehicleDirectory>();
        Assert.False(await directory.IsAvailableAsync(CancellationToken.None));
        Assert.Empty(await directory.GetAsync([vehicle.Id], CancellationToken.None));
    }

    [Fact]
    public async Task Vehicle_form_layout_is_tabbed_with_plate_and_kind_on_the_first_tab()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var layout = Assert.Single((await admin.GetFromJsonAsync<List<FormLayoutDto>>("/api/form-layouts"))!, l => l.EntityKey == "fleet.vehicle");

        Assert.True(layout.Tabs.Count >= 2);
        var firstTabSections = layout.Sections.Where(section => section.Tab == layout.Tabs[0].Key).Select(section => section.Key).ToHashSet();
        foreach (var key in new[] { "plate", "kindKey" })
        {
            var field = Assert.Single(layout.Fields, f => f.Key == key);
            Assert.True(field.Required && field.Visible);
            Assert.Contains(field.Section, firstTabSections);
        }
    }

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // taşıyıcı firmayı kayıt ucundan açmak için (firma yazma izni CRM ile gelir)
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, [VehiclesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [VehiclesRead, VehiclesWrite], Scope.All, writer);
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

    private static async Task<Guid> PartyAsync(HttpClient client, string name, params string[] roles) =>
        (await PostAsync(client, Parties, new JsonObject
        {
            ["name"] = name,
            ["taxNumber"] = Guid.NewGuid().ToString("N")[..10],
            ["relationshipRoles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        })).Id;

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
