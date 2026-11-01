using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Tarihçe satırı (API görünümü).</summary>
public sealed record OrderHistoryItem(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>Sipariş okuma sorguları. Görünürlük (izin ve veri kapsamı) kayıt boru hattında doğrulanır; görünmeyen sipariş "bulunamadı" alır.</summary>
public interface IOrderQueries
{
    Task<RecordResult<IReadOnlyList<OrderHistoryItem>>> HistoryAsync(Guid orderId, CancellationToken cancellationToken);
}

internal sealed class OrderQueries(IRecordPipeline pipeline, OrderReader reader) : IOrderQueries
{
    public async Task<RecordResult<IReadOnlyList<OrderHistoryItem>>> HistoryAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!order.IsSuccess)
        {
            return RecordResult<IReadOnlyList<OrderHistoryItem>>.Fail(order.Error!.Code, order.Error.Message, order.Error.FieldErrors);
        }

        var events = await reader.HistoryAsync(orderId, cancellationToken);
        return RecordResult<IReadOnlyList<OrderHistoryItem>>.Ok(
            [.. events.Select(e => new OrderHistoryItem(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.Reason))]);
    }
}
