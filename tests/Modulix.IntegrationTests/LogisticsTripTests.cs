using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Modules.Logistics;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 5: sefer, durak zinciri, atama ve uygunluk. Mutlu yol (numara, sevkiyat ekleme → duraklar ve "Planlandı", ara durak, uygunluk,
/// plan onayı → sefere çıkış → varışlar → tamamlama, denetim kaydı), doğrulama (dorse/çekici türü, aynı sürücü, tarih sırası, sevkiyatsız ve kapasite
/// aşan sefer onaylanmaz, süresi dolan belge varsayılan uyarı ve ayarla engel, bakımdaki araç engel, hazır sefer değişmez, varışsız tamamlanmaz,
/// iptal sevkiyatları planlamaya döndürür, silme yok), tenant izolasyonu ve yetki.
/// </summary>
public class LogisticsTripTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Trips = "/api/records/logistics.trip";
    private const string Stops = "/api/records/logistics.trip-stop";
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Shipments = "/api/records/logistics.shipment";
    private const string Vehicles = "/api/records/fleet.vehicle";
    private const string Drivers = "/api/records/logistics.driver";
    private const string People = "/api/records/core.person";
    private const string Parties = "/api/records/core.party";
    private const string Credentials = "/api/records/core.credential";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_trip_collects_shipments_into_a_stop_chain_passes_the_check_and_runs_to_completion()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34SFR01", ["kindKey"] = "Truck", ["payloadKg"] = 26_000, ["palletCapacity"] = 33 });
        var driver = await DriverAsync(admin, "Ali", "Kaya");
        var toAnkara = await ShipmentAsync(manager, s.CustomerId, "İstanbul", "Ankara", 12_000);
        var toBursa = await ShipmentAsync(manager, s.CustomerId, "İstanbul", "Bursa", 8_000);

        var trip = await PostAsync(manager, Trips, new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(1)), ["plannedEnd"] = Iso(Today.AddDays(2)), ["vehicleId"] = truck.Id.ToString(), ["driverId"] = driver.ToString(),
        });
        Assert.Matches(@"^SF-\d{4}-\d{5}$", trip.Values["number"]!.GetValue<string>());
        Assert.Equal("planning", Status(trip));

        var planned = await AssignAsync(manager, trip, toAnkara);
        Assert.Equal(("planned", trip.Id.ToString()), (Status(planned), planned.Values["tripId"]!.GetValue<string>()));
        await AssignAsync(manager, trip, toBursa);
        await PostAsync(manager, Stops, new JsonObject { ["tripId"] = trip.Id.ToString(), ["stopType"] = "rest", ["city"] = "Bolu", ["sequence"] = 25 });

        var stops = await ListAsync(manager, Stops, $"tripId={trip.Id}");
        Assert.Equal([10, 20, 25, 30, 40], stops.Select(x => x.Values["sequence"]!.GetValue<int>()));
        Assert.Equal(["loading", "unloading", "rest", "loading", "unloading"], stops.Select(x => x.Values["stopType"]!.GetValue<string>()));
        Assert.Equal(["İstanbul", "Ankara", "Bolu", "İstanbul", "Bursa"], stops.Select(x => x.Values["city"]!.GetValue<string>()));
        Assert.Equal(2, stops.Count(x => x.Values["shipmentId"]?.GetValue<string>() == toAnkara.Id.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Stops}/{stops[0].Id}?version={stops[0].Version}")).StatusCode); // sevkiyat durağı elle silinmez

        var check = (await manager.GetFromJsonAsync<TripCheckDto>($"/api/logistics/trips/{trip.Id}/check"))!;
        Assert.True(check.CanConfirm, string.Join(" | ", check.Issues.Select(i => i.Message)));
        Assert.Equal((2, 5, 20_000m, 26_000m), (check.Load.ShipmentCount, check.Load.StopCount, check.Load.WeightKg, check.Load.PayloadKg));

        trip = await ActAsync(manager, trip, "confirm");
        Assert.Equal("ready", Status(trip));
        trip = await ActAsync(manager, trip, "start");
        Assert.Equal("inProgress", Status(trip));

        foreach (var stop in await ListAsync(manager, Stops, $"tripId={trip.Id}"))
        {
            var arrived = await manager.PutAsJsonAsync($"{Stops}/{stop.Id}",
                new RecordWriteRequest(new JsonObject { ["arrivedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o") }, stop.Version));
            Assert.Equal(HttpStatusCode.OK, arrived.StatusCode);
        }

        trip = await ActAsync(manager, trip, "complete");
        Assert.Equal("completed", Status(trip));
        Assert.Contains(await ListAsync(manager, Trips, "status=completed"), t => t.Id == trip.Id);
        Assert.Equal([trip.Id], (await ListAsync(manager, Trips, $"driverId={driver}")).Select(t => t.Id));

        var history = (await manager.GetFromJsonAsync<AuditPageResponse>($"{Trips}/{trip.Id}/history"))!;
        Assert.True(history.Items.Count(h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status")) >= 3);
    }

    [Fact]
    public async Task Invalid_assignments_and_unfit_trips_are_rejected_and_cancelling_releases_the_shipments()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        var truck = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34KAP01", ["kindKey"] = "Truck", ["payloadKg"] = 10_000 });
        var trailer = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34DRS09", ["kindKey"] = "SemiTrailer" });
        var driver = await DriverAsync(admin, "Veli", "Demir");

        // Alan kuralları: dorse çekici olamaz, çekici dorse olamaz, aynı sürücü iki kez seçilmez, bitiş başlangıçtan önce olamaz.
        var wrong = await ValidationProblemAsync(await manager.PostAsJsonAsync(Trips, new RecordWriteRequest(new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(3)), ["plannedEnd"] = Iso(Today.AddDays(2)), ["vehicleId"] = trailer.Id.ToString(), ["trailerId"] = truck.Id.ToString(),
            ["driverId"] = driver.ToString(), ["coDriverId"] = driver.ToString(),
        })));
        Assert.True(new[] { "vehicleId", "trailerId", "coDriverId", "plannedEnd" }.All(wrong.Errors.ContainsKey), string.Join(",", wrong.Errors.Keys));

        var trip = await PostAsync(manager, Trips, new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(1)), ["vehicleId"] = truck.Id.ToString(), ["trailerId"] = trailer.Id.ToString(), ["driverId"] = driver.ToString(),
        });
        Assert.Contains("en az bir sevkiyat", await ConflictAsync(manager, trip, "confirm"));

        // Kapasite aşımı engeldir; sevkiyat çıkarılınca durakları kalkar ve planlamaya döner.
        var heavy = await AssignAsync(manager, trip, await ShipmentAsync(manager, s.CustomerId, "İzmir", "Manisa", 12_000));
        Assert.Contains(Issues(await CheckAsync(manager, trip)), i => i.Code == "overWeight" && i.Blocking);
        Assert.Contains("kapasitesini", await ConflictAsync(manager, trip, "confirm"));
        var unassign = await manager.DeleteAsync($"/api/logistics/trips/{trip.Id}/shipments/{heavy.Id}");
        Assert.True(unassign.StatusCode == HttpStatusCode.OK, await unassign.Content.ReadAsStringAsync());
        var released = await unassign.Content.ReadFromJsonAsync<RecordResponse>();
        Assert.Equal("pending", Status(released!));
        Assert.Null(released!.Values["tripId"]);
        Assert.Empty(await ListAsync(manager, Stops, $"tripId={trip.Id}"));

        // Süresi dolan belge varsayılan olarak uyarıdır: plan onaylanır, uyarı seferde saklanır.
        var light = await AssignAsync(manager, trip, await ShipmentAsync(manager, s.CustomerId, "İzmir", "Aydın", 6_000));
        await PostAsync(admin, Credentials, new JsonObject
        {
            ["ownerEntityKey"] = "fleet.vehicle", ["ownerId"] = truck.Id.ToString(), ["typeKey"] = "Inspection", ["validTo"] = Iso(Today.AddDays(-1)),
        });
        Assert.Contains(Issues(await CheckAsync(manager, trip)), i => i.Code == "credential" && !i.Blocking && i.Message.Contains("34KAP01"));
        trip = await ActAsync(manager, await GetAsync(manager, $"{Trips}/{trip.Id}"), "confirm");
        Assert.StartsWith("Uyarılarla onaylandı", trip.Values["statusReason"]!.GetValue<string>());

        // Hazır sefer değişmez: form, durak, sevkiyat ve varış reddedilir; planlamaya geri alınabilir.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Trips}/{trip.Id}",
            new RecordWriteRequest(new JsonObject { ["notes"] = "x" }, trip.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(Stops,
            new RecordWriteRequest(new JsonObject { ["tripId"] = trip.Id.ToString(), ["city"] = "Uşak" }))).StatusCode);
        var stop = (await ListAsync(manager, Stops, $"tripId={trip.Id}"))[0];
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Stops}/{stop.Id}",
            new RecordWriteRequest(new JsonObject { ["arrivedAt"] = DateTimeOffset.UtcNow.ToString("o") }, stop.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Stops}/{stop.Id}?version={stop.Version}")).StatusCode);
        trip = await ActAsync(manager, trip, "reopen");

        // Firma ayarıyla belge kuralı engele çevrilir.
        await SetSettingAsync(admin, LogisticsModule.SettingKeys.CredentialCheck, LogisticsModule.CredentialCheckModes.Block);
        Assert.Contains("34KAP01", await ConflictAsync(manager, trip, "confirm"));
        await SetSettingAsync(admin, LogisticsModule.SettingKeys.CredentialCheck, LogisticsModule.CredentialCheckModes.Warn);

        // İptal gerekçe ister; seferdeki planlanmış sevkiyat "Planlama bekliyor"a döner ve seferden ayrılır. Sefer silinmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/actions",
            new RunLogisticsActionRequest("cancel", trip.Version, null))).StatusCode);
        trip = await ActAsync(manager, trip, "cancel", "Araç arızalandı");
        Assert.Equal(("cancelled", "Araç arızalandı"), (Status(trip), trip.Values["statusReason"]!.GetValue<string>()));
        var back = await GetAsync(manager, $"{Shipments}/{light.Id}");
        Assert.Equal("pending", Status(back));
        Assert.Null(back.Values["tripId"]);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Trips}/{trip.Id}?version={trip.Version}")).StatusCode);

        // Yeni sefer: varış girilmeden tamamlanmaz, yoldaki sefer iptal edilmez; bakımdaki araç engeldir.
        var second = await PostAsync(manager, Trips, new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)), ["vehicleId"] = truck.Id.ToString(), ["driverId"] = driver.ToString() });
        await AssignAsync(manager, second, back);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/trips/{second.Id}/shipments",
            new AssignShipmentRequest(back.Id))).StatusCode); // zaten planlanmış sevkiyat ikinci kez eklenmez
        second = await ActAsync(manager, await ActAsync(manager, await GetAsync(manager, $"{Trips}/{second.Id}"), "confirm"), "start");
        Assert.Contains("varış", await ConflictAsync(manager, second, "complete"));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/trips/{second.Id}/actions",
            new RunLogisticsActionRequest("cancel", second.Version, "vazgeçildi"))).StatusCode);

        var broken = await PostAsync(admin, Vehicles, new JsonObject { ["plate"] = "34BKM01", ["kindKey"] = "Truck", ["status"] = "maintenance" });
        var third = await PostAsync(manager, Trips, new JsonObject { ["plannedStart"] = Iso(Today.AddDays(5)), ["vehicleId"] = broken.Id.ToString(), ["driverId"] = driver.ToString() });
        Assert.Contains(Issues(await CheckAsync(manager, third)), i => i.Code == "vehicleMaintenance" && i.Blocking);
        Assert.DoesNotContain(Issues(await CheckAsync(manager, third)), i => i.Code == "overlap"); // farklı tarih ve araç
    }

    [Fact]
    public async Task Another_tenants_trips_drivers_vehicles_and_shipments_cannot_be_seen_or_used()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        var truckA = await PostAsync(adminA, Vehicles, new JsonObject { ["plate"] = "06ISO09", ["kindKey"] = "Truck" });
        var driverA = await DriverAsync(adminA, "Ayşe", "Kılıç");
        var shipmentA = await ShipmentAsync(managerA, a.CustomerId, "Ankara", "Konya", 1_000);
        var tripA = await PostAsync(managerA, Trips, new JsonObject { ["plannedStart"] = Iso(Today), ["vehicleId"] = truckA.Id.ToString(), ["driverId"] = driverA.ToString() });
        await AssignAsync(managerA, tripA, shipmentA);

        Assert.DoesNotContain(await ListAsync(managerB, Trips, null), t => t.Id == tripA.Id);
        Assert.Empty(await ListAsync(managerB, Stops, $"tripId={tripA.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Trips}/{tripA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/logistics/trips/{tripA.Id}/check")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/trips/{tripA.Id}/actions",
            new RunLogisticsActionRequest("confirm", tripA.Version, null))).StatusCode);

        var foreignVehicle = await ValidationProblemAsync(await managerB.PostAsJsonAsync(Trips,
            new RecordWriteRequest(new JsonObject { ["plannedStart"] = Iso(Today), ["vehicleId"] = truckA.Id.ToString() })));
        Assert.Equal(["Araç bulunamadı."], foreignVehicle.Errors["vehicleId"]);
        var foreignDriver = await ValidationProblemAsync(await managerB.PostAsJsonAsync(Trips,
            new RecordWriteRequest(new JsonObject { ["plannedStart"] = Iso(Today), ["driverId"] = driverA.ToString() })));
        Assert.Equal(["Sürücü bulunamadı."], foreignDriver.Errors["driverId"]);

        var tripB = await PostAsync(managerB, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/trips/{tripB.Id}/shipments",
            new AssignShipmentRequest(shipmentA.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await managerB.PostAsJsonAsync(Stops,
            new RecordWriteRequest(new JsonObject { ["tripId"] = tripA.Id.ToString(), ["city"] = "Sivas" }))).StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_plan_or_run_trips_and_users_without_trip_permissions_cannot_see_them()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        using var shipper = await LoginAsync(s.Shipper);
        using var outsider = await LoginAsync(s.Outsider);
        var shipment = await ShipmentAsync(manager, s.CustomerId, "Kayseri", "Sivas", 2_000);
        var trip = await PostAsync(manager, Trips, new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)) });

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Trips}/{trip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/logistics/trips/{trip.Id}/check")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/trips/{trip.Id}/actions"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Trips, new RecordWriteRequest(new JsonObject { ["plannedStart"] = Iso(Today) }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/actions",
            new RunLogisticsActionRequest("cancel", trip.Version, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Stops,
            new RecordWriteRequest(new JsonObject { ["tripId"] = trip.Id.ToString(), ["city"] = "Sivas" }))).StatusCode);

        // Sevkiyat yazabilen ama sefer izni olmayan kullanıcı seferi göremez ve sevkiyatı sefere ekleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await shipper.GetAsync(Trips)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shipper.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id))).StatusCode);
        Assert.Equal("pending", Status(await GetAsync(manager, $"{Shipments}/{shipment.Id}")));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Trips)).StatusCode);

        await factory.SetModuleAsync(s.TenantId, "logistics", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/logistics/trips/{trip.Id}/check")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, ModulixUser Admin, ModulixUser Manager, ModulixUser Reader, ModulixUser Shipper, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // müşteri firmayı kayıt ucundan açmak için
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var shipper = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id,
            [RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite, DriversRead], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead, ShipmentsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [ShipmentsRead, ShipmentsWrite], Scope.All, shipper);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant.Id, customer.Id, admin, manager, reader, shipper, outsider);
    }

    /// <summary>Talep açar ve onaylar; açılan sevkiyatı döner.</summary>
    private static async Task<RecordResponse> ShipmentAsync(HttpClient client, Guid customer, string from, string to, decimal kg)
    {
        var request = await PostAsync(client, Requests, new JsonObject
        {
            ["customerPartyId"] = customer.ToString(), ["pickupCity"] = from, ["deliveryCity"] = to, ["pickupDate"] = Iso(Today.AddDays(1)),
            ["cargoDescription"] = $"{from} yükü", ["weightKg"] = kg,
        });
        var response = await client.PostAsJsonAsync($"/api/logistics/requests/{request.Id}/actions", new RunLogisticsActionRequest("approve", request.Version, null));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var approved = (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
        return await GetAsync(client, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
    }

    private static async Task<Guid> DriverAsync(HttpClient admin, string first, string last)
    {
        var person = await PostAsync(admin, People, new JsonObject { ["firstName"] = first, ["lastName"] = last });
        return (await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.Id.ToString() })).Id;
    }

    private static async Task<RecordResponse> AssignAsync(HttpClient client, RecordResponse trip, RecordResponse shipment)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<TripCheckDto> CheckAsync(HttpClient client, RecordResponse trip) =>
        (await client.GetFromJsonAsync<TripCheckDto>($"/api/logistics/trips/{trip.Id}/check"))!;

    private static IReadOnlyList<TripCheckIssueDto> Issues(TripCheckDto check) => check.Issues;

    private static string Status(RecordResponse record) => record.Values["status"]!.GetValue<string>();

    private static async Task<RecordResponse> ActAsync(HttpClient client, RecordResponse trip, string action, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/actions", new RunLogisticsActionRequest(action, trip.Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    /// <summary>Eylem 409 döner; iletinin başlığı ve ayrıntısı birlikte (Türkçe karakterler JSON'da kaçışlı geldiği için gövde bir kez okunur).</summary>
    private static async Task<string> ConflictAsync(HttpClient client, RecordResponse trip, string action)
    {
        var current = await GetAsync(client, $"{Trips}/{trip.Id}");
        var response = await client.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/actions", new RunLogisticsActionRequest(action, current.Version, null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        return $"{problem.Title} {problem.Detail}";
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(s => s.Key == key);
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, current.Version) }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? url : $"{url}?{query}"))!.Items;

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
