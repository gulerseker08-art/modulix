using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>
/// Sipariş ve operasyon özet göstergeleri. Hepsi <b>sayıdır</b>: tutar toplanmaz (farklı para birimleri karıştırılmaz; sipariş tutarı gelir ya da tahsilat değildir).
/// Her sayı kayıt boru hattının listesinden gelir; kullanıcının izni ve veri kapsamı dışındaki kayıtlar sayıya girmez.
/// </summary>
/// <param name="HandoverPending">Onaylı ama satış devri operasyonca kabul edilmemiş sipariş sayısı.</param>
/// <param name="PendingApproval">Onay bekleyen sipariş sayısı.</param>
/// <param name="PartiallyFulfilled">Onaylı ve gerçekleşmesi kısmen olan sipariş sayısı.</param>
/// <param name="OverdueOrders">Planlanan teslim tarihi geçmiş, tamamlanmamış onaylı sipariş sayısı.</param>
/// <param name="UnassignedJobs">Atama bekleyen operasyon işi sayısı.</param>
/// <param name="OverdueJobs">Planlanan bitişi geçmiş açık operasyon işi sayısı.</param>
/// <param name="OverdueDeliveries">Planlanan tarihi geçmiş planlı teslimat sayısı.</param>
/// <param name="OverdueServiceStarts">Planlanan başlangıcı geçmiş, başlamamış hizmet sayısı.</param>
/// <param name="OpenDeliveries">Açık (planlı) teslimat sayısı.</param>
/// <param name="AverageHandoverAcceptanceHours">Kabul edilen devirlerin ilk gönderimden kabule ortalama süresi (saat); örnek yoksa null.</param>
/// <param name="HandoverAcceptanceSample">Ortalamaya giren devir sayısı (en son kabul edilen en çok 200).</param>
public sealed record OrderSummaryView(
    int HandoverPending, int PendingApproval, int PartiallyFulfilled, int OverdueOrders, int UnassignedJobs, int OverdueJobs, int OverdueDeliveries,
    int OverdueServiceStarts, int OpenDeliveries, double? AverageHandoverAcceptanceHours, int HandoverAcceptanceSample);

/// <summary>Devir kabul süresi hesabı (saf).</summary>
public static class HandoverAcceptanceStats
{
    /// <summary>İlk gönderimden kabule geçen sürelerin ortalaması (saat); negatif süreler (saat kayması) yok sayılır. Örnek yoksa null.</summary>
    public static double? AverageHours(IEnumerable<(DateTimeOffset FirstSent, DateTimeOffset Accepted)> samples)
    {
        var hours = samples.Select(s => (s.Accepted - s.FirstSent).TotalHours).Where(h => h >= 0d).ToList();
        return hours.Count == 0 ? null : Math.Round(hours.Average(), 1);
    }
}

public interface IOrderSummary
{
    Task<RecordResult<OrderSummaryView>> GetAsync(CancellationToken cancellationToken);
}

internal sealed class OrderSummary(IRecordPipeline pipeline, OrderReader reader) : IOrderSummary
{
    private const int SampleSize = 200;

    public async Task<RecordResult<OrderSummaryView>> GetAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (key, entity, filters) in Queries)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(entity, 1, 1, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<OrderSummaryView>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }

            counts[key] = result.Value!.TotalCount;
        }

        var accepted = await pipeline.QueryAsync(
            new RecordQuery(OrdersModule.Entities.Handover, 1, SampleSize, null, new Dictionary<string, string> { ["status"] = HandoverStatus.Accepted }), cancellationToken);
        if (!accepted.IsSuccess)
        {
            return RecordResult<OrderSummaryView>.Fail(accepted.Error!.Code, accepted.Error.Message, accepted.Error.FieldErrors);
        }

        var firstSent = await reader.FirstSentAtAsync([.. accepted.Value!.Items.Select(i => i.Id)], cancellationToken);
        var samples = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (var item in accepted.Value.Items)
        {
            if (firstSent.TryGetValue(item.Id, out var sent) && Time(item, "acceptedAt") is { } at)
            {
                samples.Add((sent, at));
            }
        }

        return RecordResult<OrderSummaryView>.Ok(new OrderSummaryView(
            counts["handoverPending"], counts["pendingApproval"], counts["partial"], counts["overdueOrders"], counts["unassigned"], counts["overdueJobs"], counts["overdueDeliveries"],
            counts["overdueServices"], counts["openDeliveries"], HandoverAcceptanceStats.AverageHours(samples), samples.Count));
    }

    private static DateTimeOffset? Time(RecordData item, string field) =>
        item.Values.TryGetPropertyValue(field, out var node) && node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyDictionary<string, string> F(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static readonly (string Key, string Entity, IReadOnlyDictionary<string, string> Filters)[] Queries =
    [
        ("handoverPending", OrdersModule.Entities.Order, F(("handoverPending", "true"))),
        ("pendingApproval", OrdersModule.Entities.Order, F(("status", OrderStatus.PendingApproval))),
        ("partial", OrdersModule.Entities.Order, F(("status", OrderStatus.Approved), ("fulfillmentStatus", FulfillmentStatus.Partial))),
        ("overdueOrders", OrdersModule.Entities.Order, F(("overdue", "true"))),
        ("unassigned", OrdersModule.Entities.Job, F(("unassigned", "true"))),
        ("overdueJobs", OrdersModule.Entities.Job, F(("overdue", "true"))),
        ("overdueDeliveries", OrdersModule.Entities.Delivery, F(("overdue", "true"))),
        ("overdueServices", OrdersModule.Entities.ServiceStart, F(("overdue", "true"))),
        ("openDeliveries", OrdersModule.Entities.Delivery, F(("status", DeliveryStatus.Planned))),
    ];
}
