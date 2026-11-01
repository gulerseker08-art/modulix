using System.Net;
using System.Net.Http.Json;
using Modulix.Shared.Orders;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0023 dilim 6: özet göstergeler. Sayılar doğru kaynaktan ve süzgeçle gelir, tutar toplanmaz, kullanıcının veri kapsamı ve izniyle sınırlıdır,
/// başka tenant'ın verisi sayıya girmez, ortalama devir kabul süresi ilk gönderimden kabule hesaplanır.
/// </summary>
public partial class OrderFulfillmentTests
{
    [Fact]
    public async Task Summary_counts_pending_partial_unassigned_and_overdue_work_and_the_average_handover_acceptance_time()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var approver = await LoginAsync(s.Approver);

        Assert.Equal(new OrderSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0), await SummaryAsync(ops));

        // Onaylı ama devri olmayan sipariş: devir bekliyor. Onaya gönderilmiş sipariş: onay bekliyor.
        var noHandover = await ApprovedOrderAsync(s, sales);
        var accepted = await AcceptedQuoteAsync(sales, s.CompanyId);
        var created = await sales.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest());
        var pending = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        await TransitionAsync(sales, pending.Id, "submit-for-approval");
        var summary = await SummaryAsync(ops);
        Assert.Equal((1, 1, 0), (summary.HandoverPending, summary.PendingApproval, summary.PartiallyFulfilled));
        Assert.Equal((0, null), (summary.HandoverAcceptanceSample, summary.AverageHandoverAcceptanceHours));
        Assert.NotNull(noHandover);

        // Devri kabul edilmiş sipariş devir bekleyen sayılmaz (yalnızca devri olmayan sipariş kalır); ortalama devir kabul süresi oluşur.
        var order = await AcceptedOrderAsync(s, sales, ops);
        summary = await SummaryAsync(ops);
        Assert.Equal(1, summary.HandoverPending);
        Assert.Equal(1, summary.HandoverAcceptanceSample);
        Assert.True(summary.AverageHandoverAcceptanceHours is >= 0d and < 1d, $"ortalama: {summary.AverageHandoverAcceptanceHours}");

        // Teslimat: biri planlı ve gecikmiş, kısmi teslim kısmen gerçekleşen sipariş yapar; hizmet başlangıcı gecikmiş planlanır; iş atanmamış ve gecikmiş olur.
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        var first = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 1m));
        var second = await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 6), (line.LineId, 1m));
        await OkAsync(await DeliverAsync(ops, first, "Ali Veli", (line.LineId, 1m)));
        var service = (await FulfillmentAsync(ops, order.Id)).Services.Single();
        await PlanServiceAsync(ops, order.Id, service.LineId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)));
        await SqlAsync(s.Tenant.Id, "UPDATE orders.Deliveries SET PlannedDate = {0} WHERE Id = {1}", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), second.Id);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OperationJobs SET Status = 'AwaitingAssignment', ResponsibleUserId = NULL WHERE OrderId = {0} AND Kind = 'Service'", order.Id);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OperationJobs SET PlannedStartDate = {0}, PlannedEndDate = {1} WHERE OrderId = {2} AND Kind = 'Delivery'", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), order.Id);

        summary = await SummaryAsync(ops);
        Assert.Equal(1, summary.PartiallyFulfilled);
        Assert.Equal((1, 1, 1, 1), (summary.OpenDeliveries, summary.OverdueDeliveries, summary.OverdueServiceStarts, summary.UnassignedJobs));
        Assert.Equal(1, summary.OverdueJobs);

        // Yalnızca sayı döner: para alanı yoktur.
        var json = await ops.GetStringAsync("/api/orders/summary");
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("total", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currency", json, StringComparison.OrdinalIgnoreCase);

        // Gösterge sayısı listeyle aynı süzgeçten gelir.
        Assert.Equal(summary.OverdueDeliveries, (await ops.GetFromJsonAsync<PagedResult<DeliveryDto>>("/api/orders/deliveries?overdue=true"))!.TotalCount);
        Assert.Equal(summary.OverdueServiceStarts, (await ops.GetFromJsonAsync<PagedResult<ServiceStartDto>>("/api/orders/service-starts?overdue=true"))!.TotalCount);
        Assert.Equal(summary.UnassignedJobs, (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>("/api/orders/operation-jobs?unassigned=true"))!.TotalCount);
        Assert.NotNull(await approver.GetFromJsonAsync<OrderSummaryDto>("/api/orders/summary"));
    }

    [Fact]
    public async Task Summary_is_limited_by_permission_data_scope_and_tenant()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var own = await LoginAsync(s.Own);
        using var noAccess = await LoginAsync(s.NoAccess);
        using var foreign = await LoginAsync(other.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var line = (await FulfillmentAsync(ops, order.Id)).Products.Single();
        await PlanAsync(ops, order.Id, new DateOnly(2026, 12, 5), (line.LineId, 2m));
        await SqlAsync(s.Tenant.Id, "UPDATE orders.Deliveries SET PlannedDate = {0} WHERE OrderId = {1}", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), order.Id);

        // Kapsamı "kendi" olan, siparişin sahibi olmayan kullanıcı hiçbir sayı görmez; tümünü görebilen görür.
        Assert.Equal(1, (await SummaryAsync(ops)).OverdueDeliveries);
        Assert.Equal(new OrderSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0), await SummaryAsync(own));

        // İzni olmayan göstergeye ulaşamaz; başka tenant'ın kullanıcısı bu tenant'ın verisini saymaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.GetAsync("/api/orders/summary")).StatusCode);
        Assert.Equal(new OrderSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0), await SummaryAsync(foreign));
    }

    [Fact]
    public async Task Orders_are_reachable_from_the_customer_and_the_opportunity_and_handover_activities_link_to_the_order()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var foreign = await LoginAsync(other.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);

        var byParty = (await sales.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?partyId={order.PartyId}"))!;
        Assert.Equal(order.Id, Assert.Single(byParty.Items).Id);
        Assert.Empty((await foreign.GetFromJsonAsync<PagedResult<OrderDto>>($"/api/orders/orders?partyId={order.PartyId}"))!.Items);

        var activities = (await ops.GetFromJsonAsync<PagedResult<Modulix.Shared.Crm.ActivityDto>>($"/api/crm/activities?related=orders.order:{order.Id}&pageSize=50"))!;
        Assert.NotEmpty(activities.Items);
        Assert.Empty((await foreign.GetFromJsonAsync<PagedResult<Modulix.Shared.Crm.ActivityDto>>($"/api/crm/activities?related=orders.order:{order.Id}&pageSize=50"))!.Items);
    }

    private static async Task<OrderSummaryDto> SummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/orders/summary");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderSummaryDto>())!;
    }
}
