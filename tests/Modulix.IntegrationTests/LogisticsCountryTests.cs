using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0043 dilim 1: talep ve sevkiyatta yükleme/teslim ülkesi. Mutlu yol (ülkesiz talep TR olur, DE verilen talep onayda sevkiyata kopyalanır,
/// firma ayarı varsayılanı değiştirir, ülke ve "uluslararası" süzgeçleri), doğrulama (geçersiz kod 422, küçük harf büyüğe çevrilir), tenant izolasyonu
/// (başka tenant'ın varsayılan ülkesi ve kayıtları etkilemez) ve yetki (okuyan ülkeyi görür ama yazamaz, izinsiz 403).
/// </summary>
public class LogisticsCountryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Shipments = "/api/records/logistics.shipment";
    private const string Parties = "/api/records/core.party";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Country_defaults_to_TR_is_copied_to_the_shipment_and_filters_work()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        var domestic = await PostAsync(manager, Requests, Request(s.CustomerId, "Ankara", "İzmir")); // ülkesiz: firmanın varsayılanı
        Assert.Equal(("TR", "TR"), (Text(domestic, "pickupCountry"), Text(domestic, "deliveryCountry")));
        Assert.False(domestic.Values["international"]!.GetValue<bool>());

        var export = Request(s.CustomerId, "İstanbul", "Hamburg");
        export["deliveryCountry"] = "DE";
        var abroad = await PostAsync(manager, Requests, export);
        Assert.Equal(("TR", "DE"), (Text(abroad, "pickupCountry"), Text(abroad, "deliveryCountry")));
        Assert.True(abroad.Values["international"]!.GetValue<bool>());

        // Onayda sevkiyat ülkeleri talepten alır.
        var approved = await ActAsync(manager, abroad);
        var shipment = await GetAsync(manager, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
        Assert.Equal(("TR", "DE"), (Text(shipment, "pickupCountry"), Text(shipment, "deliveryCountry")));

        // Süzgeçler: ülke ve uluslararası.
        Assert.Equal([abroad.Id], (await ListAsync(manager, Requests, "deliveryCountry=DE")).Select(r => r.Id));
        Assert.Equal([abroad.Id], (await ListAsync(manager, Requests, "international=true")).Select(r => r.Id));
        Assert.Equal([domestic.Id], (await ListAsync(manager, Requests, "international=false")).Select(r => r.Id));
        Assert.Equal([shipment.Id], (await ListAsync(manager, Shipments, "international=true")).Select(r => r.Id));
    }

    [Fact]
    public async Task Dashboard_routes_carry_countries_and_count_open_international_shipments()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        // Yurt dışına giden sevkiyat sefere atanır (etkin olur, haritaya girer); iç sevkiyat planlama bekler (haritada yok).
        var export = Request(s.CustomerId, "İstanbul", "Hamburg");
        export["deliveryCountry"] = "DE";
        var approved = await ActAsync(manager, await PostAsync(manager, Requests, export));
        var shipment = await GetAsync(manager, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
        var trip = await PostAsync(manager, "/api/records/logistics.trip", new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)) });
        var assign = await manager.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.True(assign.StatusCode == HttpStatusCode.OK, await assign.Content.ReadAsStringAsync());
        await ActAsync(manager, await PostAsync(manager, Requests, Request(s.CustomerId, "Ankara", "İzmir")));

        var board = (await manager.GetFromJsonAsync<LogisticsDashboardDto>("/api/logistics/dashboard"))!;
        var route = Assert.Single(board.Shipments!.Routes);
        Assert.Equal(("İstanbul", "TR", "Hamburg", "DE", 1), (route.From, route.FromCountry, route.To, route.ToCountry, route.Count));
        Assert.Equal(1, board.Shipments.International);
    }

    [Fact]
    public async Task Invalid_country_codes_are_rejected_and_lower_case_is_normalized()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        foreach (var bad in new[] { "ZZ", "Turkiye", "T", "12" })
        {
            var values = Request(s.CustomerId, "Ankara", "İzmir");
            values["pickupCountry"] = bad;
            var problem = await ValidationProblemAsync(await manager.PostAsJsonAsync(Requests, new RecordWriteRequest(values)));
            Assert.True(problem.Errors.ContainsKey("pickupCountry"), $"{bad}: {string.Join(',', problem.Errors.Keys)}");
        }

        var lower = Request(s.CustomerId, "Sofya", "İstanbul");
        lower["pickupCountry"] = "bg";
        var created = await PostAsync(manager, Requests, lower);
        Assert.Equal("BG", Text(created, "pickupCountry"));

        // Boş gönderilen ülke de varsayılana döner, kayıt bozulmaz.
        var cleared = await PutAsync(manager, Requests, created, new JsonObject { ["pickupCountry"] = null });
        Assert.Equal("TR", Text(cleared, "pickupCountry"));
    }

    [Fact]
    public async Task Default_country_is_per_company_and_another_tenants_records_are_not_affected()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);

        // A firmasının varsayılan ülkesi Bulgaristan.
        var current = (await adminA.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(x => x.Key == "logistics.default-country");
        var save = await adminA.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { ["logistics.default-country"] = new("BG", current.Version) }));
        Assert.True(save.StatusCode == HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var requestA = await PostAsync(managerA, Requests, Request(a.CustomerId, "Sofya", "Varna"));
        Assert.Equal(("BG", "BG"), (Text(requestA, "pickupCountry"), Text(requestA, "deliveryCountry")));

        // B firması etkilenmez: kendi varsayılanı (TR) geçerli, A'nın kaydı görünmez.
        var requestB = await PostAsync(managerB, Requests, Request(b.CustomerId, "Bursa", "Konya"));
        Assert.Equal(("TR", "TR"), (Text(requestB, "pickupCountry"), Text(requestB, "deliveryCountry")));
        Assert.Empty(await ListAsync(managerB, Requests, "pickupCountry=BG"));
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Requests}/{requestA.Id}")).StatusCode);

        // Geçersiz ayar değeri TR sayılır (kayıt açılır, 500 olmaz).
        current = (await adminA.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(x => x.Key == "logistics.default-country");
        await adminA.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { ["logistics.default-country"] = new("ZZ", current.Version) }));
        var fallback = await PostAsync(managerA, Requests, Request(a.CustomerId, "Ankara", "Mersin"));
        Assert.Equal("TR", Text(fallback, "pickupCountry"));
    }

    [Fact]
    public async Task Readers_see_the_country_but_cannot_write_it_and_outsiders_get_403()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        using var outsider = await LoginAsync(s.Outsider);
        var values = Request(s.CustomerId, "İstanbul", "Hamburg");
        values["deliveryCountry"] = "DE";
        var request = await PostAsync(manager, Requests, values);

        var seen = await GetAsync(reader, $"{Requests}/{request.Id}");
        Assert.Equal("DE", Text(seen, "deliveryCountry"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Requests}/{request.Id}",
            new RecordWriteRequest(new JsonObject { ["deliveryCountry"] = "FR" }, request.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Requests, new RecordWriteRequest(Request(s.CustomerId, "A", "B")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Requests}?deliveryCountry=DE")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, ModulixUser Admin, ModulixUser Manager, ModulixUser Reader, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // müşteri firmayı kayıt ucundan açmak için
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, ShipmentsRead], Scope.All, reader);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant.Id, customer.Id, admin, manager, reader, outsider);
    }

    private static JsonObject Request(Guid customer, string from, string to) => new()
    {
        ["customerPartyId"] = customer.ToString(),
        ["pickupCity"] = from,
        ["deliveryCity"] = to,
        ["pickupDate"] = Iso(Today.AddDays(1)),
        ["deliveryBy"] = Iso(Today.AddDays(3)),
        ["cargoDescription"] = "Palet",
        ["weightKg"] = 5_000,
    };

    private static string Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>() ?? string.Empty;

    private static async Task<RecordResponse> ActAsync(HttpClient client, RecordResponse request)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/requests/{request.Id}/actions", new RunLogisticsActionRequest("approve", request.Version, null));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>($"{url}?{query}"))!.Items;

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

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

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}

/// <summary>
/// ADR-0043 K3: sunucu doğrulamasının kod kümesi (<see cref="IsoCountryCodes"/>) ile arayüz kataloğu (<see cref="CountryCatalog"/>) aynı olmalıdır
/// (proje referans kuralı iki projenin birbirini görmesine izin vermez, bu yüzden iki liste vardır). Veritabanı gerekmez.
/// </summary>
public class CountryCodeParityTests
{
    [Fact]
    public void Server_codes_and_ui_catalog_are_identical_and_complete()
    {
        Assert.Equal(249, IsoCountryCodes.All.Count);
        Assert.Equal(IsoCountryCodes.All.Order(StringComparer.Ordinal), CountryCatalog.All.Select(c => c.Code).Order(StringComparer.Ordinal));
        Assert.All(CountryCatalog.All, c =>
        {
            Assert.Equal(2, c.Code.Length);
            Assert.False(string.IsNullOrWhiteSpace(c.NameTr));
            Assert.False(string.IsNullOrWhiteSpace(c.NameEn));
        });
        Assert.Contains("TR", IsoCountryCodes.All);
        Assert.False(IsoCountryCodes.IsValid("ZZ"));
        Assert.False(IsoCountryCodes.IsValid("tr"));
    }
}
