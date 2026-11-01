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

        return app;
    }
}
