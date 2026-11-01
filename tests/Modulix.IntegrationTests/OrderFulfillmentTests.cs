using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Orders;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Orders.OrdersModule.Permissions;
using Crm = Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0023 dilim 5: teslimat ve hizmet başlangıcı. Çoklu/kısmi teslimat ve kalan miktar, fazla teslimatın (eşzamanlı dahil) engellenmesi, başarısız teslimat ve
/// gerekçeli yeniden planlama, teslim kanıtı yükleme/indirme sınırları ve yetkisi, hizmet başlangıcı ön koşulları (görev, bağımlı iş), başlangıcın tamamlanma
/// sayılmaması, karma siparişin ayrı izlenmesi, normal kapatma, sipariş iptali, tenant izolasyonu ve yetki.
/// </summary>
public class OrderFulfillmentTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly byte[] Pdf = [.. "%PDF-1.4 teslim tutanagi"u8.ToArray()];

    [Fact]
    public async Task Partial_deliveries_track_remaining_quantity_and_block_over_delivery()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        Assert.Equal((2m, 2m, 0m, 2m), (line.Ordered, line.Effective, line.Delivered, line.Remaining));

        // Sunucu 2 adet: iki ayrı teslimat 1'er adet planlanır; üçüncü plan kalan kapasiteyi aşar.
        var first = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 1m));
        var second = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 8), (line.LineId, 1m));
        Assert.StartsWith("TS-", first.Number);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostPlanAsync(ops, order.Id, new DateOnly(2026, 12, 9), (line.LineId, 1m))).StatusCode);
        Assert.Equal("Planned", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);

        // Teslim alan kişi zorunlu; fazla teslimat (kalandan çok) reddedilir; gelecekte tarih olmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DeliverAsync(ops, first, null, (line.LineId, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DeliverAsync(ops, first, "Ali Veli", (line.LineId, 3m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PostAsJsonAsync($"/api/orders/deliveries/{first.Id}/deliver",
            new DeliveryActionRequest(first.Version, ReceiverName: "Ali Veli", ActualDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3))))).StatusCode);

        // Kısmi teslim: ilk teslimat 1 adet teslim edilir; sipariş "Kısmen", kalan 1, sipariş kalemi teslim edilen miktarı güncel.
        var delivered = await OkAsync(await DeliverAsync(ops, first, "Ali Veli", (line.LineId, 1m)));
        Assert.Equal(("Delivered", "Ali Veli"), (delivered.Status, delivered.ReceiverName));
        Assert.NotNull(delivered.DeliveredAt);
        Assert.Equal(1m, delivered.Lines.Single().Delivered);
        var view = await FulfillmentAsync(ops, order.Id);
        Assert.Equal((1m, 1m), (view.Products.Single().Delivered, view.Products.Single().Remaining));
        Assert.Equal("Partial", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);
        var orderLine = (await ops.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single(l => l.Name == "Sunucu");
        Assert.Equal(1m, orderLine.DeliveredQuantity);

        // Teslim edilmiş teslimat yeniden teslim edilemez, iptal edilemez ve silinemez; tarihçeye yazılır.
        Assert.Equal(HttpStatusCode.Conflict, (await DeliverAsync(ops, delivered, "Ali Veli", (line.LineId, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActionAsync(ops, delivered, "cancel", "Vazgeç")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ops.DeleteAsync($"/api/orders/deliveries/{delivered.Id}?version={delivered.Version}")).StatusCode);
        var history = (await ops.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "Delivered");

        // İkinci teslimat son adeti teslim eder; ürün kalemi tamamlanır ama hizmet kalemi açık olduğundan sipariş tamamlanmış sayılmaz (karma sipariş ayrı izlenir).
        await OkAsync(await DeliverAsync(ops, second, "Ayşe Kaya", (line.LineId, 1m)));
        Assert.Equal((2m, 0m), ((await FulfillmentAsync(ops, order.Id)).Products.Single().Delivered, (await FulfillmentAsync(ops, order.Id)).Products.Single().Remaining));
        Assert.Equal("Partial", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostPlanAsync(ops, order.Id, new DateOnly(2026, 12, 20), (line.LineId, 1m))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_deliveries_cannot_deliver_more_than_the_ordered_quantity()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var ops2 = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var first = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 1m));
        var second = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 6), (line.LineId, 1m));

        // Her teslimat ayrı ayrı kalan miktar (2) kadar teslim girer; birlikte 4 adet olur. Biri başarısız olmalı.
        var results = await Task.WhenAll(
            DeliverAsync(ops, first, "A", (line.LineId, 2m)),
            DeliverAsync(ops2, second, "B", (line.LineId, 2m)));

        var bodies = string.Join(" | ", await Task.WhenAll(results.Select(async r => (int)r.StatusCode + " " + await r.Content.ReadAsStringAsync())));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, bodies);
        Assert.Contains(results, r => r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict);
        Assert.False(results.Any(r => r.StatusCode == HttpStatusCode.InternalServerError), bodies);
        var orderLine = (await ops.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single(l => l.Name == "Sunucu");
        Assert.Equal(2m, orderLine.DeliveredQuantity);
    }

    [Fact]
    public async Task Failed_delivery_needs_a_reason_and_is_rescheduled_once_with_a_reason_keeping_the_failed_record()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var planned = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 2m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActionAsync(ops, planned, "fail", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ActionAsync(ops, planned, "reschedule", "Henüz başarısız değil", new DateOnly(2026, 12, 9))).StatusCode);
        var failed = await OkAsync(await ActionAsync(ops, planned, "fail", "Müşteri adreste yoktu"));
        Assert.Equal(("Failed", "Müşteri adreste yoktu"), (failed.Status, failed.FailureReason));
        Assert.Equal(0m, (await FulfillmentAsync(ops, order.Id)).Products.Single().Delivered);
        Assert.Equal("NotStarted", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);

        // Yeniden planlama: gerekçe ve yeni tarih zorunlu; yeni teslimat öncekine bağlanır, başarısız kayıt silinmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActionAsync(ops, failed, "reschedule", null, new DateOnly(2026, 12, 9))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActionAsync(ops, failed, "reschedule", "Müşteri yeni gün istedi")).StatusCode);
        var again = await OkAsync(await ActionAsync(ops, failed, "reschedule", "Müşteri yeni gün istedi", new DateOnly(2026, 12, 9)));
        Assert.Equal(("Planned", failed.Id, "Müşteri yeni gün istedi", new DateOnly(2026, 12, 9)), (again.Status, again.RescheduledFromId, again.RescheduleReason, again.PlannedDate));
        Assert.Equal(2m, again.Lines.Single().Planned);
        Assert.Equal(HttpStatusCode.Conflict, (await ActionAsync(ops, failed, "reschedule", "İkinci kez", new DateOnly(2026, 12, 10))).StatusCode);
        var view = await FulfillmentAsync(ops, order.Id);
        Assert.Equal(2, view.Deliveries.Count);
        Assert.Contains(view.Deliveries, d => d.Id == failed.Id && d.Status == "Failed" && d.FailureReason == "Müşteri adreste yoktu");
        Assert.Equal("Planned", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);

        // Planlı teslimat gerekçeyle iptal edilebilir; iptal gerekçe ister.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ActionAsync(ops, again, "cancel", null)).StatusCode);
        Assert.Equal("Cancelled", (await OkAsync(await ActionAsync(ops, again, "cancel", "Sipariş içeriği değişti"))).Status);
        Assert.Equal(2m, (await FulfillmentAsync(ops, order.Id)).Products.Single().Remaining);
    }

    [Fact]
    public async Task Delivery_evidence_is_limited_by_type_content_and_size_and_download_follows_permissions_and_tenant()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var reader = await LoginAsync(s.Reader);
        using var foreign = await LoginAsync(other.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var delivery = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 2m));

        var added = await UploadAsync(ops, delivery.Id, "tutanak.pdf", "application/pdf", Pdf, "İmzalı teslim tutanağı");
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var withEvidence = (await added.Content.ReadFromJsonAsync<DeliveryViewDto>())!;
        var evidence = Assert.Single(withEvidence.Evidence);
        Assert.Equal(("tutanak.pdf", "application/pdf", Pdf.Length, 64), (evidence.FileName, evidence.ContentType, evidence.SizeBytes, evidence.Sha256.Length));

        // Tür, içerik, boyut ve ad sınırları sunucuda uygulanır.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(ops, delivery.Id, "a.exe", "application/x-msdownload", Pdf, null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(ops, delivery.Id, "sahte.pdf", "application/pdf", [1, 2, 3, 4, 5, 6], null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(ops, delivery.Id, "../etc/passwd.pdf", "application/pdf", Pdf, null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(ops, delivery.Id, "bos.pdf", "application/pdf", [], null)).StatusCode);
        var big = new byte[(2 * 1024 * 1024) + 1];
        Pdf.CopyTo(big, 0);
        Assert.True((await UploadAsync(ops, delivery.Id, "buyuk.pdf", "application/pdf", big, null)).StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity);

        // Yazma izni olmayan ekleyemez ama okuyabilir ve indirebilir; indirilen dosya aynı bayttır, önbelleğe alınmaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(reader, delivery.Id, "x.pdf", "application/pdf", Pdf, null)).StatusCode);
        var download = await reader.GetAsync($"/api/orders/deliveries/{delivery.Id}/evidence/{evidence.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString());
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));

        // Başka tenant kayıt ve dosyayı göremez.
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/orders/deliveries/{delivery.Id}/evidence/{evidence.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(foreign, delivery.Id, "y.pdf", "application/pdf", Pdf, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/orders/orders/{order.Id}/fulfillment")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ops.GetAsync($"/api/orders/deliveries/{delivery.Id}/evidence/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Service_start_is_blocked_until_prerequisites_are_done_and_starting_is_not_completing()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var closer = await LoginAsync(s.Closer);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var service = (await FulfillmentAsync(ops, order.Id)).Services.Single();
        Assert.Null(service.Start);
        var plannedService = await PlanServiceAsync(ops, order.Id, service.LineId, new DateOnly(2026, 12, 10));
        Assert.Equal(("Planned", new DateOnly(2026, 12, 10)), (plannedService.Start!.Status, plannedService.Start.PlannedStartDate));
        Assert.Equal(HttpStatusCode.Conflict, (await PlanServiceRawAsync(ops, order.Id, service.LineId, null)).StatusCode);

        // Ön koşul görevleri bitmeden başlangıç kaydedilemez; eylem listesi de nedeni söyler.
        Assert.NotEmpty(plannedService.Blockers);
        Assert.False(plannedService.Actions.Single(a => a.Action == "start").Allowed);
        var blocked = await ServiceActionAsync(ops, plannedService, "start");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("ön koşul", await blocked.Content.ReadAsStringAsync());

        // Başlamamış hizmet onaylanamaz/tamamlanamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await ServiceActionAsync(ops, plannedService, "complete")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ServiceActionAsync(ops, plannedService, "confirm", "Müşteri A")).StatusCode);

        // Hizmet işinin ön koşul görevleri tamamlanınca başlangıç kaydedilir.
        await CompleteServicePrerequisitesAsync(ops, order.Id);
        var ready = (await FulfillmentAsync(ops, order.Id)).Services.Single();
        Assert.Empty(ready.Blockers);
        var started = await OkServiceAsync(await ServiceActionAsync(ops, ready, "start"));
        Assert.Equal("Started", started.Start!.Status);
        Assert.NotNull(started.Start.ActualStartAt);
        Assert.Equal("Partial", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await ServiceActionAsync(ops, started, "start")).StatusCode);

        // Müşteri onayı kişi ister; başlamış hizmet "tamamlandı" sayılmaz, sipariş kapatılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ServiceActionAsync(ops, started, "confirm")).StatusCode);
        var confirmed = await OkServiceAsync(await ServiceActionAsync(ops, started, "confirm", "Mehmet Demir", "E-posta ile teyit edildi"));
        Assert.Equal(("Mehmet Demir", "E-posta ile teyit edildi"), (confirmed.Start!.CustomerConfirmedBy, confirmed.Start.ConfirmationNote));
        Assert.NotNull(confirmed.Start.CustomerConfirmedAt);
        var delivery = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 2m));
        await OkAsync(await DeliverAsync(ops, delivery, "Ali Veli", (line.LineId, 2m)));
        Assert.Equal("Partial", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);
        var current = await GetOrderAsync(closer, order.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await closer.PostAsJsonAsync($"/api/orders/orders/{order.Id}/close", new OrderTransitionRequest(current.Version))).StatusCode);

        // Tamamlama ayrı bir kayıttır; tüm kalemler tamamlanınca sipariş "Tamamlandı" olur ve normal kapatma yapılabilir.
        var completed = await OkServiceAsync(await ServiceActionAsync(ops, confirmed, "complete"));
        Assert.Equal("Completed", completed.Start!.Status);
        Assert.Equal("Completed", (await GetOrderAsync(ops, order.Id)).FulfillmentStatus);
        current = await GetOrderAsync(closer, order.Id);
        var closed = await closer.PostAsJsonAsync($"/api/orders/orders/{order.Id}/close", new OrderTransitionRequest(current.Version));
        Assert.True(closed.StatusCode == HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        Assert.Equal("Closed", (await closed.Content.ReadFromJsonAsync<OrderDto>())!.Status);
    }

    [Fact]
    public async Task Service_start_waits_for_the_dependent_delivery_job_when_services_follow_delivery()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var admin = await LoginAsync(s.Admin);
        await SetSettingAsync(admin, "orders.jobs.service-after-delivery", "true");
        var order = await AcceptedOrderAsync(s, sales, ops);
        var service = (await FulfillmentAsync(ops, order.Id)).Services.Single();
        var planned = await PlanServiceAsync(ops, order.Id, service.LineId, null);
        await CompleteServicePrerequisitesAsync(ops, order.Id);

        var blocked = await ServiceActionAsync(ops, (await FulfillmentAsync(ops, order.Id)).Services.Single(), "start");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Bağımlı olduğu iş tamamlanmadı", await blocked.Content.ReadAsStringAsync());
        Assert.Contains(planned.Blockers.Concat((await FulfillmentAsync(ops, order.Id)).Services.Single().Blockers), b => b.Contains("Bağımlı"));

        var deliveryJob = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}&kind=Delivery"))!.Items.Single();
        await CompleteJobAsync(ops, deliveryJob.Id);
        var started = await OkServiceAsync(await ServiceActionAsync(ops, (await FulfillmentAsync(ops, order.Id)).Services.Single(), "start"));
        Assert.Equal("Started", started.Start!.Status);
    }

    [Fact]
    public async Task Cancelling_the_order_cancels_planned_deliveries_but_keeps_delivered_records()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var approver = await LoginAsync(s.Approver);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var done = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 1m));
        var open = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 8), (line.LineId, 1m));
        await OkAsync(await DeliverAsync(ops, done, "Ali Veli", (line.LineId, 1m)));

        var current = await GetOrderAsync(approver, order.Id);
        var cancelled = await approver.PostAsJsonAsync($"/api/orders/orders/{order.Id}/cancel", new OrderTransitionRequest(current.Version, "Müşteri vazgeçti"));
        Assert.True(cancelled.StatusCode == HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        var view = await FulfillmentAsync(ops, order.Id);
        Assert.Equal("Delivered", view.Deliveries.Single(d => d.Id == done.Id).Status);
        Assert.Equal("Cancelled", view.Deliveries.Single(d => d.Id == open.Id).Status);
        Assert.Equal(1m, view.Products.Single().Delivered);
        // İptal edilmiş siparişte yeni teslimat planlanamaz, teslim edilmiş kayıt değişmez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostPlanAsync(ops, order.Id, new DateOnly(2026, 12, 20), (line.LineId, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DeliverAsync(ops, open, "Ali Veli", (line.LineId, 1m))).StatusCode);
    }

    [Fact]
    public async Task Fulfillment_requires_an_accepted_handover_the_record_permission_and_tenant_scope()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var reader = await LoginAsync(s.Reader);
        using var foreign = await LoginAsync(other.Ops);

        // Devir kabul edilmeden teslimat planlanamaz.
        var early = await ApprovedOrderAsync(s, sales);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OrderLines SET Kind = 'Product' WHERE OrderId = {0} AND Name = 'Sunucu'", early.Id);
        var earlyLine = (await ops.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={early.Id}"))!.Items.Single(l => l.Name == "Sunucu");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostPlanAsync(ops, early.Id, new DateOnly(2026, 12, 5), (earlyLine.Id, 1m))).StatusCode);

        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var delivery = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 2m));

        // Okuma izni görüntüler; kayıt izni olmayan (satış ve izleyici) hiçbir kayıt işlemi yapamaz.
        Assert.Equal(delivery.Id, (await FulfillmentAsync(reader, order.Id)).Deliveries.Single().Id);
        Assert.Empty((await FulfillmentAsync(reader, order.Id)).Deliveries.Single().Actions);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPlanAsync(reader, order.Id, new DateOnly(2026, 12, 6), (line.LineId, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeliverAsync(sales, delivery, "Ali Veli", (line.LineId, 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlanServiceRawAsync(reader, order.Id, (await FulfillmentAsync(ops, order.Id)).Services.Single().LineId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/orders/deliveries/{delivery.Id}", new SaveDeliveryRequest(delivery.Version, new DateOnly(2026, 12, 30), "x"))).StatusCode);
        // Genel uçtan teslimat/hizmet kaydı oluşturulamaz (yalnızca eylemlerle).
        Assert.True((await ops.PostAsJsonAsync("/api/orders/deliveries", new SaveDeliveryRequest(0, new DateOnly(2026, 12, 6)))).StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
        Assert.Equal("Planned", (await FulfillmentAsync(ops, order.Id)).Deliveries.Single().Status);

        // Başka tenant'ın kullanıcısı sipariş, teslimat ve eylemlere erişemez.
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/orders/orders/{order.Id}/fulfillment")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/orders/deliveries/{delivery.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeliverAsync(foreign, delivery, "Ali Veli", (line.LineId, 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostPlanAsync(foreign, order.Id, new DateOnly(2026, 12, 6), (line.LineId, 1m))).StatusCode);
        Assert.Empty((await foreign.GetFromJsonAsync<PagedResult<DeliveryDto>>("/api/orders/deliveries"))!.Items);
    }

    // ---- yardımcılar

    private static async Task<FulfillmentDto> FulfillmentAsync(HttpClient client, Guid orderId)
    {
        var response = await client.GetAsync($"/api/orders/orders/{orderId}/fulfillment");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FulfillmentDto>())!;
    }

    private static Task<OrderDto> GetOrderAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{id}")!;

    private static Task<HttpResponseMessage> PostPlanAsync(HttpClient client, Guid orderId, DateOnly date, params (Guid LineId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync($"/api/orders/orders/{orderId}/deliveries", new PlanDeliveryRequest(date, [.. lines.Select(l => new QuantityDto(l.LineId, l.Quantity))]));

    private static async Task<DeliveryViewDto> PlanAsync(HttpClient client, Guid orderId, DateOnly date, params (Guid LineId, decimal Quantity)[] lines)
    {
        var response = await PostPlanAsync(client, orderId, date, lines);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DeliveryViewDto>())!;
    }

    private static Task<HttpResponseMessage> DeliverAsync(HttpClient client, DeliveryViewDto delivery, string? receiver, params (Guid LineId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync($"/api/orders/deliveries/{delivery.Id}/deliver",
            new DeliveryActionRequest(delivery.Version, ReceiverName: receiver, Lines: [.. lines.Select(l => new QuantityDto(l.LineId, l.Quantity))]));

    private static Task<HttpResponseMessage> ActionAsync(HttpClient client, DeliveryViewDto delivery, string action, string? reason, DateOnly? plannedDate = null) =>
        client.PostAsJsonAsync($"/api/orders/deliveries/{delivery.Id}/{action}", new DeliveryActionRequest(delivery.Version, Reason: reason, PlannedDate: plannedDate));

    private static async Task<DeliveryViewDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DeliveryViewDto>())!;
    }

    private static async Task<ServiceLineDto> OkServiceAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ServiceLineDto>())!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid deliveryId, string fileName, string contentType, byte[] content, string? description)
    {
        var url = $"/api/orders/deliveries/{deliveryId}/evidence?fileName={Uri.EscapeDataString(fileName)}" + (description is null ? string.Empty : $"&description={Uri.EscapeDataString(description)}");
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await client.PutAsync(url, body);
    }

    private static Task<HttpResponseMessage> PlanServiceRawAsync(HttpClient client, Guid orderId, Guid lineId, DateOnly? date) =>
        client.PostAsJsonAsync($"/api/orders/orders/{orderId}/service-starts", new PlanServiceRequest(lineId, date));

    private static async Task<ServiceLineDto> PlanServiceAsync(HttpClient client, Guid orderId, Guid lineId, DateOnly? date)
    {
        var response = await PlanServiceRawAsync(client, orderId, lineId, date);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ServiceLineDto>())!;
    }

    private static Task<HttpResponseMessage> ServiceActionAsync(HttpClient client, ServiceLineDto service, string action, string? confirmedBy = null, string? note = null) =>
        client.PostAsJsonAsync($"/api/orders/service-starts/{service.Start!.Id}/{action}", new ServiceActionRequest(service.Start.Version, confirmedBy, note));

    /// <summary>Hizmet işinin ilk üç (ön koşul) görevini tamamlar; işi başlatmaz.</summary>
    private static async Task CompleteServicePrerequisitesAsync(HttpClient client, Guid orderId)
    {
        var job = (await client.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={orderId}&kind=Service"))!.Items.Single();
        var detail = (await client.GetFromJsonAsync<OperationJobDetailDto>($"/api/orders/operation-jobs/{job.Id}/detail"))!;
        foreach (var task in detail.Tasks.OrderBy(t => t.Position).Take(3))
        {
            var response = await client.PutAsJsonAsync($"/api/orders/operation-tasks/{task.Id}", new SaveOperationTaskRequest(job.Id, task.Title, true, task.Note, task.Version));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
    }

    private static async Task CompleteJobAsync(HttpClient client, Guid id)
    {
        var detail = (await client.GetFromJsonAsync<OperationJobDetailDto>($"/api/orders/operation-jobs/{id}/detail"))!;
        if (detail.Job.Status is "Planned" or "AwaitingAssignment")
        {
            var start = await client.PostAsJsonAsync($"/api/orders/operation-jobs/{id}/start", new OperationJobActionRequest(detail.Job.Version));
            Assert.True(start.StatusCode == HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
            detail = (await start.Content.ReadFromJsonAsync<OperationJobDetailDto>())!;
        }

        foreach (var task in detail.Tasks.Where(t => !t.IsDone))
        {
            var response = await client.PutAsJsonAsync($"/api/orders/operation-tasks/{task.Id}", new SaveOperationTaskRequest(id, task.Title, true, task.Note, task.Version));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        detail = (await client.GetFromJsonAsync<OperationJobDetailDto>($"/api/orders/operation-jobs/{id}/detail"))!;
        var complete = await client.PostAsJsonAsync($"/api/orders/operation-jobs/{id}/complete", new OperationJobActionRequest(detail.Job.Version));
        Assert.True(complete.StatusCode == HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
    }

    private static SaveHandoverRequest Full(long version, Guid responsible) => new(
        version, Scope: "Sunucu cihazlarının kurulumu", Deliverables: "Kurulu cihazlar ve rapor", Location: "Gebze depo", ContactName: "Ali Veli", ContactPhone: "0532 000 00 00",
        TargetStartDate: new DateOnly(2026, 12, 1), TargetEndDate: new DateOnly(2026, 12, 15), Priority: "High", ResponsibleUserId: responsible,
        CompletionCriteria: "Cihazlar çalışır durumda", AcceptanceCriteria: "Müşteri kabul tutanağını imzalar");

    /// <summary>Onaylı sipariş (bir ürün + bir hizmet kalemi) → hazır devir → gönderim → operasyon kabulü.</summary>
    private async Task<OrderDto> AcceptedOrderAsync(Setup s, HttpClient sales, HttpClient ops)
    {
        var order = await ApprovedOrderAsync(s, sales);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OrderLines SET Kind = 'Product' WHERE OrderId = {0} AND Name = 'Sunucu'", order.Id);
        var created = await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/handover", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = (await created.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
        var saved = await sales.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, s.Ops.Id));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        detail = (await sales.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{order.Id}/handover"))!;
        foreach (var item in detail.Items)
        {
            var done = await sales.PutAsJsonAsync($"/api/orders/handover-items/{item.Id}", new SaveHandoverItemRequest(detail.Handover.Id, item.Text, true, null, item.Version));
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        detail = (await sales.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{order.Id}/handover"))!;
        var sent = await sales.PostAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}/send", new HandoverActionRequest(detail.Handover.Version));
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var sentDetail = (await sent.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
        var accepted = await ops.PostAsJsonAsync($"/api/orders/handovers/{sentDetail.Handover.Id}/accept", new HandoverActionRequest(sentDetail.Handover.Version, "Kabul"));
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        return order;
    }

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<OrderDto> ApprovedOrderAsync(Setup s, HttpClient sales)
    {
        var accepted = await AcceptedQuoteAsync(sales, s.CompanyId);
        var response = await sales.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        using var approver = await LoginAsync(s.Approver);
        await TransitionAsync(sales, order.Id, "submit-for-approval");
        return await TransitionAsync(approver, order.Id, "approve");
    }

    private static async Task<OrderDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/orders/orders/{id}/{action}", new OrderTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private static async Task<QuoteDto> AcceptedQuoteAsync(HttpClient client, Guid companyId)
    {
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Cihaz ve kurulum teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price) in new[] { ("Sunucu", 2m, 1000m), ("Kurulum", 1m, 300m) })
        {
            var added = await client.PostAsJsonAsync("/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, name, "Free", null, quantity, price, null, "None", 0m, 20m, null, "Standard"));
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        await QuoteTransitionAsync(client, quote.Id, "present");
        return await QuoteTransitionAsync(client, quote.Id, "record-accepted");
    }

    private static async Task<QuoteDto> QuoteTransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private sealed record Setup(
        Tenant Tenant, ModulixUser Admin, ModulixUser Sales, ModulixUser Approver, ModulixUser Ops, ModulixUser Closer, ModulixUser Reader, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "orders", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var sales = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var ops = await factory.CreateUserAsync(tenant);
        var closer = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] quoteWork = [Crm.CompaniesRead, Crm.QuotesRead, Crm.QuotesWrite, Crm.QuotesPresent, Crm.QuotesRecordDecision, Crm.QuotesManualPrice];
        string[] activityWork = [Crm.ActivitiesRead, Crm.ActivitiesWrite, Crm.ActivitiesAssign];
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersCreate, OrdersWrite, FinancialsRead, HandoversWrite, HandoversSend, .. quoteWork, .. activityWork], Scope.All, sales);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, FinancialsRead, OrdersApprove, OrdersCancel], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, HandoversAccept, JobsAssign, JobsManage, FulfillmentRecord, .. activityWork], Scope.All, ops);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersClose], Scope.All, closer);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var companyId = (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id;
        return new Setup(tenant, admin, sales, approver, ops, closer, reader, companyId);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
