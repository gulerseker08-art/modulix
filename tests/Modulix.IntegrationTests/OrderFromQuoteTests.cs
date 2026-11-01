using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
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
/// ADR-0023 dilim 1: kabul edilen teklif revizyonundan sipariş. Doğru kalem ve tutarlar, kabul kanıtı, uygun olmayan teklifin reddi,
/// çift tıklama ve eşzamanlı istek, kısmi dönüşümde kabul edilen miktarın aşılmaması, snapshot, tenant izolasyonu ve yetki.
/// </summary>
public class OrderFromQuoteTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Accepted_quote_becomes_an_order_with_the_right_lines_totals_acceptance_and_trace()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 2m, 1000m, 20m, null), ("Aylık destek", 1m, 500m, 20m, "Monthly"));

        var preview = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.Equal((accepted.QuoteNumber, 2, true, "Manual"), (preview.QuoteNumber, preview.Lines.Count, preview.CustomerLinked, preview.AcceptanceMethod));
        Assert.Empty(preview.Blockers);
        Assert.Empty(preview.ExistingOrders);
        Assert.All(preview.Lines, l => Assert.Equal(l.AcceptedQuantity, l.RemainingQuantity));

        var order = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest(
            PlannedStartDate: new DateOnly(2026, 12, 1), PlannedDeliveryDate: new DateOnly(2026, 12, 15), DeliveryAddress: "Depo 3, Gebze", Location: "Gebze"));

        Assert.StartsWith("SP-", order.Number);
        Assert.Equal((accepted.Title, "Full", "Manual"), (order.Title, order.ConversionMode, order.AcceptanceMethod));
        Assert.Equal(("Draft", "NotStarted", "NotStarted"), (order.Status, order.HandoverStatus, order.FulfillmentStatus));
        Assert.Equal((accepted.Id, accepted.QuoteNumber, accepted.RevisionNo), (order.SourceQuoteId, order.SourceQuoteNumber, order.SourceQuoteRevisionNo));
        Assert.Equal(s.AdminId, order.AcceptedByUserId);
        Assert.Equal(s.AdminId, order.OwnerId);
        Assert.Equal("Depo 3, Gebze", order.DeliveryAddress);
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15)), (order.PlannedStartDate, order.PlannedDeliveryDate));
        Assert.Equal("Anka Ltd.", order.CustomerName);
        Assert.NotEqual(Guid.Empty, order.PartyId);
        Assert.Equal((2500m, 500m, 3000m), (order.NetTotal, order.TaxTotal, order.GrossTotal));

        var lines = (await admin.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items;
        Assert.Equal(["Kurulum", "Aylık destek"], lines.Select(l => l.Name));
        Assert.Equal([2m, 1m], lines.Select(l => l.Quantity));
        Assert.Equal([2m, 1m], lines.Select(l => l.AcceptedQuantity));
        Assert.Equal([2000m, 500m], lines.Select(l => l.Net));
        Assert.Equal([400m, 100m], lines.Select(l => l.Tax));
        Assert.Equal([1000m, 500m], lines.Select(l => l.UnitPrice));
        Assert.All(lines, l => Assert.Equal((0m, false), (l.DeliveredQuantity, l.IsCancelled)));

        var history = (await admin.GetFromJsonAsync<List<OrderHistoryDto>>($"/api/orders/orders/{order.Id}/history"))!;
        Assert.Equal("CreatedFromQuote", Assert.Single(history).Kind);

        // Önizleme artık siparişi ve tükenmiş kalemleri gösterir; ikinci tam dönüşüm yapılamaz.
        var after = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.True(after.FullyConverted);
        Assert.Equal(order.Number, Assert.Single(after.ExistingOrders).Number);
        Assert.NotEmpty(after.Blockers);

        // Fırsat ve müşteri üzerinden bulunur.
        var byQuote = (await admin.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?sourceQuoteId={accepted.Id}"))!;
        Assert.Equal(order.Id, Assert.Single(byQuote.Items).Id);
        var byParty = (await admin.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?partyId={order.PartyId}"))!;
        Assert.Contains(byParty.Items, o => o.Id == order.Id);
    }

    [Fact]
    public async Task Drafts_rejected_cancelled_and_unknown_quotes_cannot_become_orders()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var draft = (await (await admin.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Taslak", s.CompanyId))).Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/orders/from-quote/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{draft.Id}", new CreateOrderFromQuoteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{Guid.NewGuid()}", new CreateOrderFromQuoteRequest())).StatusCode);

        // Reddedilmiş teklif.
        var rejected = await PresentedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var rejectNow = (await admin.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{rejected.Id}"))!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/crm/quotes/{rejected.Id}/record-rejected", new QuoteTransitionRequest(rejectNow.Version, "Bütçe yok"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{rejected.Id}", new CreateOrderFromQuoteRequest())).StatusCode);

        // İptal edilmiş teklif.
        var cancelled = await PresentedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var cancelNow = (await admin.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{cancelled.Id}"))!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/crm/quotes/{cancelled.Id}/cancel", new QuoteTransitionRequest(cancelNow.Version, "Vazgeçildi"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{cancelled.Id}", new CreateOrderFromQuoteRequest())).StatusCode);

        // Hiçbiri sipariş bırakmaz.
        var all = (await admin.GetFromJsonAsync<PagedResult<OrderDto>>("/api/orders/orders"))!;
        Assert.Empty(all.Items);
    }

    [Fact]
    public async Task Order_can_be_created_after_the_quote_validity_because_acceptance_was_already_checked()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));

        // Geçerlilik kabul anında denetlenir; kabulden sonra geçerlilik tarihi geçse bile dönüşüm engellenmez.
        var preview = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.NotNull(preview.ValidUntil);
        var order = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest());
        Assert.Equal(preview.ValidUntil, order.QuoteValidUntil);
        Assert.True(order.AcceptedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Repeated_and_concurrent_requests_produce_a_single_order()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 2m, 1000m, 20m, null));

        // Çift tıklama: ikinci istek 409 alır, yanıt mevcut siparişi söyler.
        var first = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest());
        var second = await admin.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(first.Number, await second.Content.ReadAsStringAsync());

        // Eşzamanlı istek: yeni bir teklifte iki istemci aynı anda gönderir; yalnızca biri kazanır.
        var other = await AcceptedQuoteAsync(admin, s.CompanyId, ("Eğitim", 1m, 300m, 0m, null));
        using var left = await LoginAsync(s.Admin);
        using var right = await LoginAsync(s.Admin);
        var results = await Task.WhenAll(
            left.PostAsJsonAsync($"/api/orders/from-quote/{other.Id}", new CreateOrderFromQuoteRequest()),
            right.PostAsJsonAsync($"/api/orders/from-quote/{other.Id}", new CreateOrderFromQuoteRequest()));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);

        var orders = (await admin.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?sourceQuoteId={other.Id}"))!;
        Assert.Single(orders.Items);
        Assert.Equal(300m, orders.Items[0].GrossTotal);
        var onlyFirst = (await admin.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?sourceQuoteId={accepted.Id}"))!;
        Assert.Single(onlyFirst.Items);
    }

    [Fact]
    public async Task Partial_conversion_tracks_remaining_quantity_and_never_exceeds_the_accepted_quantity()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Cihaz", 4m, 1000m, 20m, null), ("Montaj", 1m, 300m, 0m, null));
        var preview = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        var device = preview.Lines.Single(l => l.Name == "Cihaz").StableKey;
        var assembly = preview.Lines.Single(l => l.Name == "Montaj").StableKey;

        var first = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest("Partial", [new(device, 1m)]));
        Assert.Equal(("Partial", 1200m), (first.ConversionMode, first.GrossTotal));
        var firstLine = (await admin.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={first.Id}"))!.Items.Single();
        Assert.Equal((1m, 4m, 1000m), (firstLine.Quantity, firstLine.AcceptedQuantity, firstLine.Net));

        // Kalan miktar önizlemede görünür; tam dönüşüm artık çakışır, aşan kısmi talep de.
        var mid = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        var deviceLine = mid.Lines.Single(l => l.StableKey == device);
        Assert.Equal((4m, 1m, 3m), (deviceLine.AcceptedQuantity, deviceLine.ConvertedQuantity, deviceLine.RemainingQuantity));
        Assert.False(mid.FullyConverted);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}",
            new CreateOrderFromQuoteRequest("Partial", [new(device, 4m)]))).StatusCode);

        // Kalan 3 adet ve montaj ikinci siparişe girer; sonra hiçbir şey kalmaz.
        var second = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest("Partial", [new(device, 3m), new(assembly, 1m)]));
        Assert.Equal(3m * 1000m * 1.2m + 300m, second.GrossTotal);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}",
            new CreateOrderFromQuoteRequest("Partial", [new(device, 1m)]))).StatusCode);
        var done = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{accepted.Id}"))!;
        Assert.True(done.FullyConverted);
        Assert.Equal(2, done.ExistingOrders.Count);

        // Toplamlar kabul edilen teklifin toplamına eşittir.
        Assert.Equal(accepted.GrossTotal, first.GrossTotal + second.GrossTotal);

        // Hatalı talepler: boş, sıfır, bilinmeyen kalem ve tam kipte miktar.
        var fresh = await AcceptedQuoteAsync(admin, s.CompanyId, ("Cihaz", 4m, 1000m, 20m, null));
        var freshKey = (await admin.GetFromJsonAsync<OrderSourceDto>($"/api/orders/from-quote/{fresh.Id}"))!.Lines.Single().StableKey;
        foreach (var bad in new[]
                 {
                     new CreateOrderFromQuoteRequest("Partial"),
                     new CreateOrderFromQuoteRequest("Partial", [new(freshKey, 0m)]),
                     new CreateOrderFromQuoteRequest("Partial", [new(Guid.NewGuid(), 1m)]),
                     new CreateOrderFromQuoteRequest("Full", [new(freshKey, 1m)]),
                     new CreateOrderFromQuoteRequest("Weird"),
                 })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/orders/from-quote/{fresh.Id}", bad)).StatusCode);
        }

        Assert.Empty((await admin.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?sourceQuoteId={fresh.Id}"))!.Items);
    }

    [Fact]
    public async Task Later_customer_changes_do_not_change_the_order_snapshot_and_commercial_fields_stay_editable_only_in_draft()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var order = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest());
        Assert.Equal("Anka Ltd.", order.CustomerName);

        // Müşteri kartı sonradan değişir: sipariş aynı kalır.
        var company = (await admin.GetFromJsonAsync<CompanyDto>($"/api/crm/companies/{s.CompanyId}"))!;
        var rename = await admin.PutAsJsonAsync($"/api/crm/companies/{s.CompanyId}", new SaveCompanyRequest("Yeni Ünvan A.Ş.", Version: company.Version));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var again = (await admin.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        Assert.Equal(("Anka Ltd.", order.GrossTotal), (again.CustomerName, again.GrossTotal));

        // Taslakta planlama alanları düzenlenir; sunucu alanları (durum, toplam, kaynak) genel uçtan yazılamaz.
        // PUT tam durumu gönderir (platform kuralı); gönderilmeyen alan temizlenir. Sistem alanları ise her durumda korunur.
        var edit = await admin.PutAsJsonAsync($"/api/orders/orders/{order.Id}",
            new SaveOrderRequest(order.Version, Title: "Güncel başlık", OwnerId: order.OwnerId, Location: "Kocaeli", DeliveryAddress: "Yeni depo", PaymentTerms: order.PaymentTerms));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(("Güncel başlık", "Kocaeli", "Draft", 100m), (edited.Title, edited.Location, edited.Status, edited.GrossTotal));
        Assert.Equal((order.OwnerId, order.PartyId, order.SourceQuoteId, "Anka Ltd."), (edited.OwnerId, edited.PartyId, edited.SourceQuoteId, edited.CustomerName));
        var forged = await admin.PutAsJsonAsync($"/api/records/orders.order/{order.Id}",
            new { version = edited.Version, values = new Dictionary<string, object> { ["status"] = "Approved", ["grossTotal"] = 1m, ["handoverStatus"] = "Accepted" } });
        var afterForge = (await admin.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        Assert.Equal(("Draft", "NotStarted", 100m), (afterForge.Status, afterForge.HandoverStatus, afterForge.GrossTotal));
        _ = forged;

        // Sürüm uyuşmazlığı 409.
        var stale = await admin.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(order.Version, Title: "Eski sürüm"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Orders_cannot_be_created_or_deleted_through_the_generic_endpoints_and_lines_are_server_written()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var order = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest());

        var blank = await admin.PostAsJsonAsync("/api/orders/orders", new SaveOrderRequest(0, Title: "Boş sipariş"));
        Assert.NotEqual(HttpStatusCode.Created, blank.StatusCode);
        var delete = await admin.DeleteAsync($"/api/orders/orders/{order.Id}?version={order.Version}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        var line = (await admin.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single();
        var extra = await admin.PostAsJsonAsync("/api/orders/order-lines", new SaveOrderLineRequest(0, "Ek kalem"));
        Assert.NotEqual(HttpStatusCode.Created, extra.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/orders/order-lines/{line.Id}?version={line.Version}")).StatusCode);
        var planned = await admin.PutAsJsonAsync($"/api/orders/order-lines/{line.Id}", new SaveOrderLineRequest(line.Version, PlannedDate: new DateOnly(2026, 12, 20)));
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        Assert.Equal(new DateOnly(2026, 12, 20), (await planned.Content.ReadFromJsonAsync<OrderLineDto>())!.PlannedDate);
    }

    [Fact]
    public async Task Other_tenants_and_unauthorized_users_cannot_create_read_or_see_amounts()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var noFinancials = await LoginAsync(a.NoFinancials);
        using var reader = await LoginAsync(a.Reader);
        using var own = await LoginAsync(a.OwnOnly);
        var accepted = await AcceptedQuoteAsync(adminA, a.CompanyId, ("Kurulum", 1m, 100m, 20m, null));

        // Başka tenant teklifi göremez, siparişi de (404).
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/from-quote/{accepted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest())).StatusCode);

        // Finansal izni olmayan oluşturucu ve yalnızca okuyucu oluşturamaz (403).
        Assert.Equal(HttpStatusCode.Forbidden, (await noFinancials.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest())).StatusCode);
        Assert.Empty((await adminA.GetFromJsonAsync<PagedResult<OrderDto>>("/api/orders/orders"))!.Items);

        var order = await CreateAsync(adminA, accepted.Id, new CreateOrderFromQuoteRequest());

        // Başka tenant siparişi okuyamaz, düzenleyemez; kendi listesinde yoktur.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/orders/{order.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(order.Version, Title: "Ele geçirme"))).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<OrderDto>>("/api/orders/orders"))!.Items);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items);

        // Yalnızca okuma izni olan siparişi görür, tutarları ve kalem fiyatlarını görmez, yazamaz.
        var seen = (await reader.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        Assert.Equal(order.Number, seen.Number);
        Assert.Null(seen.NetTotal);
        Assert.Null(seen.GrossTotal);
        var seenLine = (await reader.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items.Single();
        Assert.Null(seenLine.UnitPrice);
        Assert.Null(seenLine.Net);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/orders/orders/{order.Id}", new SaveOrderRequest(seen.Version, Title: "Yetkisiz"))).StatusCode);

        // Veri kapsamı "kendi": başkasının siparişini göremez.
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/orders/orders/{order.Id}")).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<OrderDto>>("/api/orders/orders"))!.Items);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<OrderLineDto>>($"/api/orders/order-lines?orderId={order.Id}"))!.Items);

        // Sipariş modülü kapalı tenant'ta uç noktalar kapalıdır.
        var noModule = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(noModule.Id, "crm", enabled: true);
        var user = await factory.CreateUserAsync(noModule);
        await factory.CreateRoleAsync(noModule.Id, [OrdersRead, OrdersCreate, FinancialsRead], Scope.All, user);
        using var client = await LoginAsync(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/orders/orders")).StatusCode);
    }

    [Fact]
    public async Task Order_can_be_linked_to_an_activity_but_stays_invisible_to_users_without_access()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var order = await CreateAsync(admin, accepted.Id, new CreateOrderFromQuoteRequest());

        var activity = await admin.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Devir toplantısı", "Task", DateTimeOffset.UtcNow.AddDays(3), CompanyId: s.CompanyId));
        Assert.Equal(HttpStatusCode.Created, activity.StatusCode);
        var activityDto = (await activity.Content.ReadFromJsonAsync<ActivityDto>())!;
        var link = await admin.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(activityDto.Id, "orders.order", order.Id));
        Assert.True(link.StatusCode == HttpStatusCode.Created, await link.Content.ReadAsStringAsync());
        var related = (await admin.GetFromJsonAsync<ActivityRelatedDto>($"/api/crm/activities/{activityDto.Id}/related"))!;
        var linked = related.Links.Single(l => l.EntityKey == "orders.order");
        Assert.Contains(order.Number, linked.Title);
        Assert.DoesNotContain("100", linked.Title); // tutar aktivite üzerinden taşınmaz

        using var outsider = await LoginAsync(s.OwnOnly);
        var other = await outsider.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(activityDto.Id, "orders.order", order.Id));
        Assert.NotEqual(HttpStatusCode.Created, other.StatusCode);
    }

    private async Task<OrderDto> CreateAsync(HttpClient client, Guid quoteId, CreateOrderFromQuoteRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/orders/from-quote/{quoteId}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    /// <summary>Teklif açar, kalemleri ekler, sunar.</summary>
    private static async Task<QuoteDto> PresentedQuoteAsync(
        HttpClient client, Guid companyId, params (string Name, decimal Quantity, decimal Price, decimal Tax, string? Period)[] lines)
    {
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Yıllık bakım teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price, tax, period) in lines)
        {
            var added = await client.PostAsJsonAsync("/api/crm/quote-lines",
                new SaveQuoteLineRequest(quote.Id, name, "Free", null, quantity, price, null, "None", 0m, tax, null, "Standard", RecurringPeriod: period));
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        return await TransitionAsync(client, quote.Id, "present");
    }

    private static async Task<QuoteDto> AcceptedQuoteAsync(
        HttpClient client, Guid companyId, params (string Name, decimal Quantity, decimal Price, decimal Tax, string? Period)[] lines)
    {
        var presented = await PresentedQuoteAsync(client, companyId, lines);
        return await TransitionAsync(client, presented.Id, "record-accepted");
    }

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private sealed record Setup(ModulixUser Admin, Guid AdminId, ModulixUser NoFinancials, ModulixUser Reader, ModulixUser OwnOnly, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "orders", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var noFinancials = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var ownOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersCreate, OrdersWrite, Modulix.Modules.Crm.CrmModule.Permissions.QuotesRead], Scope.All, noFinancials);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, Modulix.Modules.Crm.CrmModule.Permissions.ActivitiesRead], Scope.Own, ownOnly);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        return new Setup(admin, admin.Id, noFinancials, reader, ownOnly, (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
