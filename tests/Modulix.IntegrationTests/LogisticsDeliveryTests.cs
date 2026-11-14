using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Logistics;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Files;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 6: teslim komutu ve değişmez teslim kanıtı. Mutlu yol (teslim: kanıt, sunucu zamanı, durum, iş olayı ve outbox yükü, kanıta fotoğraf;
/// kısmi teslim; başarısız teslim ayrı olay), doğrulama (yoldaki olmayan sevkiyat, zorunlu alanlar, bilinmeyen sonuç, kanıt değişmez ve silinmez, genel
/// uçtan kanıt açılmaz, eski sürüm, genel eylem ucu teslim durumuna geçmez), tenant izolasyonu (başka tenant'ın sevkiyatı ve kanıtı görünmez, teslim
/// edilemez, dosyası eklenemez, indirilemez, silinemez) ve yetki (okuyan teslim kaydedemez, dosya ekleyemez ve silemez, izinsiz göremez, modül kapalıyken 403).
/// Kanıt dosyası (bölüm 24): sefer açıkken eklenir ve silinir, sefer kapanınca donar (403).
/// </summary>
public class LogisticsDeliveryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Requests = "/api/records/logistics.transport-request";
    private const string Shipments = "/api/records/logistics.shipment";
    private const string Proofs = "/api/records/logistics.delivery-proof";
    private const string Parties = "/api/records/core.party";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Delivering_a_shipment_writes_an_immutable_proof_the_status_and_one_business_event()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        var shipment = await InTransitAsync(manager, s.CustomerId);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var response = await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "delivered", "  Ali Yılmaz  ", Damaged: true, Shortage: false, null, "Palet köşesi ezik"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var delivery = (await response.Content.ReadFromJsonAsync<DeliveryResponse>())!;

        Assert.Equal("delivered", Status(delivery.Shipment));
        Assert.Equal("Ali Yılmaz", delivery.Proof.Values["receivedBy"]!.GetValue<string>());
        Assert.True(delivery.Proof.Values["damaged"]!.GetValue<bool>());
        Assert.False(delivery.Proof.Values["shortage"]!.GetValue<bool>());
        Assert.True(DateTimeOffset.Parse(delivery.Proof.Values["receivedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture) >= before); // sunucu zamanı

        // Kanıt sevkiyata bağlı okunur; sevkiyat kapanmıştır ve yük bilgisi artık değişmez.
        var proofs = await ListAsync(manager, Proofs, $"shipmentId={shipment.Id}");
        Assert.Equal(delivery.Proof.Id, Assert.Single(proofs).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Shipments}/{shipment.Id}",
            new RecordWriteRequest(new JsonObject { ["weightKg"] = 10 }, delivery.Shipment.Version))).StatusCode);

        // Aynı işlemde tek iş olayı: sürüm, sıra numarası ve yalnızca beyanlı alanlar.
        var message = Assert.Single(await OutboxAsync(s.TenantId, shipment.Id, LogisticsModule.Events.ShipmentDelivered));
        Assert.Equal(1, message.SchemaVersion);
        Assert.Equal(LogisticsModule.Entities.Shipment, message.EntityKey);
        Assert.Equal(delivery.Shipment.Version, message.Sequence);
        var payload = JsonNode.Parse(message.Payload)!.AsObject();
        Assert.Equal("delivered", payload["outcome"]!.GetValue<string>());
        Assert.Equal(delivery.Proof.Id.ToString(), payload["proofId"]!.GetValue<string>());
        Assert.True(payload["damaged"]!.GetValue<bool>());
        Assert.Equal(s.CustomerId.ToString(), payload["customerPartyId"]!.GetValue<string>());
        Assert.All(payload.Select(p => p.Key), key => Assert.Contains(key, LogisticsModule.Events.ShipmentDeliveredFields));
        Assert.Empty(await OutboxAsync(s.TenantId, shipment.Id, LogisticsModule.Events.ShipmentDeliveryFailed));

        // Fotoğraf ve imza kanıta Core dosya deposuyla eklenir.
        using (var form = Form(delivery.Proof.Id, "teslim-foto.png"))
        {
            Assert.Equal(HttpStatusCode.Created, (await manager.PostAsync("/api/files", form)).StatusCode);
        }

        var files = (await manager.GetFromJsonAsync<List<StoredFileDto>>($"/api/files?entityKey={LogisticsAccess.DeliveryProofEntityKey}&recordId={delivery.Proof.Id}"))!;
        Assert.Equal("teslim-foto.png", Assert.Single(files).FileName);

        // Denetim kaydı: kanıt oluşturuldu.
        var history = (await manager.GetFromJsonAsync<AuditPageResponse>($"{Proofs}/{delivery.Proof.Id}/history"))!;
        Assert.Contains(history.Items, h => h.Action == AuditOperation.Created);
    }

    [Fact]
    public async Task Partial_and_failed_deliveries_close_the_shipment_and_failure_has_its_own_event()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        var partial = await InTransitAsync(manager, s.CustomerId);
        var partialResult = await DeliverAsync(manager, partial, new RecordDeliveryRequest(partial.Version, "partial", "Depo şefi", false, Shortage: true, null, "2 palet yer yetersizliğinden indirilmedi"));
        Assert.Equal("partial", Status(partialResult.Shipment));
        Assert.Equal("2 palet yer yetersizliğinden indirilmedi", partialResult.Shipment.Values["statusReason"]!.GetValue<string>());
        Assert.Single(await OutboxAsync(s.TenantId, partial.Id, LogisticsModule.Events.ShipmentDelivered));

        var failed = await InTransitAsync(manager, s.CustomerId);
        var failedResult = await DeliverAsync(manager, failed, new RecordDeliveryRequest(failed.Version, "failed", null, false, false, "Alıcı yerinde yoktu", null));
        Assert.Equal("failed", Status(failedResult.Shipment));
        Assert.Null(failedResult.Proof.Values["receivedBy"]);
        var failedMessage = Assert.Single(await OutboxAsync(s.TenantId, failed.Id, LogisticsModule.Events.ShipmentDeliveryFailed));
        Assert.Equal("Alıcı yerinde yoktu", JsonNode.Parse(failedMessage.Payload)!["failureReason"]!.GetValue<string>());
        Assert.Empty(await OutboxAsync(s.TenantId, failed.Id, LogisticsModule.Events.ShipmentDelivered)); // başarısız teslim faturalanabilir değildir
    }

    [Fact]
    public async Task Delivery_rules_are_enforced_and_the_proof_cannot_be_changed_or_created_outside_the_command()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);

        // Yola çıkmamış sevkiyat teslim edilemez.
        var notOnTheRoad = await ApprovedShipmentAsync(manager, s.CustomerId);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{notOnTheRoad.Id}/delivery",
            new RecordDeliveryRequest(notOnTheRoad.Version, "delivered", "Ali", false, false, null, null))).StatusCode);

        var shipment = await InTransitAsync(manager, s.CustomerId);

        // Zorunlu alanlar ve bilinmeyen sonuç.
        var noReceiver = await ValidationProblemAsync(await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "delivered", " ", false, false, null, null)));
        Assert.Contains("receivedBy", noReceiver.Errors.Keys);
        var partialWithoutNote = await ValidationProblemAsync(await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "partial", "Ali", false, true, null, null)));
        Assert.Contains("notes", partialWithoutNote.Errors.Keys);
        var failedWithoutReason = await ValidationProblemAsync(await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "failed", null, false, false, null, null)));
        Assert.Contains("failureReason", failedWithoutReason.Errors.Keys);
        var failedWithReceiver = await ValidationProblemAsync(await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "failed", "Ali", false, false, "Yok", null)));
        Assert.Contains("outcome", failedWithReceiver.Errors.Keys);
        var unknown = await ValidationProblemAsync(await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "lost", "Ali", false, false, null, null)));
        Assert.Contains("outcome", unknown.Errors.Keys);

        // Hatalı denemeler hiçbir şey yazmadı: sevkiyat yolda, kanıt ve olay yok.
        Assert.Equal("inTransit", Status(await GetAsync(manager, $"{Shipments}/{shipment.Id}")));
        Assert.Empty(await ListAsync(manager, Proofs, $"shipmentId={shipment.Id}"));
        Assert.Empty(await OutboxAsync(s.TenantId, shipment.Id, LogisticsModule.Events.ShipmentDelivered));

        // Eski sürüm çakışmadır; genel eylem ucu teslim durumuna geçmez; kanıt genel uçtan açılmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version - 1, "delivered", "Ali", false, false, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/actions",
            new RunLogisticsActionRequest("deliver", shipment.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(Proofs, new RecordWriteRequest(new JsonObject
        {
            ["shipmentId"] = shipment.Id.ToString(), ["outcome"] = "delivered", ["receivedBy"] = "Ali",
        }))).StatusCode);

        // Teslim edilen sevkiyat ikinci kez teslim edilemez; kanıt değiştirilemez ve silinemez.
        var delivery = await DeliverAsync(manager, shipment, new RecordDeliveryRequest(shipment.Version, "delivered", "Ali", false, false, null, null));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(delivery.Shipment.Version, "delivered", "Veli", false, false, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"{Proofs}/{delivery.Proof.Id}",
            new RecordWriteRequest(new JsonObject { ["receivedBy"] = "Başkası" }, delivery.Proof.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Proofs}/{delivery.Proof.Id}?version={delivery.Proof.Version}")).StatusCode);
        Assert.Equal("Ali", (await GetAsync(manager, $"{Proofs}/{delivery.Proof.Id}")).Values["receivedBy"]!.GetValue<string>());
    }

    /// <summary>
    /// Kanıt dosyası kaydın kuralını izler (ADR-0038 bölüm 24): kayıt değişmez ama fotoğraf ve imza teslimden sonra eklenir; sevkiyatın seferi açıkken
    /// eklenir ve silinir, sefer kapanınca (burada iptal) kanıt dosyalarıyla birlikte donar: sevkiyat yazma izni olan da ekleyemez, silemez (403). Okuma sürer.
    /// </summary>
    [Fact]
    public async Task Proof_files_change_while_the_trip_is_open_and_freeze_once_the_trip_is_closed()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        var shipment = await InTransitAsync(manager, s.CustomerId);
        var tripId = shipment.Values["tripId"]!.GetValue<string>();
        var proof = (await DeliverAsync(manager, shipment, new RecordDeliveryRequest(shipment.Version, "delivered", "Ali", false, false, null, null))).Proof;

        // Sefer açık: fotoğraf ve imza eklenir, yanlış yüklenen silinir.
        var photo = await UploadAsync(manager, proof.Id, "teslim-foto.png");
        var mistake = await UploadAsync(manager, proof.Id, "yanlis.png");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/files/{mistake.Id}?version={mistake.Version}")).StatusCode);

        // Sefer kapanır: kanıt donar.
        var trip = await GetAsync(manager, $"/api/records/logistics.trip/{tripId}");
        Assert.Equal("cancelled", Status(await ActAsync(manager, "trips", trip, "cancel", "Sefer kapatıldı")));
        using var late = Form(proof.Id, "sonradan.png");
        var denied = await manager.PostAsync("/api/files", late);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("donmuştur", (await denied.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/files/{photo.Id}?version={photo.Version}")).StatusCode);

        var files = (await manager.GetFromJsonAsync<List<StoredFileDto>>($"/api/files?entityKey={LogisticsAccess.DeliveryProofEntityKey}&recordId={proof.Id}"))!;
        Assert.Equal(["teslim-foto.png"], files.Select(f => f.FileName));
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/files/{photo.Id}/content")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_shipments_and_proofs_cannot_be_seen_delivered_or_given_files()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var managerB = await LoginAsync(b.Manager);
        var onTheRoad = await InTransitAsync(managerA, a.CustomerId);
        var delivered = await InTransitAsync(managerA, a.CustomerId);
        var proofA = (await DeliverAsync(managerA, delivered, new RecordDeliveryRequest(delivered.Version, "delivered", "Ali", false, false, null, null))).Proof;

        // Başka tenant'ın yoldaki sevkiyatı ona göre yoktur (404) ve teslim edilemez.
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/shipments/{onTheRoad.Id}/delivery",
            new RecordDeliveryRequest(onTheRoad.Version, "delivered", "Veli", false, false, null, null))).StatusCode);
        Assert.Equal("inTransit", Status(await GetAsync(managerA, $"{Shipments}/{onTheRoad.Id}")));

        // Kanıt görünmez, sevkiyatı kimliğiyle sorgulansa bile; dosyası eklenemez ve listelenemez.
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Proofs}/{proofA.Id}")).StatusCode);
        Assert.Empty(await ListAsync(managerB, Proofs, $"shipmentId={delivered.Id}"));
        Assert.Empty(await ListAsync(managerB, Proofs, null));
        using (var form = Form(proofA.Id, "yabanci.png"))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsync("/api/files", form)).StatusCode);
        }

        // Sefer açıkken bile başka tenant kanıtın dosyasını indiremez ve silemez.
        var photoA = await UploadAsync(managerA, proofA.Id, "teslim.png");
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/files/{photoA.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"/api/files/{photoA.Id}?version={photoA.Version}")).StatusCode);

        // Kanıtın çıktığı tenant kendi kaydını görmeye devam eder.
        Assert.Equal(proofA.Id, Assert.Single(await ListAsync(managerA, Proofs, null)).Id);
    }

    [Fact]
    public async Task Only_those_who_may_write_shipments_can_deliver_and_attach_files_while_readers_and_outsiders_cannot()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        using var outsider = await LoginAsync(s.Outsider);
        var shipment = await InTransitAsync(manager, s.CustomerId);

        // Okuma izni teslim kaydettirmez; izinsiz kişi sevkiyatı göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "delivered", "Ali", false, false, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(shipment.Version, "delivered", "Ali", false, false, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Proofs)).StatusCode);
        Assert.Equal("inTransit", Status(await GetAsync(manager, $"{Shipments}/{shipment.Id}")));

        var delivery = await DeliverAsync(manager, shipment, new RecordDeliveryRequest(shipment.Version, "delivered", "Ali", false, false, null, null));

        // Okuyan kanıtı görür ve dosyasını listeler ama dosya ekleyemez; izinsiz kişi ikisini de yapamaz.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Proofs}/{delivery.Proof.Id}")).StatusCode);
        using (var form = Form(delivery.Proof.Id, "okuyan.png"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/files", form)).StatusCode);
        }

        // Sefer açıkken bile okuyan, yazanın eklediği dosyayı silemez.
        var photo = await UploadAsync(manager, delivery.Proof.Id, "teslim.png");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/files/{photo.Id}?version={photo.Version}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, (await outsider.DeleteAsync($"/api/files/{photo.Id}?version={photo.Version}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/files?entityKey={LogisticsAccess.DeliveryProofEntityKey}&recordId={delivery.Proof.Id}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await outsider.GetAsync($"/api/files?entityKey={LogisticsAccess.DeliveryProofEntityKey}&recordId={delivery.Proof.Id}")).StatusCode);

        // Modül kapalıyken uç kapalıdır.
        await factory.SetModuleAsync(s.TenantId, "logistics", enabled: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery",
            new RecordDeliveryRequest(delivery.Shipment.Version, "delivered", "Ali", false, false, null, null))).StatusCode);
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, Guid CustomerId, ModulixUser Manager, ModulixUser Reader, ModulixUser Outsider);

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

        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, RequestsWrite, RequestsApprove, ShipmentsRead, ShipmentsWrite, TripsRead, TripsWrite], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [RequestsRead, ShipmentsRead], Scope.All, reader);

        using var adminClient = await LoginAsync(admin);
        var customer = await PostAsync(adminClient, Parties, new JsonObject { ["name"] = $"Müşteri {Guid.NewGuid():N}"[..20], ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        return new Setup(tenant.Id, customer.Id, manager, reader, outsider);
    }

    /// <summary>Talep açar, onaylar (sevkiyat doğar) ve sevkiyatı döner.</summary>
    private static async Task<RecordResponse> ApprovedShipmentAsync(HttpClient manager, Guid customerId)
    {
        var request = await PostAsync(manager, Requests, new JsonObject
        {
            ["customerPartyId"] = customerId.ToString(),
            ["pickupCity"] = "Ankara",
            ["deliveryCity"] = "İzmir",
            ["pickupDate"] = Iso(Today.AddDays(1)),
            ["cargoDescription"] = "24 palet gıda",
            ["weightKg"] = 12_000,
        });
        var approved = await ActAsync(manager, "requests", request, "approve");
        return await GetAsync(manager, $"{Shipments}/{approved.Values["shipmentId"]!.GetValue<string>()}");
    }

    /// <summary>Sevkiyatı yola çıkarır: sefere ekler, yükleme başlar, yola çıkar.</summary>
    private static async Task<RecordResponse> InTransitAsync(HttpClient manager, Guid customerId)
    {
        var shipment = await ApprovedShipmentAsync(manager, customerId);
        var trip = await PostAsync(manager, "/api/records/logistics.trip", new JsonObject { ["plannedStart"] = Iso(Today.AddDays(1)) });
        var assigned = await manager.PostAsJsonAsync($"/api/logistics/trips/{trip.Id}/shipments", new AssignShipmentRequest(shipment.Id));
        Assert.True(assigned.StatusCode == HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        shipment = (await assigned.Content.ReadFromJsonAsync<RecordResponse>())!;
        return await ActAsync(manager, "shipments", await ActAsync(manager, "shipments", shipment, "startLoading"), "depart");
    }

    private static async Task<DeliveryResponse> DeliverAsync(HttpClient client, RecordResponse shipment, RecordDeliveryRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/shipments/{shipment.Id}/delivery", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DeliveryResponse>())!;
    }

    private static MultipartFormDataContent Form(Guid proofId, string fileName)
    {
        var file = new ByteArrayContent([.. Png, .. Encoding.ASCII.GetBytes(Guid.NewGuid().ToString())]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent(LogisticsAccess.DeliveryProofEntityKey), "entityKey" },
            { new StringContent(proofId.ToString()), "recordId" },
            { file, "file", fileName },
        };
    }

    private static async Task<StoredFileDto> UploadAsync(HttpClient client, Guid proofId, string fileName)
    {
        using var form = Form(proofId, fileName);
        var response = await client.PostAsync("/api/files", form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
    }

    private async Task<List<Modulix.Infrastructure.Records.OutboxMessage>> OutboxAsync(Guid tenantId, Guid recordId, string eventType)
    {
        using var scope = factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().OutboxMessages.AsNoTracking()
            .Where(o => o.RecordId == recordId && o.EventType == eventType).ToListAsync();
    }

    private static string Status(RecordResponse record) => record.Values["status"]!.GetValue<string>();

    private static async Task<RecordResponse> ActAsync(HttpClient client, string kind, RecordResponse record, string action, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, reason));
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
