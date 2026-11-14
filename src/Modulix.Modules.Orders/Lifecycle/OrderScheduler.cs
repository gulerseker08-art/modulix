using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Modules.Crm.Contracts.Activities;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Zamanlanmış işlerin tur özeti.</summary>
public sealed record OrderSchedulerRun(int HandoverEscalations, int UnassignedJobAlerts = 0, int OverdueJobAlerts = 0);

/// <summary>
/// Sipariş zamanlanmış işleri (ADR-0023). Host, işi tenant başına bir kez çalıştırır (tenant başına oturum kilidi); aynı turun ikinci kez çalışması da
/// güvenlidir: her takip, kayıt üzerindeki işaretle yalnızca bir kez üretilir. Kurallar: kabul bekleme süresini aşan devir için satış sorumlusuna (tur başına bir
/// kez); belirlenen süreden uzun atama bekleyen iş için devri kabul eden kullanıcıya (iş başına bir kez); planlanan bitişi geçmiş açık iş için sorumluya (iş başına
/// bir kez) takip aktivitesi açılır. Aktivite modülü kapalıysa takip atlanır ama işaret konur (her turda yeniden denenip gürültü üretilmez).
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
        var now = time.GetUtcNow();
        var escalations = 0;
        var unassigned = 0;
        var overdue = 0;
        if (settings.HandoverWaitHours > 0)
        {
            foreach (var handover in await reader.HandoversWaitingSinceAsync(now.AddHours(-settings.HandoverWaitHours), BatchSize, cancellationToken))
            {
                if (await EscalateHandoverAsync(handover, settings, now, cancellationToken))
                {
                    escalations++;
                }
            }
        }

        if (settings.JobUnassignedHours > 0)
        {
            foreach (var job in await reader.UnassignedJobsSinceAsync(now.AddHours(-settings.JobUnassignedHours), BatchSize, cancellationToken))
            {
                if (await AlertJobAsync(job, unassignedKind: true, settings, now, cancellationToken))
                {
                    unassigned++;
                }
            }
        }

        foreach (var job in await reader.OverdueJobsAsync(DateOnly.FromDateTime(now.UtcDateTime), BatchSize, cancellationToken))
        {
            if (await AlertJobAsync(job, unassignedKind: false, settings, now, cancellationToken))
            {
                overdue++;
            }
        }

        return new OrderSchedulerRun(escalations, unassigned, overdue);
    }

    private async Task<bool> EscalateHandoverAsync(OrderHandover handover, OrderPolicySettings settings, DateTimeOffset now, CancellationToken cancellationToken)
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

    /// <summary>Atama bekleyen ya da geciken iş için takip aktivitesi açar ve işaretini koyar (iş başına bir kez).</summary>
    private async Task<bool> AlertJobAsync(OperationJob job, bool unassignedKind, OrderPolicySettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await reader.OrderAsync(job.OrderId, cancellationToken) is not { } order)
        {
            return false;
        }

        Guid? assignee;
        string subject;
        string description;
        string kind;
        string field;
        if (unassignedKind)
        {
            var accepter = job.HandoverId is { } handoverId ? (await reader.HandoverAsync(handoverId, cancellationToken))?.AcceptedBy : null;
            assignee = accepter ?? order.OwnerId;
            subject = $"Operasyon işi atama bekliyor: {job.Number}";
            description = $"{job.Title} işi {settings.JobUnassignedHours} saati aşkın süredir atama bekliyor; sorumlu kullanıcı ya da ekip atayın.";
            kind = JobEventKinds.UnassignedAlert;
            field = "unassignedAlertAt";
        }
        else
        {
            assignee = job.ResponsibleUserId ?? order.OwnerId;
            subject = $"Operasyon işi gecikti: {job.Number}";
            description = $"{job.Title} işinin planlanan bitişi ({job.PlannedEndDate:dd.MM.yyyy}) geçti ve iş tamamlanmadı.";
            kind = JobEventKinds.OverdueAlert;
            field = "overdueAlertAt";
        }

        var note = description;
        if (followUps is not null)
        {
            var created = await followUps.CreateAsync(new FollowUpRequest(subject, description, DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), assignee, OrdersModule.Entities.Order, order.Id), cancellationToken);
            if (!created.IsCreated && !created.Unavailable)
            {
                return false;
            }

            if (!created.IsCreated)
            {
                note += " (Aktivite modülü kapalı olduğundan takip aktivitesi açılamadı.)";
            }
        }

        using var _ = gate.Open([OrdersModule.Permissions.JobsManage], kind, note, jobEffect: (ctx, j, token) =>
        {
            JobHistory.Add(ctx, j, kind, j.Status, j.Status, note);
            return Task.CompletedTask;
        });
        var values = new JsonObject { [field] = now.ToString("O", CultureInfo.InvariantCulture) };
        var updated = await pipeline.UpdateAsync(new RecordCommand(OrdersModule.Entities.Job, job.Id, values, job.Version) { Reason = $"job.{kind}" }, cancellationToken);
        return updated.IsSuccess;
    }
}
