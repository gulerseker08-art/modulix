using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Orders;
using Modulix.Shared.Records;
using static Modulix.Modules.Orders.OrdersModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0023 dilim 2: sipariş ticari durum geçişleri (sunucuda doğrulanır), dört göz, iptal ve tüketim defteri, kapatma, onaylanmış siparişin
/// değişiklik talebi (etki, uygulama, ret, geri çekme), tenant izolasyonu ve yetki.
/// </summary>
public class OrderLifecycleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Order_moves_through_approval_with_four_eyes_and_locks_commercial_fields_once_approved()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        var order = await NewOrderAsync(sales, s.CompanyId);

        // Kuralı sunucu uygular: taslakta yalnızca gönderme/iptal; onay yok.
        var actions = (await sales.GetFromJsonAsync<List<OrderActionDto>>($"/api/orders/orders/{order.Id}/actions"))!;
        Assert.Equal(["submit-for-approval", "withdraw-approval"], actions.Select(a => a.Action));
        Assert.True(actions.Single(a => a.Action == "submit-for-approval").Allowed);
        Assert.False(actions.Single(a => a.Action == "withdraw-approval").Allowed);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(sales, order.Id, "withdraw-approval", order.Version, "x")).StatusCode);

        var pending = await TransitionAsync(sales, order.Id, "submit-for-approval");
        Assert.Equal("PendingApproval", pending.Status);

        // Onay bekleyen siparişin ticari alanları değişmez.
        var edit = await sales.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(pending.Version, Title: "Habersiz değişiklik", OwnerId: pending.OwnerId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);

        // Onay yetkisi olmayan onaylayamaz (403); gönderen yetkili olsa da kendi gönderdiğini onaylayamaz (dört göz).
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(sales, order.Id, "approve", pending.Version)).StatusCode);
        var selfActions = (await approver.GetFromJsonAsync<List<OrderActionDto>>($"/api/orders/orders/{order.Id}/actions"))!;
        Assert.True(selfActions.Single(a => a.Action == "approve").Allowed);
        using var admin = await LoginAsync(s.Admin);
        var adminOrder = await NewOrderAsync(admin, s.CompanyId);
        var adminPending = await TransitionAsync(admin, adminOrder.Id, "submit-for-approval");
        var self = await PostAsync(admin, adminOrder.Id, "approve", adminPending.Version);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("Kendi", await self.Content.ReadAsStringAsync());
        Assert.False((await admin.GetFromJsonAsync<List<OrderActionDto>>($"/api/orders/orders/{adminOrder.Id}/actions"))!.Single(a => a.Action == "approve").Allowed);

        // Onaylayıcı geri gönderir (gerekçe zorunlu), satış yeniden gönderir, onaylayıcı onaylar.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(approver, order.Id, "return-to-draft", pending.Version)).StatusCode);
        var returned = await TransitionAsync(approver, order.Id, "return-to-draft", "Teslim tarihi eksik");
        Assert.Equal(("Draft", "Teslim tarihi eksik"), (returned.Status, returned.StatusReason));
        var again = await TransitionAsync(sales, order.Id, "submit-for-approval");
        var approved = await TransitionAsync(approver, order.Id, "approve");
        Assert.Equal("Approved", approved.Status);

        // Onaylanmış siparişin ticari alanları kapalıdır; stale sürümle işlem 409.
        var locked = await sales.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(approved.Version, Title: "Kontrolsüz", OwnerId: approved.OwnerId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, locked.StatusCode);
        Assert.Contains("değişiklik talebi", await locked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(approver, order.Id, "cancel", again.Version, "Eski sürüm")).StatusCode);

        var history = (await sales.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Equal(["CreatedFromQuote", "SubmittedForApproval", "ReturnedToDraft", "SubmittedForApproval", "Approved"], history.Select(h => h.Kind));
        Assert.Equal(("Draft/NotStarted/NotStarted", "PendingApproval/NotStarted/NotStarted"), (history[1].FromStatus, history[1].ToStatus));
    }

    [Fact]
    public async Task Withdrawal_needs_a_reason_and_returns_a_pending_order_to_draft()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        var order = await NewOrderAsync(sales, s.CompanyId);
        var pending = await TransitionAsync(sales, order.Id, "submit-for-approval");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(sales, order.Id, "withdraw-approval", pending.Version)).StatusCode);
        var draft = await TransitionAsync(sales, order.Id, "withdraw-approval", "Fiyat teyidi bekleniyor");

        Assert.Equal(("Draft", "Fiyat teyidi bekleniyor"), (draft.Status, draft.StatusReason));
        var again = await sales.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(draft.Version, Title: "Tekrar düzenlenebilir", OwnerId: draft.OwnerId));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Cancelling_needs_reason_and_permission_keeps_history_and_frees_the_quote_lines_for_a_new_order()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        var accepted = await AcceptedQuoteAsync(sales, s.CompanyId, ("Cihaz", 4m, 1000m, 20m), ("Montaj", 1m, 300m, 0m));
        var order = await CreateOrderAsync(sales, accepted.Id);

        // Yetki ve gerekçe.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(sales, order.Id, "cancel", order.Version, "Vazgeçti")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(approver, order.Id, "cancel", order.Version)).StatusCode);

        // Teklif kalemleri siparişte olduğu için ikinci tam dönüşüm çakışır; iptalden sonra serbest kalır.
        Assert.Equal(HttpStatusCode.Conflict, (await sales.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest())).StatusCode);
        var cancelled = await TransitionAsync(approver, order.Id, "cancel", "Müşteri vazgeçti");
        Assert.Equal(("Cancelled", "Müşteri vazgeçti"), (cancelled.Status, cancelled.StatusReason));

        // Sipariş ve kalemleri silinmez; kalemler iptal edilmiş görünür, tutar kopyası (snapshot) korunur.
        var lines = (await sales.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items;
        Assert.Equal([4m, 1m], lines.Select(l => l.CancelledQuantity));
        Assert.All(lines, l => Assert.True(l.IsCancelled));
        Assert.Equal([4000m, 300m], lines.Select(l => l.Net));
        Assert.Equal(5100m, (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.GrossTotal - 0m);

        var freed = (await sales.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.All(freed.Lines, l => Assert.Equal((0m, l.AcceptedQuantity), (l.ConvertedQuantity, l.RemainingQuantity)));
        var second = await CreateOrderAsync(sales, accepted.Id);
        Assert.NotEqual(order.Id, second.Id);

        // İptal sonuç durumudur: ikinci iptal ve onay yok; sipariş artık düzenlenemez.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(approver, order.Id, "cancel", cancelled.Version, "Tekrar")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await sales.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(cancelled.Version, Title: "Ölü sipariş", OwnerId: cancelled.OwnerId))).StatusCode);
        Assert.Equal(2, (await sales.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?sourceQuoteId={accepted.Id}"))!.TotalCount);
        var history = (await sales.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Equal("Cancelled", history.Last().Kind);
    }

    [Fact]
    public async Task Approved_order_can_be_cancelled_and_cancellation_withdraws_a_pending_change_request()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        var order = await ApprovedOrderAsync(sales, approver, s.CompanyId);
        var request = await CreateChangeRequestAsync(sales, order.Id, new OrderChangeRequestInputDto("Adres değişti", new Dictionary<string, string?> { ["location"] = "Kocaeli" }));
        Assert.Equal("Pending", request.Status);

        var cancelled = await TransitionAsync(approver, order.Id, "cancel", "Proje iptal");

        Assert.Equal("Cancelled", cancelled.Status);
        var requests = (await sales.GetFromJsonAsync<List<OrderChangeRequestDto>>($"/api/orders/orders/{order.Id}/change-requests"))!;
        Assert.Equal(("Withdrawn", "Sipariş iptal edildi."), (requests.Single().Status, requests.Single().DecisionNote));
    }

    [Fact]
    public async Task Closing_needs_completed_fulfillment_and_force_close_needs_its_own_permission_and_a_reason()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        using var closer = await LoginAsync(s.Closer);
        using var admin = await LoginAsync(s.Admin);
        var draft = await NewOrderAsync(sales, s.CompanyId);
        var order = await ApprovedOrderAsync(sales, approver, s.CompanyId);

        // Taslak kapatılamaz; onaylı ama gerçekleşmesi tamamlanmamış sipariş normal kapatılamaz (kalan yükümlülük).
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(closer, draft.Id, "close", draft.Version)).StatusCode);
        var blocked = await PostAsync(closer, order.Id, "close", order.Version);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("kalan yükümlülük", await blocked.Content.ReadAsStringAsync());

        // Zorla kapatma ayrı yetki ve gerekçe ister.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(closer, order.Id, "force-close", order.Version, "Acil")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(admin, order.Id, "force-close", order.Version)).StatusCode);
        var closed = await TransitionAsync(admin, order.Id, "force-close", "Müşteri teslimatı kendi aldı");

        Assert.Equal(("Closed", "Müşteri teslimatı kendi aldı"), (closed.Status, closed.StatusReason));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(admin, order.Id, "cancel", closed.Version, "Kapalı iptal edilemez")).StatusCode);
        Assert.Equal("ForceClosed", (await admin.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!.Last().Kind);
    }

    [Fact]
    public async Task Change_request_on_an_approved_order_shows_impact_and_is_applied_only_after_a_different_approver_decides()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        var accepted = await AcceptedQuoteAsync(sales, s.CompanyId, ("Cihaz", 4m, 1000m, 20m), ("Montaj", 1m, 300m, 0m));
        var order = await CreateOrderAsync(sales, accepted.Id, new CreateOrderFromQuoteRequest(PlannedDeliveryDate: new DateOnly(2026, 12, 15)));
        order = await ApproveAsync(sales, approver, order);
        var lines = (await sales.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items;
        var device = lines.Single(l => l.Name == "Cihaz");

        var input = new OrderChangeRequestInputDto("Müşteri 1 cihazdan vazgeçti, teslim ertelendi",
            new Dictionary<string, string?> { ["plannedDeliveryDate"] = "2027-01-10", ["deliveryAddress"] = "Yeni depo, Gebze" },
            [new OrderLineChangeDto(device.Id, 3m)]);

        // Önizleme yazmaz; etkiyi ve tutar farkını gösterir.
        var preview = (await (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests/preview", input)).Content.ReadFromJsonAsync<OrderChangePreviewDto>())!;
        Assert.Equal((-1000m, -200m), (preview.NetDelta, preview.TaxDelta));
        Assert.Contains(preview.Deliveries, d => d.Contains("Cihaz") && d.Contains("4") && d.Contains("3"));
        Assert.Contains(preview.Deliveries, d => d.Contains("2026-12-15") && d.Contains("2027-01-10"));
        Assert.Empty((await sales.GetFromJsonAsync<List<OrderChangeRequestDto>>($"/api/orders/orders/{order.Id}/change-requests"))!);

        var request = await CreateChangeRequestAsync(sales, order.Id, input);
        Assert.Equal(("Pending", 1), (request.Status, request.Sequence));
        Assert.Equal((-1000m, -200m), (request.NetDelta, request.TaxDelta));

        // Talep onaylanana kadar sipariş değişmez; aynı anda ikinci talep açılamaz; kendi talebini karara bağlayamaz.
        var unchanged = (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        Assert.Equal((new DateOnly(2026, 12, 15), 5100m), (unchanged.PlannedDeliveryDate, unchanged.GrossTotal));
        Assert.Equal(HttpStatusCode.Conflict, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/approve", new DecideOrderChangeRequest(request.Version))).StatusCode);

        var applied = (await (await approver.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/approve", new DecideOrderChangeRequest(request.Version, "Uygun"))).Content.ReadFromJsonAsync<OrderChangeRequestDto>())!;
        Assert.Equal(("Approved", "Uygun"), (applied.Status, applied.DecisionNote));

        // Sipariş güncellendi: tarih, adres ve toplamlar; kalem kopyası (snapshot) aynı, etkin miktar azaldı.
        var after = (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        Assert.Equal((new DateOnly(2027, 1, 10), "Yeni depo, Gebze", "Approved"), (after.PlannedDeliveryDate, after.DeliveryAddress, after.Status));
        Assert.Equal((3300m, 600m, 3900m), (after.NetTotal, after.TaxTotal, after.GrossTotal));
        var line = (await sales.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single(l => l.Name == "Cihaz");
        Assert.Equal((4m, 1m, 4000m, false), (line.Quantity, line.CancelledQuantity, line.Net, line.IsCancelled));

        // İptal edilen 1 cihaz tüketim defterinden serbest kalır: yeniden siparişe alınabilir.
        var preview2 = (await sales.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.Equal((3m, 1m), (preview2.Lines.Single(l => l.Name == "Cihaz").ConvertedQuantity, preview2.Lines.Single(l => l.Name == "Cihaz").RemainingQuantity));

        var history = (await sales.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "ChangeRequested");
        Assert.Contains(history, h => h.Kind == "ChangeApplied" && h.Reason!.Contains("Uygun"));

        // Karara bağlanmış talep tekrar karara bağlanamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/approve", new DecideOrderChangeRequest(applied.Version))).StatusCode);
    }

    [Fact]
    public async Task Change_request_can_be_rejected_with_a_reason_or_withdrawn_and_invalid_requests_are_refused()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var approver = await LoginAsync(s.Approver);
        var draft = await NewOrderAsync(sales, s.CompanyId);
        var order = await ApprovedOrderAsync(sales, approver, s.CompanyId);
        var lines = (await sales.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items;
        var device = lines.Single(l => l.Name == "Cihaz");

        // Taslak siparişe talep açılmaz; gerekçe, boş/etkisiz, artırma ve bilinmeyen alan reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PostAsJsonAsync($"/api/orders/orders/{draft.Id}/change-requests",
            new OrderChangeRequestInputDto("Neden", new Dictionary<string, string?> { ["location"] = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto(null, new Dictionary<string, string?> { ["location"] = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto("Boş", new Dictionary<string, string?>()))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto("Artır", null, [new OrderLineChangeDto(device.Id, 9m)]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto("Fiyat", new Dictionary<string, string?> { ["netTotal"] = "1" }))).StatusCode);
        Assert.Empty((await sales.GetFromJsonAsync<List<OrderChangeRequestDto>>($"/api/orders/orders/{order.Id}/change-requests"))!);

        // Ret: gerekçe zorunlu; sipariş değişmez; reddedilmiş talepten sonra yeni talep açılabilir.
        var first = await CreateChangeRequestAsync(sales, order.Id, new OrderChangeRequestInputDto("Lokasyon", new Dictionary<string, string?> { ["location"] = "Kocaeli" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await approver.PostAsJsonAsync($"/api/orders/change-requests/{first.Id}/reject", new DecideOrderChangeRequest(first.Version))).StatusCode);
        var rejected = (await (await approver.PostAsJsonAsync($"/api/orders/change-requests/{first.Id}/reject", new DecideOrderChangeRequest(first.Version, "Gerekçe yetersiz"))).Content.ReadFromJsonAsync<OrderChangeRequestDto>())!;
        Assert.Equal(("Rejected", "Gerekçe yetersiz"), (rejected.Status, rejected.DecisionNote));
        Assert.Null((await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.Location);

        // Geri çekme: bekleyen talep yazan tarafından geri çekilir.
        var second = await CreateChangeRequestAsync(sales, order.Id, new OrderChangeRequestInputDto("Not", new Dictionary<string, string?> { ["operationsNotes"] = "Kapıdan giriş" }));
        Assert.Equal(2, second.Sequence);
        var withdrawn = (await (await sales.PostAsJsonAsync($"/api/orders/change-requests/{second.Id}/withdraw", new WithdrawOrderChangeRequest(second.Version))).Content.ReadFromJsonAsync<OrderChangeRequestDto>())!;
        Assert.Equal("Withdrawn", withdrawn.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await sales.PostAsJsonAsync($"/api/orders/change-requests/{second.Id}/withdraw", new WithdrawOrderChangeRequest(withdrawn.Version))).StatusCode);

        // Talepler genel uçtan yazılamaz ve silinemez.
        Assert.NotEqual(HttpStatusCode.Created, (await sales.PostAsJsonAsync("/api/records/orders.change-request", new { values = new Dictionary<string, object> { ["orderId"] = order.Id, ["reason"] = "Sahte" } })).StatusCode);
        var requests = (await sales.GetFromJsonAsync<List<OrderChangeRequestDto>>($"/api/orders/orders/{order.Id}/change-requests"))!;
        Assert.Equal([2, 1], requests.Select(r => r.Sequence));
    }

    [Fact]
    public async Task Other_tenants_and_unauthorized_users_cannot_decide_see_amounts_or_reach_change_requests()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var sales = await LoginAsync(a.Sales);
        using var approver = await LoginAsync(a.Approver);
        using var reader = await LoginAsync(a.Reader);
        using var own = await LoginAsync(a.OwnOnly);
        using var adminB = await LoginAsync(b.Admin);
        var order = await ApprovedOrderAsync(sales, approver, a.CompanyId);
        var device = (await sales.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single(l => l.Name == "Cihaz");
        var request = await CreateChangeRequestAsync(sales, order.Id, new OrderChangeRequestInputDto("Azalt", null, [new OrderLineChangeDto(device.Id, 3m)]));

        // Başka tenant: sipariş, eylem, talep ve karar uçları 404.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/orders/{order.Id}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/orders/{order.Id}/cancel", new OrderTransitionRequest(order.Version, "Ele geçirme"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/orders/{order.Id}/change-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto("Sızma", new Dictionary<string, string?> { ["location"] = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/approve", new DecideOrderChangeRequest(request.Version))).StatusCode);

        // Yalnızca okuyucu: talep açamaz, karar veremez, eylem listesi boş; talepleri görür ama tutar farkını görmez.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/orders/{order.Id}/change-requests",
            new OrderChangeRequestInputDto("Yetkisiz", new Dictionary<string, string?> { ["location"] = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/reject", new DecideOrderChangeRequest(request.Version, "Yetkisiz"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/orders/{order.Id}/cancel", new OrderTransitionRequest(order.Version, "Yetkisiz"))).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<List<OrderActionDto>>($"/api/orders/orders/{order.Id}/actions"))!);
        var seen = (await reader.GetFromJsonAsync<List<OrderChangeRequestDto>>($"/api/orders/orders/{order.Id}/change-requests"))!.Single();
        Assert.Null(seen.NetDelta);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/reject", new DecideOrderChangeRequest(request.Version, "Kendi"))).StatusCode);

        // Veri kapsamı "kendi": başkasının siparişinin talebini göremez.
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/orders/orders/{order.Id}/change-requests")).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items);
        Assert.NotEqual(HttpStatusCode.OK, (await own.GetAsync($"/api/records/orders.change-request/{request.Id}")).StatusCode);

        // Yetkili onaylayıcı karar verir; talep durumu değişir.
        var decided = await approver.PostAsJsonAsync($"/api/orders/change-requests/{request.Id}/reject", new DecideOrderChangeRequest(request.Version, "Gerek yok"));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
    }

    // ---- yardımcılar

    private async Task<OrderDto> ApprovedOrderAsync(HttpClient sales, HttpClient approver, Guid companyId)
    {
        var accepted = await AcceptedQuoteAsync(sales, companyId, ("Cihaz", 4m, 1000m, 20m), ("Montaj", 1m, 300m, 0m));
        return await ApproveAsync(sales, approver, await CreateOrderAsync(sales, accepted.Id));
    }

    private static async Task<OrderDto> ApproveAsync(HttpClient sales, HttpClient approver, OrderDto order)
    {
        await TransitionAsync(sales, order.Id, "submit-for-approval");
        return await TransitionAsync(approver, order.Id, "approve");
    }

    private async Task<OrderDto> NewOrderAsync(HttpClient client, Guid companyId)
    {
        var accepted = await AcceptedQuoteAsync(client, companyId, ("Cihaz", 4m, 1000m, 20m), ("Montaj", 1m, 300m, 0m));
        return await CreateOrderAsync(client, accepted.Id);
    }

    private static async Task<OrderDto> CreateOrderAsync(HttpClient client, Guid quoteId, CreateOrderFromQuoteRequest? request = null)
    {
        var response = await client.PostAsJsonAsync($"/api/orders/from-quote/{quoteId}", request ?? new CreateOrderFromQuoteRequest());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string action, long version, string? reason = null) =>
        client.PostAsJsonAsync($"/api/orders/orders/{id}/{action}", new OrderTransitionRequest(version, reason));

    /// <summary>Güncel sürümü okuyup eylemi uygular ve başarıyı doğrular.</summary>
    private static async Task<OrderDto> TransitionAsync(HttpClient client, Guid id, string action, string? reason = null)
    {
        var current = (await client.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{id}"))!;
        var response = await PostAsync(client, id, action, current.Version, reason);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private static async Task<OrderChangeRequestDto> CreateChangeRequestAsync(HttpClient client, Guid orderId, OrderChangeRequestInputDto input)
    {
        var response = await client.PostAsJsonAsync($"/api/orders/orders/{orderId}/change-requests", input);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderChangeRequestDto>())!;
    }

    private static async Task<QuoteDto> AcceptedQuoteAsync(HttpClient client, Guid companyId, params (string Name, decimal Quantity, decimal Price, decimal Tax)[] lines)
    {
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Cihaz ve montaj teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price, tax) in lines)
        {
            var added = await client.PostAsJsonAsync("/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, name, "Free", null, quantity, price, null, "None", 0m, tax, null, "Standard"));
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

    private sealed record Setup(ModulixUser Admin, ModulixUser Sales, ModulixUser Approver, ModulixUser Closer, ModulixUser Reader, ModulixUser OwnOnly, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "orders", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var sales = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var closer = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var ownOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] quoteWork =
        [
            Modulix.Modules.Crm.CrmModule.Permissions.CompaniesRead, Modulix.Modules.Crm.CrmModule.Permissions.QuotesRead, Modulix.Modules.Crm.CrmModule.Permissions.QuotesWrite,
            Modulix.Modules.Crm.CrmModule.Permissions.QuotesPresent, Modulix.Modules.Crm.CrmModule.Permissions.QuotesRecordDecision, Modulix.Modules.Crm.CrmModule.Permissions.QuotesManualPrice,
        ];
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersCreate, OrdersWrite, FinancialsRead, .. quoteWork], Scope.All, sales);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, FinancialsRead, OrdersApprove, OrdersCancel], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersClose], Scope.All, closer);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, Modulix.Modules.Crm.CrmModule.Permissions.ActivitiesRead], Scope.Own, ownOnly);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        return new Setup(admin, sales, approver, closer, reader, ownOnly, (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
