using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 7a: sevkiyat navlunu ve taşeron sefer hakedişi (hassas alanlar). Mutlu yol (yetkili kullanıcı tutarı ve para birimini girer, büyük
/// harfe çevrilir, okur; talep onayı navlunsuz sevkiyat açar; hakediş taşeron seferde), doğrulama (tutar-para birimi birlikteliği, geçersiz kod ve tutar,
/// taşıyıcısız hakediş, yüklemesi başlamış sevkiyatın navlunu kilitli), tenant izolasyonu (başka tenant'ın navlunu görünmez) ve yetki (navlun izni
/// olmayan alanı görmez, yazamaz, denetim kaydında değeri görmez).
/// </summary>
public class LogisticsFreightTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Shipments = "/api/records/logistics.shipment";
    private const string Trips = "/api/records/logistics.trip";
    private const string Parties = "/api/records/core.party";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Finance_enters_freight_and_settlement_and_reads_them_back_while_request_approval_opens_a_shipment_without_freight()
    {
        var s = await SetUpAsync();
        using var finance = await LoginAsync(s.Finance);

        // Talep onayı navlunsuz sevkiyat açar (navlun talepte tutulmaz; yetkisiz onaylayanda kaybolmasın).
        var shipment = await ApprovedShipmentAsync(finance, s.CustomerId);
        Assert.Null(shipment.Values["freightAmount"]);
        Assert.Null(shipment.Values["freightCurrency"]);

        var updated = await PutAsync(finance, $"{Shipments}/{shipment.Id}", shipment.Version, new JsonObject { ["freightAmount"] = 18500.5m, ["freightCurrency"] = "try" });
        Assert.Equal(18500.5m, updated.Values["freightAmount"]!.GetValue<decimal>());
        Assert.Equal("TRY", updated.Values["freightCurrency"]!.GetValue<string>());
        Assert.Equal(18500.5m, (await GetAsync(finance, $"{Shipments}/{shipment.Id}")).Values["freightAmount"]!.GetValue<decimal>());

        // Tutar temizlenince para birimi de temizlenir (ikisi birlikte boş).
        var cleared = await PutAsync(finance, $"{Shipments}/{shipment.Id}", updated.Version, new JsonObject { ["freightAmount"] = null, ["freightCurrency"] = null });
        Assert.Null(cleared.Values["freightAmount"]);
        Assert.Null(cleared.Values["freightCurrency"]);

        // Hakediş taşeron seferde: taşıyıcı seçili, tutar ve para birimi.
        var trip = await PostAsync(finance, Trips, new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(2)), ["carrierPartyId"] = s.CarrierId.ToString(), ["settlementAmount"] = 12000, ["settlementCurrency"] = "eur",
        });
        Assert.Equal(12000m, trip.Values["settlementAmount"]!.GetValue<decimal>());
        Assert.Equal("EUR", trip.Values["settlementCurrency"]!.GetValue<string>());
    }

    [Fact]
    public async Task Amount_and_currency_go_together_and_invalid_values_settlement_without_carrier_and_started_shipments_are_rejected()
    {
        var s = await SetUpAsync();
        using var finance = await LoginAsync(s.Finance);
        var shipment = await ApprovedShipmentAsync(finance, s.CustomerId);

        async Task<ValidationProblemDetails> RejectedAsync(JsonObject values) => await ValidationProblemAsync(
            await finance.PutAsJsonAsync($"{Shipments}/{shipment.Id}", new RecordWriteRequest(values, shipment.Version)));

        Assert.Contains("freightCurrency", (await RejectedAsync(new JsonObject { ["freightAmount"] = 100 })).Errors.Keys);
        Assert.Contains("freightAmount", (await RejectedAsync(new JsonObject { ["freightCurrency"] = "TRY" })).Errors.Keys);
        Assert.Contains("freightCurrency", (await RejectedAsync(new JsonObject { ["freightAmount"] = 100, ["freightCurrency"] = "TR1" })).Errors.Keys);
        Assert.Contains("freightCurrency", (await RejectedAsync(new JsonObject { ["freightAmount"] = 100, ["freightCurrency"] = "TURK" })).Errors.Keys);
        Assert.Contains("freightAmount", (await RejectedAsync(new JsonObject { ["freightAmount"] = -1, ["freightCurrency"] = "TRY" })).Errors.Keys);
        Assert.Contains("freightAmount", (await RejectedAsync(new JsonObject { ["freightAmount"] = 10.123m, ["freightCurrency"] = "TRY" })).Errors.Keys);
        Assert.Null((await GetAsync(finance, $"{Shipments}/{shipment.Id}")).Values["freightAmount"]); // hatalı denemeler hiçbir şey yazmadı

        // Hakediş yalnızca taşıyıcı seçiliyken girilir.
        var noCarrier = await ValidationProblemAsync(await finance.PostAsJsonAsync(Trips, new RecordWriteRequest(new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(2)), ["settlementAmount"] = 5000, ["settlementCurrency"] = "TRY",
        })));
        Assert.Contains("settlementAmount", noCarrier.Errors.Keys);
        var noCurrency = await ValidationProblemAsync(await finance.PostAsJsonAsync(Trips, new RecordWriteRequest(new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(2)), ["carrierPartyId"] = s.CarrierId.ToString(), ["settlementAmount"] = 5000,
        })));
        Assert.Contains("settlementCurrency", noCurrency.Errors.Keys);

        // Yüklemesi başlamış sevkiyatın yük bilgisi gibi navlunu da kilitlidir (dilim 4 kuralı).
        var trip = await PostAsync(finance, Trips, new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)) });
        var assigned = await finance.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var planned = (await assigned.Content.ReadFromJsonAsync<RecordResponse>())!;
        var loading = await ActAsync(finance, "shipments", planned, "startLoading");
        Assert.Equal(HttpStatusCode.Conflict, (await finance.PutAsJsonAsync($"{Shipments}/{shipment.Id}",
            new RecordWriteRequest(new JsonObject { ["freightAmount"] = 100, ["freightCurrency"] = "TRY" }, loading.Version))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_freight_and_settlement_cannot_be_seen_or_changed()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var financeA = await LoginAsync(a.Finance);
        using var financeB = await LoginAsync(b.Finance);
        var shipment = await PutAsync(financeA, $"{Shipments}", await ApprovedShipmentAsync(financeA, a.CustomerId), new JsonObject { ["freightAmount"] = 9999, ["freightCurrency"] = "USD" });
        var trip = await PostAsync(financeA, Trips, new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(2)), ["carrierPartyId"] = a.CarrierId.ToString(), ["settlementAmount"] = 7000, ["settlementCurrency"] = "TRY",
        });

        Assert.Equal(HttpStatusCode.NotFound, (await financeB.GetAsync($"{Shipments}/{shipment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await financeB.GetAsync($"{Trips}/{trip.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await financeB.PutAsJsonAsync($"{Shipments}/{shipment.Id}",
            new RecordWriteRequest(new JsonObject { ["freightAmount"] = 1, ["freightCurrency"] = "TRY" }, shipment.Version))).StatusCode);
        Assert.Empty((await financeB.GetFromJsonAsync<RecordPageResponse>(Shipments))!.Items);
        Assert.Empty((await financeB.GetFromJsonAsync<RecordPageResponse>(Trips))!.Items);

        // Kayıt sahibi tenant değeri aynen görür.
        Assert.Equal(9999m, (await GetAsync(financeA, $"{Shipments}/{shipment.Id}")).Values["freightAmount"]!.GetValue<decimal>());
        Assert.Equal(7000m, (await GetAsync(financeA, $"{Trips}/{trip.Id}")).Values["settlementAmount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Without_the_freight_permission_amounts_are_not_returned_cannot_be_written_and_are_hidden_in_history()
    {
        var s = await SetUpAsync();
        using var finance = await LoginAsync(s.Finance);
        using var operations = await LoginAsync(s.Operations);
        using var outsider = await LoginAsync(s.Outsider);

        var shipment = await PutAsync(finance, $"{Shipments}", await ApprovedShipmentAsync(finance, s.CustomerId), new JsonObject { ["freightAmount"] = 18500.5m, ["freightCurrency"] = "TRY" });
        var trip = await PostAsync(finance, Trips, new JsonObject
        {
            ["plannedStart"] = Iso(Today.AddDays(2)), ["carrierPartyId"] = s.CarrierId.ToString(), ["settlementAmount"] = 12000, ["settlementCurrency"] = "EUR",
        });

        // Operasyon sevkiyatı ve seferi görür ama tutar alanları yanıtta yoktur (alan gönderilmez).
        var seenShipment = await GetAsync(operations, $"{Shipments}/{shipment.Id}");
        Assert.Equal("Ankara", seenShipment.Values["pickupCity"]!.GetValue<string>());
        Assert.False(seenShipment.Values.ContainsKey("freightAmount"));
        Assert.False(seenShipment.Values.ContainsKey("freightCurrency"));
        var seenTrip = await GetAsync(operations, $"{Trips}/{trip.Id}");
        Assert.False(seenTrip.Values.ContainsKey("settlementAmount"));
        Assert.False(seenTrip.Values.ContainsKey("settlementCurrency"));
        Assert.All((await operations.GetFromJsonAsync<RecordPageResponse>(Shipments))!.Items, i => Assert.False(i.Values.ContainsKey("freightAmount")));

        // Yazamaz: izinsiz kişinin navlun değiştirme denemesi başarılı olmaz ve değer değişmez.
        var attempt = await operations.PutAsJsonAsync($"{Shipments}/{shipment.Id}",
            new RecordWriteRequest(new JsonObject { ["freightAmount"] = 1, ["freightCurrency"] = "USD" }, shipment.Version));
        Assert.False(attempt.IsSuccessStatusCode, $"{(int)attempt.StatusCode}");
        var tripAttempt = await operations.PutAsJsonAsync($"{Trips}/{trip.Id}",
            new RecordWriteRequest(new JsonObject { ["settlementAmount"] = 1, ["settlementCurrency"] = "USD" }, trip.Version));
        Assert.False(tripAttempt.IsSuccessStatusCode, $"{(int)tripAttempt.StatusCode}");
        var unchanged = await GetAsync(finance, $"{Shipments}/{shipment.Id}");
        Assert.Equal(18500.5m, unchanged.Values["freightAmount"]!.GetValue<decimal>());
        Assert.Equal("TRY", unchanged.Values["freightCurrency"]!.GetValue<string>());

        // Navlunsuz alanları operasyon yine düzenler (izinsiz alan gönderilmedikçe).
        var edited = await PutAsync(operations, $"{Shipments}", unchanged, new JsonObject { ["notes"] = "Rampa 3" });
        Assert.Equal("Rampa 3", edited.Values["notes"]!.GetValue<string>());
        Assert.Equal(18500.5m, (await GetAsync(finance, $"{Shipments}/{shipment.Id}")).Values["freightAmount"]!.GetValue<decimal>()); // navlun korundu

        // Denetim kaydı: izni olmayan geçmişte tutarı göremez; izinli görür.
        var opsHistory = await (await operations.GetAsync($"{Shipments}/{shipment.Id}/history")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("18500", opsHistory);
        var financeHistory = (await finance.GetFromJsonAsync<AuditPageResponse>($"{Shipments}/{shipment.Id}/history"))!;
        Assert.NotEmpty(financeHistory.Items);

        // Kayıt iznisiz kişi sevkiyatı hiç görmez.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Shipments}/{shipment.Id}")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, Guid CarrierId, ModulixUser Finance, ModulixUser Operations, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // firmaları kayıt ucundan açmak için
        var admin = await factory.CreateUserAsync(tenant);
        var finance = await factory.CreateUserAsync(tenant);
        var operations = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        var operational = new[] { RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite, CarriersRead };
        await factory.CreateRoleAsync(tenant.Id, [.. operational, FreightRead], Scope.All, finance);
        await factory.CreateRoleAsync(tenant.Id, operational, Scope.All, operations);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        var carrier = await PostAsync(adminClient, Parties, new JsonObject
        {
            ["name"] = $"Taşıyıcı {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10], ["relationshipRoles"] = new JsonArray("tasiyici"),
        });
        return new Setup(tenant.Id, customer.Id, carrier.Id, finance, operations, outsider);
    }

    private static async Task<RecordResponse> ApprovedShipmentAsync(HttpClient client, Guid customerId)
    {
        var request = await PostAsync(client, Requests, new JsonObject
        {
            ["customerPartyId"] = customerId.ToString(), ["pickupCity"] = "Ankara", ["deliveryCity"] = "İzmir",
            ["pickupDate"] = Iso(Today.AddDays(1)), ["cargoDescription"] = "24 palet gıda", ["weightKg"] = 12_000,
        });
        var approved = await ActAsync(client, "requests", request, "approve");
        return await GetAsync(client, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, string kind, RecordResponse record, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, null));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    /// <summary>Kaydı günceller; <paramref name="baseUrl"/> kayıt türü adresi (sonuna kimlik eklenir) ya da tam adres olabilir.</summary>
    private static async Task<RecordResponse> PutAsync(HttpClient client, string baseUrl, RecordResponse current, JsonObject values) =>
        await PutAsync(client, $"{baseUrl}/{current.Id}", current.Version, values);

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, long version, JsonObject values)
    {
        var response = await client.PutAsJsonAsync(url, new RecordWriteRequest(values, version));
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
