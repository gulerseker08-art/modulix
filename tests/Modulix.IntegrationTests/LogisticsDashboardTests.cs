using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 9a: lojistik operasyon panosu. Mutlu yol (onay bekleyen talep, planlama bekleyen ve yarına kadar yüklenecek sevkiyat, geciken
/// sevkiyat, yoldaki sefer ve durak ilerlemesi, teslim başarısı ve günlük dağılım, süresi yaklaşan sürücü belgesi), boş firma (sıfırlar, veri yok),
/// tenant izolasyonu (başka firmanın kayıtları sayılmaz) ve yetki (bölümler okuma iznine göre gelir; hiçbir izni olmayan 403; modül kapalıyken 403).
/// </summary>
public class LogisticsDashboardTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/logistics/dashboard";
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Trips = "/api/records/logistics.trip";
    private const string Stops = "/api/records/logistics.trip-stop";
    private const string Drivers = "/api/records/logistics.driver";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Dashboard_counts_the_operation_from_request_to_delivery()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);

        await PostAsync(manager, Requests, Request(s.CustomerId, "Konya", "Antalya", Today.AddDays(2), null)); // onay bekliyor
        var soon = await ShipmentAsync(manager, s.CustomerId, "İstanbul", "Ankara", Today, null);              // yarına kadar yüklenecek, planlanmamış
        var late = await ShipmentAsync(manager, s.CustomerId, "İzmir", "Bursa", Today.AddDays(-3), Today.AddDays(-1)); // teslim günü geçmiş
        var road = await ShipmentAsync(manager, s.CustomerId, "Adana", "Mersin", Today, null);

        var carrier = await PostAsync(admin, "/api/records/core.party", new JsonObject
        {
            ["name"] = $"Taşıyıcı {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10], ["relationshipRoles"] = new JsonArray("tasiyici"),
        });
        var trip = await PostAsync(manager, Trips, new JsonObject { ["plannedStart"] = Iso(Today), ["carrierPartyId"] = carrier.Id.ToString() }); // taşeron sefer
        await AssignAsync(manager, trip, road);
        trip = await ActAsync(manager, "trips", await ActAsync(manager, "trips", await GetAsync(manager, $"{Trips}/{trip.Id}"), "confirm"), "start");
        var firstStop = (await ListAsync(manager, $"{Stops}?tripId={trip.Id}"))[0];
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"{Stops}/{firstStop.Id}",
            new RecordWriteRequest(new JsonObject { ["arrivedAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o") }, firstStop.Version))).StatusCode);

        // Yoldaki sevkiyat teslim edilir: son 14 günün bugününe bir teslim düşer.
        var shipment = await GetAsync(manager, $"/api/records/logistics.shipment/{road.Id}");
        shipment = await ActAsync(manager, "shipments", await ActAsync(manager, "shipments", shipment, "startLoading"), "depart");
        var delivered = await manager.PostAsJsonAsync($"/api/logistics/shipments/{road.Id}/delivery",
            new JsonObject { ["version"] = shipment.Version, ["outcome"] = "delivered", ["receivedBy"] = "Depo şefi" });
        Assert.True(delivered.StatusCode == HttpStatusCode.OK, await delivered.Content.ReadAsStringAsync());

        // Süresi 10 gün içinde dolan sürücü belgesi.
        var person = await PostAsync(admin, "/api/records/core.person", new JsonObject { ["firstName"] = "Eren", ["lastName"] = "Doğan" });
        var driver = await PostAsync(admin, Drivers, new JsonObject { ["personId"] = person.Id.ToString() });
        await PostAsync(admin, "/api/records/core.credential", new JsonObject
        {
            ["ownerEntityKey"] = "logistics.driver", ["ownerId"] = driver.Id.ToString(), ["typeKey"] = "Src", ["validTo"] = Iso(Today.AddDays(10)),
        });

        var board = (await manager.GetFromJsonAsync<LogisticsDashboardDto>(Url))!;
        Assert.Equal(1, board.Requests!.Awaiting);
        Assert.Equal(0, board.Requests.OldestWaitingDays);
        // Yükleme günü gelmiş ya da geçmiş planlanmamış sevkiyatlar da "yarına kadar yüklenecek" sayılır.
        Assert.Equal((2, 2, 1), (board.Shipments!.Pending, board.Shipments.PickupSoonUnplanned, board.Shipments.Late));
        Assert.Equal(late.Id, Assert.Single(board.Shipments.LateItems).Id);
        Assert.Equal(1, board.Shipments.LateItems[0].DaysLate);
        Assert.DoesNotContain(board.Shipments.LateItems, x => x.Id == soon.Id);
        Assert.Equal(["pending", "planned", "loading", "inTransit"], board.Shipments.ByStatus.Select(x => x.Status));

        Assert.Equal(1, board.Trips!.InProgress);
        var active = Assert.Single(board.Trips.Active);
        Assert.Equal((trip.Id, 2, 1, "Mersin"), (active.Id, active.Stops, active.Arrived, active.NextCity));

        Assert.Equal((1, 0, 0, 100m), (board.Deliveries!.Delivered, board.Deliveries.Partial, board.Deliveries.Failed, board.Deliveries.SuccessPercent));
        Assert.Equal(14, board.Deliveries.Days.Count);
        Assert.Equal(1, board.Deliveries.Days[^1].Delivered);

        var credential = Assert.Single(board.Credentials!.Items);
        Assert.Equal(("Eren Doğan", Today.AddDays(10)), (credential.DriverName, credential.ValidTo));
        Assert.Equal((0, 1), (board.Credentials.Expired, board.Credentials.ExpiringSoon));
    }

    [Fact]
    public async Task Active_shipments_are_grouped_into_routes_for_the_map_and_pending_ones_are_left_out()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        await ShipmentAsync(manager, s.CustomerId, "İstanbul", "Ankara", Today.AddDays(2), null); // planlama bekliyor: haritada yok
        var first = await ShipmentAsync(manager, s.CustomerId, "Kocaeli", "Mersin", Today.AddDays(2), null);
        var second = await ShipmentAsync(manager, s.CustomerId, "Kocaeli", "Mersin", Today.AddDays(2), null);
        var trip = await PostAsync(manager, Trips, new JsonObject { ["plannedStart"] = Iso(Today.AddDays(2)) });
        await AssignAsync(manager, trip, first);
        await AssignAsync(manager, trip, second);

        var route = Assert.Single((await manager.GetFromJsonAsync<LogisticsDashboardDto>(Url))!.Shipments!.Routes);
        Assert.Equal(("Kocaeli", "Mersin", 2), (route.From, route.To, route.Count));
    }

    [Fact]
    public async Task An_empty_company_gets_zeros_and_no_rates()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        var board = (await manager.GetFromJsonAsync<LogisticsDashboardDto>(Url))!;
        Assert.Equal(0, board.Requests!.Awaiting);
        Assert.Null(board.Requests.OldestWaitingDays);
        Assert.Equal(0, board.Shipments!.Pending);
        Assert.Empty(board.Shipments.LateItems);
        Assert.Empty(board.Trips!.Active);
        Assert.Null(board.Deliveries!.SuccessPercent);
        Assert.All(board.Deliveries.Days, x => Assert.Equal(0, x.Delivered + x.Partial + x.Failed));
        Assert.Empty(board.Credentials!.Items);
    }

    [Fact]
    public async Task Another_tenants_operation_is_not_counted()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        await PostAsync(managerA, Requests, Request(a.CustomerId, "Kars", "Erzurum", Today.AddDays(1), null));
        await ShipmentAsync(managerA, a.CustomerId, "Sivas", "Tokat", Today.AddDays(-4), Today.AddDays(-2));
        await PostAsync(managerA, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        var board = (await managerB.GetFromJsonAsync<LogisticsDashboardDto>(Url))!;
        Assert.Equal(0, board.Requests!.Awaiting);
        Assert.Equal((0, 0), (board.Shipments!.Pending, board.Shipments.Late));
        Assert.Empty(board.Trips!.Active);
    }

    [Fact]
    public async Task Sections_follow_read_permissions_and_users_without_any_get_403()
    {
        var s = await SetUpAsync();
        using var tripsOnly = await LoginAsync(s.TripsOnly);
        using var outsider = await LoginAsync(s.Outsider);
        using var manager = await LoginAsync(s.Manager);

        var board = (await tripsOnly.GetFromJsonAsync<LogisticsDashboardDto>(Url))!;
        Assert.NotNull(board.Trips);
        Assert.Null(board.Requests);
        Assert.Null(board.Shipments);
        Assert.Null(board.Deliveries);
        Assert.Null(board.Credentials);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Url)).StatusCode);

        await factory.SetModuleAsync(s.TenantId, "logistics", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(Url)).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, ModulixUser Admin, ModulixUser Manager, ModulixUser TripsOnly, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "fleet", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // müşteri firmayı kayıt ucundan açmak için
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var tripsOnly = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id,
            [RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite, DriversRead], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead], Scope.All, tripsOnly);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, "/api/records/core.party",
            new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant.Id, customer.Id, admin, manager, tripsOnly, outsider);
    }

    private static JsonObject Request(Guid customer, string from, string to, DateOnly pickup, DateOnly? deliveryBy) => new()
    {
        ["customerPartyId"] = customer.ToString(),
        ["pickupCity"] = from,
        ["deliveryCity"] = to,
        ["pickupDate"] = Iso(pickup),
        ["deliveryBy"] = deliveryBy is { } by ? Iso(by) : null,
        ["cargoDescription"] = $"{from} yükü",
        ["weightKg"] = 1_000,
    };

    /// <summary>Talep açar ve onaylar; açılan sevkiyatı döner.</summary>
    private static async Task<RecordResponse> ShipmentAsync(HttpClient client, Guid customer, string from, string to, DateOnly pickup, DateOnly? deliveryBy)
    {
        var request = await PostAsync(client, Requests, Request(customer, from, to, pickup, deliveryBy));
        var approved = await ActAsync(client, "requests", request, "approve");
        return await GetAsync(client, $"/api/records/logistics.shipment/{approved.Values["shipmentId"]!.GetValue<string>()}");
    }

    private static async Task AssignAsync(HttpClient client, RecordResponse trip, RecordResponse shipment)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, string kind, RecordResponse record, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, null));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(url))!.Items;

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
