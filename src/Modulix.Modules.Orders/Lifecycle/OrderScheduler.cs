using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Zamanlanmış işlerin tur özeti.</summary>
public sealed record OrderSchedulerRun(int HandoverEscalations);

/// <summary>
/// Sipariş zamanlanmış işleri (ADR-0023). Host, işi tenant başına bir kez çalıştırır (tenant başına oturum kilidi); aynı turun ikinci kez çalışması da
/// güvenlidir: her takip, kayıt üzerindeki tur işaretiyle yalnızca bir kez üretilir. Dilim 3: kabul bekleme süresini aşan devir için satış sorumlusuna
/// bir kez takip aktivitesi açılır. Aktivite modülü kapalıysa takip atlanır ama tur işaretlenir (her turda yeniden denenip gürültü üretilmez).
/// </summary>
public interface IOrderScheduler
{
    Task<OrderSchedulerRun> RunAsync(CancellationToken cancellationToken);
}

/// <summary>"orders.scheduler" kimliğinin izin çapası (zamanlanmış işin izinlerini tanımlar); olay işlemez.</summary>
internal sealed class OrdersSchedulerAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class OrderScheduler(
    OrderWorkflowGate gate,
    IRecordPipeline pipeline,
    OrderReader reader,
    TimeProvider time,
    IOrderPolicyProvider? policy = null,
    IFollowUpActivities? followUps = null) : IOrderScheduler
{
    private const int BatchSize = 50;

    public async Task<OrderSchedulerRun> RunAsync(CancellationToken cancellationToken)
    {
        var settings = policy is null ? OrderPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var escalations = 0;
        if (settings.HandoverWaitHours > 0)
        {
            var now = time.GetUtcNow();
            foreach (var handover in await reader.HandoversWaitingSinceAsync(now.AddHours(-settings.HandoverWaitHours), BatchSize, cancellationToken))
            {
                if (await EscalateAsync(handover, settings, now, cancellationToken))
                {
                    escalations++;
                }
            }
        }

        return new OrderSchedulerRun(escalations);
    }

    private async Task<bool> EscalateAsync(Domain.OrderHandover handover, OrderPolicySettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await reader.OrderAsync(handover.OrderId, cancellationToken) is not { } order)
        {
            return false;
        }

        var note = "Devir " + settings.HandoverWaitHours + " saati aşkın süredir operasyon kabulü bekliyor.";
        if (followUps is not null)
        {
            var created = await followUps.CreateAsync(new FollowUpRequest(
                $"Devir kabulü gecikti: {order.Number}",
                $"{order.Title} siparişinin satış devri {settings.HandoverWaitHours} saati aşkın süredir operasyon kabulü bekliyor; operasyon sorumlusuyla görüşün.",
                DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), order.OwnerId, OrdersModule.Entities.Order, order.Id), cancellationToken);
            if (!created.IsCreated && !created.Unavailable)
            {
                // Geçici hata: tur işaretlenmez, sonraki turda yeniden denenir.
                return false;
            }

            if (!created.IsCreated)
            {
                note += " (Aktivite modülü kapalı olduğundan takip aktivitesi açılamadı.)";
            }
        }

        // Tur işareti ve geçmiş satırı tek işlemde yazılır; sürüm denetimi eşzamanlı ikinci çalıştırmayı reddeder.
        using var _ = gate.Open([OrdersModule.Permissions.HandoversWrite], HandoverEventKinds.WaitingEscalated, note, handoverEffect: (ctx, h, token) =>
        {
            HandoverSync.AddEvent(ctx, h, HandoverEventKinds.WaitingEscalated, note);
            return Task.CompletedTask;
        });
        var values = new JsonObject { ["escalatedRound"] = handover.Round };
        var updated = await pipeline.UpdateAsync(
            new RecordCommand(OrdersModule.Entities.Handover, handover.Id, values, handover.Version) { Reason = "handover.WaitingEscalated" }, cancellationToken);
        return updated.IsSuccess;
    }
}
