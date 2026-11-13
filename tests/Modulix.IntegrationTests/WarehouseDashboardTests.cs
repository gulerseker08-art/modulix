using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using Modulix.Shared.Warehouse;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Depo ve antrepo panosu (<c>GET /api/warehouse/dashboard</c>): sayaçlar, doluluk, günlük hareketler, beyanname durumları ve uyarılar defterden ve kayıtlardan
/// doğru hesaplanır; dönem ve tesis doğrulanır; başka tenant'ın verisi ve tesisi görünmez; her bölüm kendi okuma iznine bağlıdır.
/// </summary>
public class WarehouseDashboardTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Dashboard = "/api/warehouse/dashboard";
    private const string Movements = "/api/records/warehouse.stock-movement";
    private const string Locations = "/api/records/warehouse.location";
    private const string Parties = "/api/records/core.party";

    [Fact]
    public async Task Dashboard_counts_occupancy_movements_declarations_and_alerts_from_the_records()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);

        // A'ya giriş, B'ye transfer, A'dan çıkış: A ve B dolu, C boş; pasife alınmış D'de stok kalır.
        await PostAsync(admin, Movements, Move("Receipt", w.Lot, to: w.A, quantity: 10));
        await PostAsync(admin, Movements, Move("Receipt", w.Lot, to: w.D, quantity: 5));
        await PostAsync(admin, Movements, Move("Transfer", w.Lot, from: w.A, to: w.B, quantity: 4));
        await PostAsync(admin, Movements, Move("Issue", w.Lot, from: w.A, quantity: 1));
        var d = (await admin.GetFromJsonAsync<RecordResponse>($"{Locations}/{w.D}"))!;
        await PutAsync(admin, Locations, d, new JsonObject { ["isActive"] = false });

        // Beyanname: biri taslakta 10 gündür (eski), biri yeni taslak, biri tescilli. Vekâlet 10 gün sonra bitiyor, diğeri süresiz.
        var importer = await PartyAsync(admin, "İthalatçı A.Ş.", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Müşavir Ltd.", "gumruk-musaviri");
        var stale = await PostAsync(admin, "/api/records/warehouse.customs-declaration", Declaration("ESKI-1", importer, "Draft"));
        await PostAsync(admin, "/api/records/warehouse.customs-declaration", Declaration("YENI-1", importer, "Draft"));
        await PostAsync(admin, "/api/records/warehouse.customs-declaration", Declaration("TESCIL-1", importer, "Registered"));
        await factory.ScalarAsync<int>(s.TenantId,
            "UPDATE [warehouse].[CustomsDeclarations] SET [CreatedAt] = DATEADD(day, -10, SYSUTCDATETIME()) WHERE [Id] = @id; SELECT 1;", ("@id", stale.Id));
        var soon = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        await PostAsync(admin, "/api/records/warehouse.power-of-attorney", Attorney(importer, broker, soon.ToString("yyyy-MM-dd")));
        await PostAsync(admin, "/api/records/warehouse.power-of-attorney", Attorney(importer, broker, null));

        var dash = await GetAsync(admin, $"{Dashboard}?days=7");
        Assert.Equal(7, dash.Days);
        Assert.Equal(new WarehouseFacilityCountsDto(1, 1), dash.Facilities);

        // Doluluk: uç ve etkin lokasyonlar (A, B, C) üzerinden; pasif D sayılmaz ama "pasifte stok var" uyarısı verir.
        var occupancy = dash.Occupancy!;
        Assert.Equal((3, 2, 1), (occupancy.Locations, occupancy.Occupied, occupancy.PassiveWithStock));
        var row = Assert.Single(occupancy.ByFacility);
        Assert.Equal((w.Facility, 3, 2, true), (row.FacilityId, row.Locations, row.Occupied, row.CustomsEnabled));

        Assert.Equal(new WarehouseStockItemCountsDto(1, 1), dash.StockItems);

        // Hareketler: 7 günlük seri bugünle biter; kategoriye göre sayılır (adet, miktar değil).
        var movements = dash.Movements!;
        Assert.Equal(7, movements.Daily.Count);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), movements.Daily[^1].Day);
        Assert.Equal((2, 1, 1), (movements.Daily[^1].Inbound, movements.Daily[^1].Outbound, movements.Daily[^1].Transfer));
        Assert.Equal((2, 1, 1), (movements.Inbound, movements.Outbound, movements.Transfer));
        Assert.Equal(4, movements.Recent.Count);
        var latest = movements.Recent[0];
        Assert.Equal(("Issue", "Çıkış", "out", 1m), (latest.TypeKey, latest.TypeLabel, latest.Category, latest.Quantity));
        Assert.Equal((w.ItemCode, w.LotNo, "A1", (string?)null), (latest.StockItemCode, latest.LotNo, latest.FromLocation, latest.ToLocation));

        // Beyanname durumları firmanın listesinden (ad + kategori); eski taslak uyarısı en eski kaydı gösterir.
        var declarations = dash.Declarations!;
        Assert.Equal(2, declarations.ByStatus.Single(x => x.Category == "draft").Count);
        Assert.Equal(1, declarations.ByStatus.Single(x => x.Category == "registered").Count);
        Assert.Equal(0, declarations.ByStatus.Single(x => x.Category == "cancelled").Count);
        Assert.Equal(1, declarations.StaleDrafts);
        Assert.Equal("ESKI-1", Assert.Single(declarations.StaleSample).RegistrationNo);

        Assert.Equal(new WarehouseAttorneyAlertDto(1, 30, soon), dash.Attorneys);

        // Tesis süzgeci: hareketi ve lokasyonu olmayan ikinci tesiste doluluk ve hareket sıfırdır; beyanname tesise bağlı değildir.
        var empty = await PostAsync(admin, "/api/records/warehouse.facility", new JsonObject { ["code"] = $"BOS{w.Suffix}", ["name"] = "Boş tesis" });
        var filtered = await GetAsync(admin, $"{Dashboard}?facilityId={empty.Id}");
        Assert.Equal(empty.Id, filtered.FacilityId);
        Assert.Equal(14, filtered.Days);
        Assert.Equal((0, 0), (filtered.Occupancy!.Locations, filtered.Movements!.Inbound));
        Assert.Empty(filtered.Movements.Recent);
        Assert.Equal(3, filtered.Declarations!.ByStatus.Sum(x => x.Count));
        var own = await GetAsync(admin, $"{Dashboard}?facilityId={w.Facility}");
        Assert.Equal((3, 2), (own.Occupancy!.Locations, own.Movements!.Inbound));
    }

    [Fact]
    public async Task Period_and_facility_are_validated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        foreach (var days in new[] { "0", "5", "31", "365" })
        {
            var response = await admin.GetAsync($"{Dashboard}?days={days}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("days", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Dashboard}?facilityId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Dashboard}?facilityId=abc")).StatusCode);

        // Boş tenant: sayılar sıfır, seri tam uzunlukta, liste boş.
        var dash = await GetAsync(admin, $"{Dashboard}?days=30");
        Assert.Equal(30, dash.Movements!.Daily.Count);
        Assert.All(dash.Movements.Daily, x => Assert.Equal(0, x.Inbound + x.Outbound + x.Transfer + x.Adjustment));
        Assert.Equal((0, 0, 0), (dash.Occupancy!.Locations, dash.Facilities!.Active, dash.Declarations!.StaleDrafts));
        Assert.Equal(new WarehouseAttorneyAlertDto(0, 30, null), dash.Attorneys);
    }

    [Fact]
    public async Task Another_tenants_records_and_facility_stay_invisible()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var wA = await WorldAsync(adminA);
        await PostAsync(adminA, Movements, Move("Receipt", wA.Lot, to: wA.A, quantity: 10));

        var dashB = await GetAsync(adminB, Dashboard);
        Assert.Equal(new WarehouseFacilityCountsDto(0, 0), dashB.Facilities);
        Assert.Equal((0, 0), (dashB.Occupancy!.Locations, dashB.Movements!.Inbound));
        Assert.Empty(dashB.Movements.Recent);
        Assert.Empty(dashB.Occupancy.ByFacility);
        Assert.Equal(new WarehouseStockItemCountsDto(0, 0), dashB.StockItems);

        // A'nın tesisi B için yoktur: varlığı belli edilmez.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Dashboard}?facilityId={wA.Facility}")).StatusCode);
        Assert.Equal(1, (await GetAsync(adminA, $"{Dashboard}?facilityId={wA.Facility}")).Movements!.Inbound);
    }

    [Fact]
    public async Task Each_section_requires_its_own_read_permission_and_the_module_must_be_enabled()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);
        await PostAsync(admin, Movements, Move("Receipt", w.Lot, to: w.A, quantity: 10));

        // Yalnızca tesis okuyan: tesis sayaçları var, diğer bölümler sunucuda boş gelir.
        using var facilitiesOnly = await LoginAsync(s.FacilitiesOnly);
        var minimal = await GetAsync(facilitiesOnly, Dashboard);
        Assert.NotNull(minimal.Facilities);
        Assert.Null(minimal.Occupancy);
        Assert.Null(minimal.StockItems);
        Assert.Null(minimal.Declarations);
        Assert.Null(minimal.Movements);
        Assert.Null(minimal.Attorneys);

        // Hareket okuyan ama stok kartı, parti ve lokasyon okumayan: hareket var, kalem/parti/lokasyon adı gelmez; doluluk lokasyon izni ister.
        using var movementsReader = await LoginAsync(s.MovementsReader);
        var partial = await GetAsync(movementsReader, Dashboard);
        Assert.Null(partial.Occupancy);
        var recent = Assert.Single(partial.Movements!.Recent);
        Assert.Equal(((string?)null, (string?)null, (string?)null, (string?)null), (recent.StockItemCode, recent.LotNo, recent.FromLocation, recent.ToLocation));

        // Tesis okuma izni olmayan panoyu açamaz.
        using var stranger = await LoginAsync(s.NoFacilities);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Dashboard)).StatusCode);

        // Modül kapalıysa yönetici de açamaz; oturumsuz istek reddedilir.
        await factory.SetModuleAsync(s.TenantId, "warehouse", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Dashboard)).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Dashboard)).StatusCode);
    }

    private static JsonObject Move(string type, Guid lot, Guid? from = null, Guid? to = null, decimal? quantity = null)
    {
        var values = new JsonObject { ["typeKey"] = type, ["lotId"] = lot.ToString() };
        if (from is { } f) { values["fromLocationId"] = f.ToString(); }
        if (to is { } t) { values["toLocationId"] = t.ToString(); }
        if (quantity is { } q) { values["quantity"] = q; }
        return values;
    }

    private static JsonObject Declaration(string number, Guid importer, string status) => new()
    {
        ["registrationNo"] = number, ["registrationDate"] = "2026-03-10", ["importerPartyId"] = importer.ToString(), ["statusKey"] = status,
    };

    private static JsonObject Attorney(Guid importer, Guid broker, string? validTo) => new()
    {
        ["importerPartyId"] = importer.ToString(), ["brokerPartyId"] = broker.ToString(), ["validFrom"] = "2026-01-01", ["validTo"] = validTo,
    };

    private sealed record World(Guid Facility, Guid Lot, Guid A, Guid B, Guid C, Guid D, string ItemCode, string LotNo, string Suffix);

    /// <summary>Gümrük uzantılı bir tesis (tek seviye, dört lokasyon), bir ürün, bir malik ve bir parti kurar.</summary>
    private async Task<World> WorldAsync(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var facility = await PostAsync(admin, "/api/records/warehouse.facility", new JsonObject { ["code"] = $"T{suffix}", ["name"] = "Pano tesisi", ["customsEnabled"] = true });
        await PostAsync(admin, "/api/records/warehouse.location-level", new JsonObject
        {
            ["facilityId"] = facility.Id.ToString(), ["position"] = 1, ["key"] = "bin", ["name"] = "Göz",
            ["characterSet"] = "alphanumeric", ["minLength"] = 1, ["maxLength"] = 8,
        });
        async Task<Guid> Location(string value) =>
            (await PostAsync(admin, Locations, new JsonObject { ["facilityId"] = facility.Id.ToString(), ["value"] = value })).Id;
        var a = await Location("A1");
        var b = await Location("B1");
        var c = await Location("C1");
        var d = await Location("D1");
        var itemCode = $"URUN-{suffix}";
        var item = await PostAsync(admin, "/api/records/warehouse.stock-item", new JsonObject { ["code"] = itemCode, ["name"] = "Test ürünü" });
        var owner = await PartyAsync(admin, $"Malik {suffix}");
        var lotNo = $"LOT-{suffix}";
        var lot = await PostAsync(admin, "/api/records/warehouse.lot", new JsonObject
        {
            ["lotNo"] = lotNo, ["stockItemId"] = item.Id.ToString(), ["ownerPartyId"] = owner.ToString(),
        });
        return new World(facility.Id, lot.Id, a, b, c, d, itemCode, lotNo, suffix);
    }

    private sealed record Setup(Guid TenantId, ModulixUser Admin, ModulixUser FacilitiesOnly, ModulixUser MovementsReader, ModulixUser NoFacilities);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var facilitiesOnly = await factory.CreateUserAsync(tenant);
        var movementsReader = await factory.CreateUserAsync(tenant);
        var noFacilities = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead], Scope.All, facilitiesOnly);
        await factory.CreateRoleAsync(tenant.Id, [FacilitiesRead, MovementsRead], Scope.All, movementsReader);
        await factory.CreateRoleAsync(tenant.Id, [StockItemsRead, MovementsRead, DeclarationsRead], Scope.All, noFacilities);
        return new Setup(tenant.Id, admin, facilitiesOnly, movementsReader, noFacilities);
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

    private static async Task<WarehouseDashboardDto> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WarehouseDashboardDto>())!;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, RecordResponse record, JsonObject values)
    {
        var response = await client.PutAsJsonAsync($"{url}/{record.Id}", new RecordWriteRequest(values, record.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }
}
