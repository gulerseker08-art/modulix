using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders;
using Modulix.Modules.Orders.Lifecycle;
using Modulix.Shared.Orders;

namespace Modulix.Api.Orders;

/// <summary>
/// Sipariş uç noktaları (/api/orders/...; ADR-0023). Liste, detay ve taslak düzenleme kayıt boru hattından geçer; genel uçtan sipariş
/// oluşturulamaz ve silinemez (depo reddeder). Sipariş yalnızca kabul edilmiş tekliften oluşur (<c>POST /api/orders/from-quote/{quoteId}</c>).
/// Tenant her istekte oturumdan çözülür; istemciden gelen kayıt kimliği tek başına yetki kanıtı değildir (kapsam boru hattında uygulanır).
/// </summary>
public static class OrdersEndpoints
{
    private const string Prefix = "/api/orders";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .RequireAuthorization()
            .RequireModule(OrdersModule.ModuleKey)
            .WithTags("Siparişler");

        ResourceEndpoints.MapResource<OrderDto, SaveOrderRequest>(group, Prefix, "orders", OrdersModule.Entities.Order,
            OrdersModule.Permissions.OrdersRead, OrdersModule.Permissions.OrdersWrite, OrdersModule.Permissions.OrdersCreate);
        ResourceEndpoints.MapResource<OrderLineDto, SaveOrderLineRequest>(group, Prefix, "order-lines", OrdersModule.Entities.OrderLine,
            OrdersModule.Permissions.OrdersRead, OrdersModule.Permissions.OrdersWrite, OrdersModule.Permissions.OrdersCreate);

        ResourceEndpoints.MapResource<HandoverDto, SaveHandoverRequest>(group, Prefix, "handovers", OrdersModule.Entities.Handover,
            OrdersModule.Permissions.OrdersRead, OrdersModule.Permissions.HandoversWrite, OrdersModule.Permissions.HandoversWrite);
        ResourceEndpoints.MapResource<HandoverItemDto, SaveHandoverItemRequest>(group, Prefix, "handover-items", OrdersModule.Entities.HandoverItem,
            OrdersModule.Permissions.OrdersRead, OrdersModule.Permissions.HandoversWrite, OrdersModule.Permissions.HandoversWrite, OrdersModule.Permissions.HandoversWrite);

        var orders = group.MapGroup("/orders");
        orders.MapGet("/{id:guid}/history", async (Guid id, IOrderQueries queries, CancellationToken ct) =>
        {
            var result = await queries.HistoryAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(e => new OrderHistoryDto(e.Id, e.Kind, e.At, e.ActorId, e.FromStatus, e.ToStatus, e.Reason)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);

        // Kabul edilmiş tekliften sipariş: önizleme ve oluşturma. İzinler serviste denetlenir (oluşturma + finansal).
        var fromQuote = group.MapGroup("/from-quote");
        fromQuote.MapGet("/{quoteId:guid}", async (Guid quoteId, IOrderFromQuote service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(quoteId, ct);
            return result.IsSuccess
                ? Results.Ok(JsonSerializer.Deserialize<OrderSourceDto>(JsonSerializer.Serialize(result.Value, Json), Json))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        fromQuote.MapPost("/{quoteId:guid}", async (Guid quoteId, CreateOrderFromQuoteRequest r, IOrderFromQuote service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(quoteId, new OrderFromQuoteOptions(
                string.IsNullOrWhiteSpace(r.Mode) ? ConversionMode.Full : r.Mode,
                r.Lines?.Select(l => new ConversionLineRequest(l.LineKey, l.Quantity)).ToList(),
                r.Title, r.OwnerId, r.DeliveryAddress, r.Location, r.PlannedStartDate, r.PlannedDeliveryDate, r.Commitments, r.OperationsNotes), ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/orders/{result.Value!.Id}", ResourceEndpoints.ToDto<OrderDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);

        // Durum geçişleri (dilim 2): eylem izinleri serviste, kayıt bulunduktan SONRA denetlenir (kapsam dışı kayıt 404, izinsiz eylem 403).
        orders.MapGet("/{id:guid}/actions", async (Guid id, IOrderWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new OrderActionDto(a.Action, a.Allowed, a.Code, a.Message, a.RequiresReason)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        foreach (var action in OrderAction.All)
        {
            orders.MapPost($"/{{id:guid}}/{action}", async (Guid id, OrderTransitionRequest r, IOrderWorkflow workflow, CancellationToken ct) =>
            {
                var result = await workflow.RunAsync(id, action, r.Version, r.Reason, ct);
                return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<OrderDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        }

        // Değişiklik talepleri: onaylanmış siparişin ticari alanları yerinde değişmez.
        orders.MapGet("/{id:guid}/change-requests", async (Guid id, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.ListAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<OrderChangeRequestDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        orders.MapPost("/{id:guid}/change-requests/preview", async (Guid id, OrderChangeRequestInputDto r, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.PreviewAsync(id, ToInput(r), ct);
            return result.IsSuccess ? Results.Ok(Map<OrderChangePreviewDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        orders.MapPost("/{id:guid}/change-requests", async (Guid id, OrderChangeRequestInputDto r, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(id, ToInput(r), ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/change-requests/{result.Value!.Id}", Map<OrderChangeRequestDto>(result.Value))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);

        var requests = group.MapGroup("/change-requests");
        requests.MapPost("/{id:guid}/approve", async (Guid id, DecideOrderChangeRequest r, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.DecideAsync(id, true, r.Version, r.Note, ct);
            return result.IsSuccess ? Results.Ok(Map<OrderChangeRequestDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        requests.MapPost("/{id:guid}/reject", async (Guid id, DecideOrderChangeRequest r, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.DecideAsync(id, false, r.Version, r.Note, ct);
            return result.IsSuccess ? Results.Ok(Map<OrderChangeRequestDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        requests.MapPost("/{id:guid}/withdraw", async (Guid id, WithdrawOrderChangeRequest r, IOrderChangeRequests service, CancellationToken ct) =>
        {
            var result = await service.WithdrawAsync(id, r.Version, ct);
            return result.IsSuccess ? Results.Ok(Map<OrderChangeRequestDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);

        // Satış devri (dilim 3): sipariş başına tek yapılandırılmış kayıt; eylemlerin izinleri serviste, kayıt bulunduktan SONRA denetlenir.
        orders.MapGet("/{id:guid}/handover", async (Guid id, IOrderHandovers service, CancellationToken ct) =>
        {
            var result = await service.GetByOrderAsync(id, ct);
            return result.IsSuccess ? Results.Ok(ToDetail(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        orders.MapPost("/{id:guid}/handover", async (Guid id, IOrderHandovers service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(id, ct);
            return result.IsSuccess ? Results.Created($"{Prefix}/orders/{id}/handover", ToDetail(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);

        var handovers = group.MapGroup("/handovers");
        handovers.MapGet("/{id:guid}/detail", async (Guid id, IOrderHandovers service, CancellationToken ct) =>
        {
            var result = await service.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(ToDetail(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        foreach (var action in HandoverAction.All)
        {
            handovers.MapPost($"/{{id:guid}}/{action}", async (Guid id, HandoverActionRequest r, IOrderHandovers service, CancellationToken ct) =>
            {
                var result = await service.RunAsync(id, action, r.Version, r.Note, r.ResponsibleUserId, ct);
                return result.IsSuccess ? Results.Ok(ToDetail(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(OrdersModule.Permissions.OrdersRead);
        }

        return app;
    }

    private static HandoverDetailDto ToDetail(OrderHandoverView view) => new(
        ResourceEndpoints.ToDto<HandoverDto>(view.Handover),
        [.. view.Items.Select(i => new HandoverItemDto(i.Id, i.Version, i.Position, i.Text, i.IsRequired, i.IsDone, i.DoneBy, i.DoneAt, i.Note, i.IsDefault))],
        view.Missing,
        [.. view.Actions.Select(a => new HandoverActionDto(a.Action, a.Allowed, a.Code, a.Message, a.RequiresNote))],
        [.. view.History.Select(h => new HandoverEventDto(h.Id, h.Round, h.Kind, h.At, h.ActorId, h.Note))]);

    private static OrderChangeRequestInput ToInput(OrderChangeRequestInputDto r) =>
        new(r.Reason, r.Header, r.Lines?.Select(l => new OrderLineChange(l.LineId, l.Quantity, l.PlannedDate)).ToList());

    /// <summary>Alan adları aynı olan alan modeli ve DTO arasında JSON üzerinden eşleme.</summary>
    private static T Map<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

/// <summary>Sipariş politikası: tenant ayarlarından okunur (modül Core'un ayar servisini görmez; host bu köprüyü sağlar).</summary>
internal sealed class OrderPolicyProvider(IServiceProvider services) : IOrderPolicyProvider
{
    public async Task<OrderPolicySettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<Modulix.Core.Settings.ITenantSettings>();
        return new OrderPolicySettings(
            string.Equals(await settings.GetValueAsync(OrdersModule.SettingKeys.AllowSelfApproval, cancellationToken), "true", StringComparison.OrdinalIgnoreCase),
            int.TryParse(await settings.GetValueAsync(OrdersModule.SettingKeys.HandoverAcceptDueDays, cancellationToken), out var dueDays) ? Math.Clamp(dueDays, 1, 30) : 2,
            int.TryParse(await settings.GetValueAsync(OrdersModule.SettingKeys.HandoverWaitHours, cancellationToken), out var waitHours) ? Math.Clamp(waitHours, 0, 9999) : 48);
    }
}
