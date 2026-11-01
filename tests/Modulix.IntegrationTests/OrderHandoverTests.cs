using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Orders;
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
/// ADR-0023 dilim 3: yapılandırılmış satış devri. Kontrol listesi ve zorunlu bilgi denetimi, gönderim/bilgi isteği/yeniden gönderim/kabul akışı ve geçmişi,
/// devir kilidi, takip aktiviteleri, kabul bekleme gecikmesi (tekrarsız), tenant izolasyonu ve yetki.
/// </summary>
public class OrderHandoverTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Handover_gets_a_default_checklist_and_becomes_ready_only_when_information_and_required_items_are_complete()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        var order = await ApprovedOrderAsync(s, sales);

        var detail = await CreateHandoverAsync(sales, order.Id);
        Assert.Equal(("Preparing", 0), (detail.Handover.Status, detail.Handover.Round));
        Assert.Equal(8, detail.Items.Count);
        Assert.All(detail.Items, i => Assert.True(i.IsRequired && i.IsDefault && !i.IsDone));
        Assert.True(detail.Missing.Count >= 10);
        Assert.Equal("Preparing", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.HandoverStatus);

        // Sipariş başına tek devir kaydı.
        Assert.Equal(HttpStatusCode.Conflict, (await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/handover", new { })).StatusCode);

        // Bilgiler tamam ama kontrol listesi bitmedi: hâlâ hazırlanıyor; eksik listesi maddeleri sayar.
        detail = await SaveAsync(sales, order.Id, Full(detail.Handover.Version, s.Ops.Id));
        Assert.Equal("Preparing", detail.Handover.Status);
        Assert.All(detail.Missing, m => Assert.StartsWith("Kontrol listesi maddesi tamamlanmadı", m));
        Assert.Equal(8, detail.Missing.Count);

        // Maddeler tamamlandıkça eksik azalır; sonuncusu devri Hazır yapar ve sipariş devir durumuna yansır.
        foreach (var item in detail.Items)
        {
            await ToggleAsync(sales, detail.Handover.Id, item, done: true);
        }

        detail = await GetHandoverAsync(sales, order.Id);
        Assert.Equal(("Ready", 0), (detail.Handover.Status, detail.Missing.Count));
        Assert.All(detail.Items, i => Assert.True(i.IsDone && i.DoneBy != null && i.DoneAt != null));
        Assert.Equal("Ready", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.HandoverStatus);

        // Zorunlu bir bilgi silinirse ya da yeni madde eklenirse tekrar Hazırlanıyor olur.
        var cleared = await sales.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, s.Ops.Id) with { Location = null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        detail = await GetHandoverAsync(sales, order.Id);
        Assert.Equal("Preparing", detail.Handover.Status);
        Assert.Contains(detail.Missing, m => m.Contains("Lokasyon"));
        detail = await SaveAsync(sales, order.Id, Full(detail.Handover.Version, s.Ops.Id));
        Assert.Equal("Ready", detail.Handover.Status);

        var added = await sales.PostAsJsonAsync("/api/orders/handover-items", new SaveHandoverItemRequest(detail.Handover.Id, "Müşteri kapı giriş izni alındı"));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        detail = await GetHandoverAsync(sales, order.Id);
        Assert.Equal(("Preparing", 9), (detail.Handover.Status, detail.Items.Count));
        var custom = detail.Items.Single(i => !i.IsDefault);
        await ToggleAsync(sales, detail.Handover.Id, custom, done: true);
        Assert.Equal("Ready", (await GetHandoverAsync(sales, order.Id)).Handover.Status);
    }

    [Fact]
    public async Task Incomplete_or_unapproved_handover_cannot_be_sent_and_only_senders_can_send()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var draft = await NewOrderAsync(s, sales);
        var approved = await ApprovedOrderAsync(s, sales);

        // Eksik bilgi: 409 ve ilk eksik açıkça söylenir.
        var incomplete = await CreateHandoverAsync(sales, approved.Id);
        var blocked = await PostActionAsync(sales, incomplete.Handover.Id, "send", incomplete.Handover.Version);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Zorunlu bilgi", await blocked.Content.ReadAsStringAsync());
        Assert.Equal("NotStarted", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{draft.Id}"))!.HandoverStatus);

        // Onaylanmamış siparişin devri hazırlanabilir ama operasyona gönderilemez.
        var draftHandover = await ReadyHandoverAsync(s, sales, draft.Id);
        var notApproved = await PostActionAsync(sales, draftHandover.Handover.Id, "send", draftHandover.Handover.Version);
        Assert.Equal(HttpStatusCode.Conflict, notApproved.StatusCode);
        Assert.Contains("onaylanmış siparişten", await notApproved.Content.ReadAsStringAsync());

        // Gönderme yetkisi olmayan operasyon gönderemez; hazır devri satış gönderir.
        var ready = await ReadyHandoverAsync(s, sales, approved.Id, existing: incomplete);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(ops, ready.Handover.Id, "send", ready.Handover.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(sales, ready.Handover.Id, "accept", ready.Handover.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(ops, ready.Handover.Id, "accept", ready.Handover.Version)).StatusCode);
        var sent = await ActAsync(sales, ready.Handover.Id, "send", ready.Handover.Version);
        Assert.Equal("Sent", sent.Handover.Status);
    }

    [Fact]
    public async Task Handover_flows_send_info_request_resubmit_and_accept_with_history_locks_and_follow_ups()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await ApprovedOrderAsync(s, sales);
        var ready = await ReadyHandoverAsync(s, sales, order.Id);

        // Gönderim: tur, gönderen, tarih; sipariş devir durumu; operasyon için takip aktivitesi.
        var sent = await ActAsync(sales, ready.Handover.Id, "send", ready.Handover.Version);
        Assert.Equal(("Sent", 1, s.Sales.Id), (sent.Handover.Status, sent.Handover.Round, sent.Handover.SentBy));
        Assert.NotNull(sent.Handover.SentAt);
        Assert.Equal("Sent", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.HandoverStatus);
        Assert.NotNull(sent.Handover.OperationsActivityId);
        var opsActivity = await ActivityAsync(ops, sent.Handover.OperationsActivityId!.Value);
        Assert.Contains(order.Number, opsActivity.Subject);
        Assert.Equal(s.Ops.Id, opsActivity.OwnerId);

        // Gönderilen devir kilitli: alanlar ve maddeler değişmez.
        var locked = await sales.PutAsJsonAsync($"/api/orders/handovers/{sent.Handover.Id}", Full(sent.Handover.Version, s.Ops.Id) with { Scope = "Habersiz değişiklik" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, locked.StatusCode);
        Assert.Contains("karara bağlanana kadar", await locked.Content.ReadAsStringAsync());
        var item = sent.Items[0];
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await sales.PutAsJsonAsync($"/api/orders/handover-items/{item.Id}",
            new SaveHandoverItemRequest(sent.Handover.Id, item.Text, false, null, item.Version))).StatusCode);

        // Operasyon gerekçeyle bilgi ister (gerekçesiz olmaz); satış isteyemez.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(ops, sent.Handover.Id, "request-info", sent.Handover.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(sales, sent.Handover.Id, "request-info", sent.Handover.Version, "Kendi")).StatusCode);
        var info = await ActAsync(ops, sent.Handover.Id, "request-info", sent.Handover.Version, "Kurulum için bina giriş kartı gerekli mi?");
        Assert.Equal(("InfoRequested", "Kurulum için bina giriş kartı gerekli mi?"), (info.Handover.Status, info.Handover.InfoRequest));
        Assert.Equal("InfoRequested", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.HandoverStatus);
        Assert.NotNull(info.Handover.SalesActivityId);
        Assert.Equal(s.Sales.Id, (await ActivityAsync(sales, info.Handover.SalesActivityId!.Value)).OwnerId);

        // Satış tamamlar (düzenleme yeniden açılır) ve yeniden gönderir: neyin tamamlandığı yazılmalı; tur artar.
        var edit = await sales.PutAsJsonAsync($"/api/orders/handovers/{info.Handover.Id}", Full(info.Handover.Version, s.Ops.Id) with { CustomerPrerequisites = "Giriş kartı müşteri güvenliğinden alınır" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var again = await GetHandoverAsync(sales, order.Id);
        Assert.Equal("InfoRequested", again.Handover.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(sales, again.Handover.Id, "send", again.Handover.Version)).StatusCode);
        var resent = await ActAsync(sales, again.Handover.Id, "send", again.Handover.Version, "Giriş kartı bilgisi ön koşullara eklendi");
        Assert.Equal(("Sent", 2), (resent.Handover.Status, resent.Handover.Round));

        // Operasyon kabul eder ve sorumluyu atar; kabul edilen devir değişmez.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(sales, resent.Handover.Id, "accept", resent.Handover.Version)).StatusCode);
        var accepted = (await (await ops.PostAsJsonAsync($"/api/orders/handovers/{resent.Handover.Id}/accept",
            new HandoverActionRequest(resent.Handover.Version, "Kabul edildi", s.Ops.Id))).Content.ReadFromJsonAsync<HandoverDetailDto>())!;
        Assert.Equal(("Accepted", s.Ops.Id, s.Ops.Id), (accepted.Handover.Status, accepted.Handover.AcceptedBy, accepted.Handover.ResponsibleUserId));
        Assert.NotNull(accepted.Handover.AcceptedAt);
        Assert.Equal("Accepted", (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!.HandoverStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await sales.PutAsJsonAsync($"/api/orders/handovers/{accepted.Handover.Id}", Full(accepted.Handover.Version, s.Ops.Id) with { Scope = "Kabul sonrası" })).StatusCode);
        Assert.Contains("Kabul edilen devir değiştirilemez", await (await sales.PutAsJsonAsync($"/api/orders/handovers/{accepted.Handover.Id}", Full(accepted.Handover.Version, s.Ops.Id))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(ops, accepted.Handover.Id, "accept", accepted.Handover.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(sales, accepted.Handover.Id, "send", accepted.Handover.Version)).StatusCode);

        // Geçmiş: gönderen, kabul eden, bilgi talepleri ve yeniden gönderim.
        var history = (await ops.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/handovers/{accepted.Handover.Id}/detail"))!.History;
        Assert.Equal(["Created", "Sent", "InfoRequested", "Resubmitted", "Accepted"], history.Select(h => h.Kind));
        Assert.Equal([0, 1, 1, 2, 2], history.Select(h => h.Round));
        Assert.Equal([s.Sales.Id, s.Sales.Id, s.Ops.Id, s.Sales.Id, s.Ops.Id], history.Select(h => h.ActorId!.Value));
        Assert.Equal("Kurulum için bina giriş kartı gerekli mi?", history[2].Note);
        var orderHistory = (await sales.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Equal(["Preparing", "Ready", "Sent", "InfoRequested", "Sent", "Accepted"], orderHistory.Where(h => h.Kind == "HandoverChanged").Select(h => h.ToStatus));
    }

    [Fact]
    public async Task Checklist_rules_protect_default_items_and_other_tenants_and_unauthorized_users_are_kept_out()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var sales = await LoginAsync(a.Sales);
        using var ops = await LoginAsync(a.Ops);
        using var reader = await LoginAsync(a.Reader);
        using var own = await LoginAsync(a.OwnOnly);
        using var adminB = await LoginAsync(b.Admin);
        var order = await ApprovedOrderAsync(a, sales);
        var detail = await CreateHandoverAsync(sales, order.Id);
        var defaultItem = detail.Items[0];

        // Varsayılan madde: metin değişmez, silinmez; tamamlandı işaretlenebilir. Kullanıcı maddesi silinebilir.
        var rename = await sales.PutAsJsonAsync($"/api/orders/handover-items/{defaultItem.Id}", new SaveHandoverItemRequest(detail.Handover.Id, "Başka metin", false, null, defaultItem.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sales.DeleteAsync($"/api/orders/handover-items/{defaultItem.Id}?version={defaultItem.Version}")).StatusCode);
        var added = (await (await sales.PostAsJsonAsync("/api/orders/handover-items", new SaveHandoverItemRequest(detail.Handover.Id, "Ek kontrol"))).Content.ReadFromJsonAsync<HandoverItemDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await sales.DeleteAsync($"/api/orders/handover-items/{added.Id}?version={added.Version}")).StatusCode);
        Assert.Equal(8, (await GetHandoverAsync(sales, order.Id)).Items.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await sales.DeleteAsync($"/api/orders/handovers/{detail.Handover.Id}?version={detail.Handover.Version}")).StatusCode);

        // Genel uçtan devir açılamaz; durum ve tur alanları yazılamaz.
        Assert.NotEqual(HttpStatusCode.Created, (await sales.PostAsJsonAsync("/api/orders/handovers", new SaveHandoverRequest(0, Scope: "Sahte"))).StatusCode);
        var forged = await sales.PutAsJsonAsync($"/api/records/orders.handover/{detail.Handover.Id}", new { version = detail.Handover.Version, values = new Dictionary<string, object> { ["status"] = "Accepted", ["round"] = 9 } });
        var afterForge = (await GetHandoverAsync(sales, order.Id)).Handover;
        Assert.Equal(("Preparing", 0), (afterForge.Status, afterForge.Round));
        _ = forged;

        // Başka tenant: devir, madde ve eylem uçları 404.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/orders/{order.Id}/handover")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/orders/{order.Id}/handover", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/handovers/{detail.Handover.Id}/detail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}/accept", new HandoverActionRequest(detail.Handover.Version))).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<HandoverItemDto>>($"/api/orders/handover-items?handoverId={detail.Handover.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, b.Ops.Id))).StatusCode);

        // Yalnızca okuyucu devri görür ama yazamaz; operasyon yazamaz; "kendi" kapsamı başkasının devrini göremez.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/orders/orders/{order.Id}/handover")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, a.Ops.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/orders/handover-items", new SaveHandoverItemRequest(detail.Handover.Id, "Sızma"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, a.Ops.Id))).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{order.Id}/handover"))!.Actions);
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/orders/orders/{order.Id}/handover")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/orders/handovers/{detail.Handover.Id}/detail")).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<HandoverItemDto>>($"/api/orders/handover-items?handoverId={detail.Handover.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/orders/{order.Id}/handover", new { })).StatusCode);
    }

    [Fact]
    public async Task A_handover_waiting_too_long_gets_one_follow_up_per_round_and_the_job_can_be_repeated_safely()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var admin = await LoginAsync(s.Admin);
        var order = await ApprovedOrderAsync(s, sales);
        var ready = await ReadyHandoverAsync(s, sales, order.Id);
        var sent = await ActAsync(sales, ready.Handover.Id, "send", ready.Handover.Version);
        var runner = factory.Services.GetRequiredService<OrderJobsRunner>();

        // Bekleme süresi dolmadan tur hiçbir şey üretmez.
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);

        // 72 saat önce gönderilmiş gibi: ilk tur bir takip açar, ikinci tur (yeniden deneme) hiçbir şey eklemez.
        await SqlAsync(s.Tenant.Id, "UPDATE orders.Handovers SET SentAt = {0} WHERE Id = {1}", DateTimeOffset.UtcNow.AddHours(-72), sent.Handover.Id);
        Assert.Equal(1, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);

        var activities = (await admin.GetFromJsonAsync<PagedResult<ActivityDto>>("/api/crm/activities?pageSize=200"))!.Items.Where(a => a.Subject.Contains("Devir kabulü gecikti")).ToList();
        Assert.Equal(s.Sales.Id, Assert.Single(activities).OwnerId);
        var detail = await GetHandoverAsync(sales, order.Id);
        Assert.Equal(1, detail.Handover.EscalatedRound);
        Assert.Single(detail.History, h => h.Kind == "WaitingEscalated");

        // Yeni tur (bilgi isteği ve yeniden gönderim) için gecikme yeniden izlenir; süre kapalıysa (0) izlenmez.
        using var ops = await LoginAsync(s.Ops);
        var info = await ActAsync(ops, sent.Handover.Id, "request-info", detail.Handover.Version, "Eksik");
        var resent = await ActAsync(sales, info.Handover.Id, "send", info.Handover.Version, "Tamamlandı");
        await SqlAsync(s.Tenant.Id, "UPDATE orders.Handovers SET SentAt = {0} WHERE Id = {1}", DateTimeOffset.UtcNow.AddHours(-72), resent.Handover.Id);
        await SetSettingAsync(admin, "orders.handover.wait-hours", "0");
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);
        await SetSettingAsync(admin, "orders.handover.wait-hours", "48");
        Assert.Equal(1, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.HandoverEscalations);
        Assert.Equal(2, (await GetHandoverAsync(sales, order.Id)).Handover.EscalatedRound);

        // Sipariş modülü kapalı tenant için iş çalışmaz.
        await factory.SetModuleAsync(s.Tenant.Id, "orders", enabled: false);
        Assert.Null((await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run);
    }

    // ---- yardımcılar

    private static SaveHandoverRequest Full(long version, Guid responsible) => new(
        version, Scope: "Sunucu cihazlarının kurulumu ve devreye alınması", OutOfScope: "Ağ altyapısı", Deliverables: "Kurulu cihazlar ve kurulum raporu",
        Commitments: "Kurulum 3 iş gününde tamamlanır", Location: "Gebze depo", ContactName: "Ali Veli", ContactPhone: "0532 000 00 00", ContactEmail: "ali@example.com",
        TargetStartDate: new DateOnly(2026, 12, 1), TargetEndDate: new DateOnly(2026, 12, 15), Priority: "High", RequiredDocuments: "Kurulum izin formu",
        PaymentConditions: "Avans alındı", Risks: "Elektrik altyapısı", ResponsibleUserId: responsible, CompletionCriteria: "Cihazlar çalışır durumda",
        AcceptanceCriteria: "Müşteri kabul tutanağını imzalar");

    private static async Task<HandoverDetailDto> CreateHandoverAsync(HttpClient client, Guid orderId)
    {
        var response = await client.PostAsJsonAsync($"/api/orders/orders/{orderId}/handover", new { });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
    }

    private static async Task<HandoverDetailDto> GetHandoverAsync(HttpClient client, Guid orderId) =>
        (await client.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{orderId}/handover"))!;

    private static async Task<HandoverDetailDto> SaveAsync(HttpClient client, Guid orderId, SaveHandoverRequest request)
    {
        var current = await GetHandoverAsync(client, orderId);
        var response = await client.PutAsJsonAsync($"/api/orders/handovers/{current.Handover.Id}", request with { Version = current.Handover.Version });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await GetHandoverAsync(client, orderId);
    }

    private static async Task ToggleAsync(HttpClient client, Guid handoverId, HandoverItemDto item, bool done)
    {
        var response = await client.PutAsJsonAsync($"/api/orders/handover-items/{item.Id}", new SaveHandoverItemRequest(handoverId, item.Text, done, item.Note, item.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Devir kaydını açar (yoksa), bilgileri doldurur ve kontrol listesini tamamlar: Hazır.</summary>
    private static async Task<HandoverDetailDto> ReadyHandoverAsync(Setup s, HttpClient sales, Guid orderId, HandoverDetailDto? existing = null)
    {
        var detail = existing ?? await CreateHandoverAsync(sales, orderId);
        detail = await SaveAsync(sales, orderId, Full(detail.Handover.Version, s.Ops.Id));
        foreach (var item in detail.Items)
        {
            await ToggleAsync(sales, detail.Handover.Id, item, done: true);
        }

        detail = await GetHandoverAsync(sales, orderId);
        Assert.Equal("Ready", detail.Handover.Status);
        return detail;
    }

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, Guid handoverId, string action, long version, string? note = null) =>
        client.PostAsJsonAsync($"/api/orders/handovers/{handoverId}/{action}", new HandoverActionRequest(version, note));

    private static async Task<HandoverDetailDto> ActAsync(HttpClient client, Guid handoverId, string action, long version, string? note = null)
    {
        var response = await PostActionAsync(client, handoverId, action, version, note);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
    }

    private static async Task<ActivityDto> ActivityAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<ActivityDto>($"/api/crm/activities/{id}"))!;

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
        var order = await NewOrderAsync(s, sales);
        using var approver = await LoginAsync(s.Approver);
        await TransitionAsync(sales, order.Id, "submit-for-approval");
        return await TransitionAsync(approver, order.Id, "approve");
    }

    private async Task<OrderDto> NewOrderAsync(Setup s, HttpClient client)
    {
        var accepted = await AcceptedQuoteAsync(client, s.CompanyId);
        var response = await client.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
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
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Cihaz ve montaj teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price) in new[] { ("Cihaz", 4m, 1000m), ("Montaj", 1m, 300m) })
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
        Tenant Tenant, ModulixUser Admin, ModulixUser Sales, ModulixUser Approver, ModulixUser Ops, ModulixUser Reader, ModulixUser OwnOnly, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "orders", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var sales = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var ops = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var ownOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] quoteWork = [Crm.CompaniesRead, Crm.QuotesRead, Crm.QuotesWrite, Crm.QuotesPresent, Crm.QuotesRecordDecision, Crm.QuotesManualPrice];
        string[] activityWork = [Crm.ActivitiesRead, Crm.ActivitiesWrite, Crm.ActivitiesAssign];
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersCreate, OrdersWrite, FinancialsRead, HandoversWrite, HandoversSend, .. quoteWork, .. activityWork], Scope.All, sales);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, FinancialsRead, OrdersApprove], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, HandoversAccept, .. activityWork], Scope.All, ops);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, Crm.ActivitiesRead], Scope.Own, ownOnly);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var companyId = (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id;
        return new Setup(tenant, admin, sales, approver, ops, reader, ownOnly, companyId);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
