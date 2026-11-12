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
/// ADR-0038 dilim 4: taşıma talebi ve sevkiyat. Mutlu yol (talep aç, onayla → aynı işlemde sevkiyat açılır ve talebe bağlanır, sevkiyat Planlandı →
/// Yükleniyor → Yolda, denetim kaydı, durum süzgeci), doğrulama (zorunlu alanlar, tarih sırası, referans tekilliği, kapanan talep düzenlenmez,
/// genel güncelleme durumu yazamaz, sevkiyat doğrudan açılmaz, silme yok, gerekçesiz ret, sırasız geçiş, eski sürüm), tenant izolasyonu (başka
/// tenant'ın talebi görünmez ve onaylanamaz, müşterisi kullanılamaz) ve yetki (operasyon onaylayamaz, okuyan yazamaz, izinsiz göremez, onaylayan
/// sevkiyat yazma izni olmadan da sevkiyatı açar).
/// </summary>
public class LogisticsShipmentTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Shipments = "/api/records/logistics.shipment";
    private const string Parties = "/api/records/core.party";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Approving_a_request_opens_its_shipment_which_moves_from_planning_to_the_road()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);

        var request = await PostAsync(operations, Requests, Request(s.CustomerId, "Ankara", "İzmir", "24 palet gıda", "ORD-1"));
        Assert.Equal("new", Status(request));
        Assert.Equal("manual", request.Values["source"]!.GetValue<string>());
        Assert.Equal("Ftl", request.Values["transportModeKey"]!.GetValue<string>()); // listenin varsayılanı

        // Operasyon onaylayamaz; eylem listesinde onay yok.
        var opsActions = (await operations.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/requests/{request.Id}/actions"))!;
        Assert.DoesNotContain(opsActions, a => a.Action == "approve");
        Assert.Contains(opsActions, a => a.Action == "cancel" && a.Allowed && a.RequiresReason);

        var approved = await ActAsync(manager, "requests", request, "approve");
        Assert.Equal("approved", Status(approved));
        var shipmentId = Guid.Parse(approved.Values["shipmentId"]!.GetValue<string>());

        var shipment = await GetAsync(operations, $"{Shipments}/{shipmentId}");
        Assert.Equal(("pending", request.Id.ToString(), "Ankara", "24 palet gıda"),
            (Status(shipment), shipment.Values["requestId"]!.GetValue<string>(), shipment.Values["pickupCity"]!.GetValue<string>(), shipment.Values["cargoDescription"]!.GetValue<string>()));

        shipment = await PlanAsync(operations, shipment); // planlama sefer atamasıdır (dilim 5)
        Assert.Equal("planned", Status(shipment));
        shipment = await ActAsync(operations, "shipments", shipment, "startLoading");
        shipment = await ActAsync(operations, "shipments", shipment, "depart");
        Assert.Equal("inTransit", Status(shipment));

        Assert.Equal([shipmentId], (await ListAsync(operations, Shipments, "status=loading,inTransit")).Select(r => r.Id));
        Assert.Empty(await ListAsync(operations, Requests, "status=new"));

        var history = (await operations.GetFromJsonAsync<AuditPageResponse>($"{Shipments}/{shipmentId}/history"))!;
        Assert.True(history.Items.Count(h => h.Action == AuditOperation.Updated && h.Changes.Any(c => c.Field == "status")) >= 3);
    }

    [Fact]
    public async Task Invalid_requests_and_out_of_order_commands_are_rejected()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        var missing = await ValidationProblemAsync(await manager.PostAsJsonAsync(Requests, new RecordWriteRequest(new JsonObject())));
        Assert.True(missing.Errors.ContainsKey("customerPartyId") && missing.Errors.ContainsKey("pickupCity") && missing.Errors.ContainsKey("cargoDescription"));

        var backwards = Request(s.CustomerId, "Bursa", "Konya", "Makine", null);
        backwards["deliveryBy"] = Iso(Today);
        backwards["pickupDate"] = Iso(Today.AddDays(3));
        Assert.Equal(["Teslim günü yükleme gününden önce olamaz."], (await ValidationProblemAsync(await manager.PostAsJsonAsync(Requests, new RecordWriteRequest(backwards)))).Errors["deliveryBy"]);

        var first = await PostAsync(manager, Requests, Request(s.CustomerId, "Bursa", "Konya", "Makine", "REF-9"));
        Assert.Equal(["Bu referansla açılmış bir talep zaten var."],
            (await ValidationProblemAsync(await manager.PostAsJsonAsync(Requests, new RecordWriteRequest(Request(s.CustomerId, "Bursa", "Konya", "Makine", "REF-9"))))).Errors["externalRef"]);

        // Genel güncelleme durumu yazamaz (yok sayılır); ret gerekçe ister; sırasız geçiş çakışmadır; sevkiyat doğrudan açılmaz; silme yok.
        var ignored = await PutAsync(manager, Requests, first, new JsonObject { ["status"] = "approved", ["notes"] = "not" });
        Assert.Equal("new", Status(ignored));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"/api/logistics/requests/{first.Id}/actions",
            new RunLogisticsActionRequest("reject", ignored.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"/api/logistics/requests/{first.Id}/actions",
            new RunLogisticsActionRequest("teleport", ignored.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/requests/{first.Id}/actions",
            new RunLogisticsActionRequest("approve", ignored.Version - 1, null))).StatusCode);
        var direct = Request(s.CustomerId, "Bursa", "Konya", "Makine", null);
        direct.Remove("externalRef");
        direct["requestId"] = first.Id.ToString();
        var blocked = await manager.PostAsJsonAsync(Shipments, new RecordWriteRequest(direct));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = (await blocked.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Contains("taşıma talebi onaylanınca", $"{problem.Title} {problem.Detail}");
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Requests}/{first.Id}?version={ignored.Version}")).StatusCode);

        var rejected = await ActAsync(manager, "requests", ignored, "reject", "Araç yok");
        Assert.Equal(("rejected", "Araç yok"), (Status(rejected), rejected.Values["statusReason"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Requests}/{first.Id}",
            new RecordWriteRequest(new JsonObject { ["notes"] = "kapandı" }, rejected.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/requests/{first.Id}/actions",
            new RunLogisticsActionRequest("approve", rejected.Version, null))).StatusCode);

        // Sevkiyat: yola çıkmadan önce yükleme sırası şart; yoldaki sevkiyat iptal edilmez.
        var other = await ActAsync(manager, "requests", await PostAsync(manager, Requests, Request(s.CustomerId, "Adana", "Mersin", "Seramik", null)), "approve");
        var shipment = await GetAsync(manager, $"{Shipments}/{other.Values["shipmentId"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/actions",
            new RunLogisticsActionRequest("depart", shipment.Version, null))).StatusCode);
        shipment = await ActAsync(manager, "shipments", await ActAsync(manager, "shipments", await PlanAsync(manager, shipment), "startLoading"), "depart");
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/actions",
            new RunLogisticsActionRequest("cancel", shipment.Version, "vazgeçildi"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Shipments}/{shipment.Id}",
            new RecordWriteRequest(new JsonObject { ["weightKg"] = 10 }, shipment.Version))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_requests_customers_and_shipments_cannot_be_seen_or_used()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        var requestA = await PostAsync(managerA, Requests, Request(a.CustomerId, "Kocaeli", "Sakarya", "Kimyasal", "A-1"));

        var foreignCustomer = await ValidationProblemAsync(await managerB.PostAsJsonAsync(Requests, new RecordWriteRequest(Request(a.CustomerId, "X", "Y", "Z", null))));
        Assert.Equal(["Müşteri firma bulunamadı."], foreignCustomer.Errors["customerPartyId"]);
        await PostAsync(managerB, Requests, Request(b.CustomerId, "Kocaeli", "Sakarya", "Kimyasal", "A-1")); // aynı referans başka tenant'ta serbest

        Assert.DoesNotContain(await ListAsync(managerB, Requests, null), r => r.Id == requestA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Requests}/{requestA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/logistics/requests/{requestA.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/requests/{requestA.Id}/actions",
            new RunLogisticsActionRequest("approve", requestA.Version, null))).StatusCode);
        Assert.Empty(await ListAsync(managerB, Shipments, null));
    }

    [Fact]
    public async Task Approval_needs_its_own_permission_and_readers_and_outsiders_cannot_act()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var reader = await LoginAsync(s.Reader);
        using var approver = await LoginAsync(s.ApproverOnly);
        using var outsider = await LoginAsync(s.Outsider);
        var request = await PostAsync(operations, Requests, Request(s.CustomerId, "Samsun", "Trabzon", "Fındık", null));

        Assert.Equal(HttpStatusCode.Forbidden, (await operations.PostAsJsonAsync($"/api/logistics/requests/{request.Id}/actions",
            new RunLogisticsActionRequest("approve", request.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Requests}/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Requests}/{request.Id}",
            new RecordWriteRequest(new JsonObject { ["notes"] = "x" }, request.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/requests/{request.Id}/actions",
            new RunLogisticsActionRequest("cancel", request.Version, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Requests)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/logistics/requests/{request.Id}/actions")).StatusCode);

        // Yalnızca onay izni olan kişi sevkiyat yazma izni olmadan da onaylar; sevkiyat onayın parçası olarak açılır. Sevkiyatı yürütemez.
        var approved = await ActAsync(approver, "requests", request, "approve");
        var shipment = await GetAsync(operations, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/actions",
            new RunLogisticsActionRequest("startLoading", shipment.Version, null))).StatusCode);

        // Modül kapalıyken uçlar kapalıdır.
        await factory.SetModuleAsync(s.TenantId, "logistics", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.GetAsync($"/api/logistics/requests/{request.Id}/actions")).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, ModulixUser Manager, ModulixUser Operations, ModulixUser Reader, ModulixUser ApproverOnly, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); // müşteri firmayı kayıt ucundan açmak için
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var operations = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, RequestsWrite, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite], Scope.All, operations);
        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, ShipmentsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, RequestsApprove], Scope.All, approver);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant.Id, customer.Id, manager, operations, reader, approver, outsider);
    }

    private static JsonObject Request(Guid customer, string from, string to, string cargo, string? reference) => new()
    {
        ["customerPartyId"] = customer.ToString(),
        ["pickupCity"] = from,
        ["deliveryCity"] = to,
        ["pickupDate"] = Iso(Today.AddDays(1)),
        ["deliveryBy"] = Iso(Today.AddDays(3)),
        ["cargoDescription"] = cargo,
        ["weightKg"] = 12_000,
        ["externalRef"] = reference,
    };

    private static string Status(RecordResponse record) => record.Values["status"]!.GetValue<string>();

    private static async Task<RecordResponse> ActAsync(HttpClient client, string kind, RecordResponse record, string action, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    /// <summary>Sevkiyatı yeni bir sefere ekler (sevkiyat "Planlandı" olur) ve sevkiyatın yeni halini döner.</summary>
    private static async Task<RecordResponse> PlanAsync(HttpClient client, RecordResponse shipment)
    {
        var trip = await PostAsync(client, "/api/records/logistics.trip", new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)) });
        var response = await client.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
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
