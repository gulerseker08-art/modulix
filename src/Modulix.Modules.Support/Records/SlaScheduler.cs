using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Lifecycle;

/// <summary>Tur özeti: yeni işaretlenen ihlal sayısı ve uygulanan eskalasyon eylemi sayısı.</summary>
public sealed record SlaSchedulerRun(int Breaches, int Escalations);

/// <summary>
/// SLA zamanlanmış işi (ADR-0024 dilim 2). Host, işi tenant başına bir kez çalıştırır (tenant başına oturum kilidi); aynı turun tekrar ya da paralel çalışması da güvenlidir:
/// ihlal gerçek hedef anıyla bir kez işaretlenir (işaretli örnek yeniden işaretlenmez) ve her eskalasyon eylemi (tenant, örnek, kural) benzersiz anahtarıyla bir kez uygulanır.
/// Kesinti sonrası dönüşte kaçırılan ihlal ve eşikler, işin çalıştığı ana değil gerçek hedef/eşik anına göre işlenir. Eskalasyon eylemleri ayrıdır: sahibe bildirim, adlı kullanıcıya
/// (ör. ekip lideri) bildirim, yeniden atama. Bildirim e-postası yalnızca talep numarası ve konusunu içerir (iç not ve açıklama gitmez).
/// </summary>
public interface ISlaScheduler
{
    Task<SlaSchedulerRun> RunAsync(CancellationToken cancellationToken);
}

internal sealed class SlaScheduler(
    IModuleData data,
    IRecordPipeline pipeline,
    TicketWorkflowGate gate,
    TimeProvider time,
    IEmailQueue emailQueue,
    INotificationRecipients? recipients = null) : ISlaScheduler
{
    private const int BatchSize = 200;

    public async Task<SlaSchedulerRun> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var breaches = await MarkBreachesAsync(now, cancellationToken);
        var escalations = await EscalateAsync(now, cancellationToken);
        return new SlaSchedulerRun(breaches, escalations);
    }

    /// <summary>Hedef anı geçmiş çalışan örnekleri gerçek hedef anıyla ihlal olarak işaretler (bir kez).</summary>
    private async Task<int> MarkBreachesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var overdue = await data.Set<SlaInstance>()
            .Where(i => i.Status == SlaStatus.Running && i.DueAt != null && i.DueAt <= now && i.BreachedAt == null)
            .OrderBy(i => i.DueAt).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var instance in overdue)
        {
            instance.BreachedAt = instance.DueAt;
            instance.Touch();
            var ticket = await data.Set<SupportTicket>().AsNoTracking().Where(t => t.Id == instance.TicketId)
                .Select(t => new { t.Status, t.OwnerId, t.QueueId }).FirstAsync(cancellationToken);
            data.Set<TicketEvent>().Add(new TicketEvent
            {
                TicketId = instance.TicketId,
                Kind = TicketEventKinds.SlaBreached,
                ActorId = null,
                FromStatus = ticket.Status,
                ToStatus = ticket.Status,
                FromOwnerId = ticket.OwnerId,
                ToOwnerId = ticket.OwnerId,
                FromQueueId = ticket.QueueId,
                ToQueueId = ticket.QueueId,
                Reason = Truncate($"{MetricLabel(instance.Metric)} hedefi aşıldı ({instance.PolicyName}, hedef anı {instance.DueAt:yyyy-MM-dd HH:mm}Z)."),
            });
        }

        return overdue.Count;
    }

    private async Task<int> EscalateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var running = await data.Set<SlaInstance>().AsNoTracking().Where(i => i.Status == SlaStatus.Running && i.RunningSince != null)
            .OrderBy(i => i.DueAt).Take(BatchSize * 2).ToListAsync(cancellationToken);
        if (running.Count == 0)
        {
            return 0;
        }

        var ids = running.Select(i => i.Id).ToList();
        var done = (await data.Set<SlaEscalation>().AsNoTracking().Where(e => ids.Contains(e.InstanceId)).Select(e => new { e.InstanceId, e.RuleId }).ToListAsync(cancellationToken))
            .Select(e => (e.InstanceId, e.RuleId)).ToHashSet();

        var applied = 0;
        foreach (var instance in running)
        {
            var snapshot = SlaSnapshots.Parse(instance.Snapshot);
            var calendar = SlaSnapshots.CalendarOf(snapshot);
            var target = TimeSpan.FromMinutes(instance.TargetMinutes);
            var worked = TimeSpan.FromSeconds(instance.WorkedSeconds);

            foreach (var rule in snapshot.Rules.Where(r => r.Metric == instance.Metric).OrderBy(r => r.Percent).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                if (done.Contains((instance.Id, rule.Id)))
                {
                    continue;
                }

                // Eşik gerçek hedef/eşik anına göre değerlendirilir; iş geç çalışsa da (kesinti sonrası dönüş) doğru anla işlenir.
                var thresholdAt = SlaClock.ThresholdAt(calendar, instance.RunningSince!.Value, target, worked, rule.Percent);
                if (thresholdAt > now)
                {
                    continue;
                }

                if (await ApplyAsync(instance, rule, thresholdAt, now, cancellationToken))
                {
                    done.Add((instance.Id, rule.Id));
                    applied++;
                }
            }
        }

        return applied;
    }

    /// <summary>Eylemi uygular ve (örnek, kural) satırını yazar. Geçici hata (sürüm çakışması) satırı yazmaz: sonraki turda yeniden denenir.</summary>
    private async Task<bool> ApplyAsync(SlaInstance instance, EscalationRule rule, DateTimeOffset thresholdAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ticket = await data.Set<SupportTicket>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == instance.TicketId, cancellationToken);
        if (ticket is null || TicketStatus.IsFinal(ticket.Status))
        {
            return false;
        }

        string outcome;
        switch (rule.Action)
        {
            case SlaEscalationAction.NotifyOwner:
                outcome = await NotifyAsync(ticket, ticket.OwnerId, instance, rule, thresholdAt, cancellationToken);
                break;

            case SlaEscalationAction.NotifyUser:
                outcome = await NotifyAsync(ticket, rule.UserId, instance, rule, thresholdAt, cancellationToken);
                break;

            default:
            {
                var (applied, text) = await ReassignAsync(ticket, instance, rule, thresholdAt, now, cancellationToken);
                if (applied is null)
                {
                    return false;
                }

                // Başarılıysa satır ticket güncellemesiyle aynı işlemde yazıldı; geçersiz yapılandırma (ör. pasif kullanıcı) satırı ayrıca yazılır ki döngüye girmesin.
                if (applied == false)
                {
                    data.Set<SlaEscalation>().Add(Row(instance, rule, thresholdAt, text));
                }

                return true;
            }
        }

        data.Set<SlaEscalation>().Add(Row(instance, rule, thresholdAt, outcome));
        return true;
    }

    private async Task<string> NotifyAsync(SupportTicket ticket, Guid? userId, SlaInstance instance, EscalationRule rule, DateTimeOffset thresholdAt, CancellationToken cancellationToken)
    {
        if (userId is null || recipients is null || await recipients.FindAsync(userId.Value, cancellationToken) is not { } recipient)
        {
            return "Alıcı yok (bildirim gönderilmedi).";
        }

        var kind = rule.Percent >= 100 ? "hedef aşıldı" : $"hedefin %{rule.Percent}'ine ulaşıldı";
        var link = recipients.Link($"destek/talepler/{ticket.Id}");
        var number = WebUtility.HtmlEncode(ticket.Number);
        var subject = WebUtility.HtmlEncode(ticket.Subject);
        var html = $"<p>{number} numaralı talep için {WebUtility.HtmlEncode(MetricLabel(instance.Metric))} SLA'sında {WebUtility.HtmlEncode(kind)}.</p><p><strong>{subject}</strong></p>"
            + (link is null ? string.Empty : $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Talebi aç</a></p>");
        try
        {
            emailQueue.Enqueue(new EmailMessage(recipient.Email, $"[SLA] {ticket.Number}: {MetricLabel(instance.Metric)} {kind}", html)
            {
                ToName = recipient.DisplayName,
                TextBody = $"{ticket.Number} numaralı talep için {MetricLabel(instance.Metric)} SLA'sında {kind}.\n{ticket.Subject}" + (link is null ? string.Empty : $"\n{link}"),
                Reference = $"support.sla:{instance.Id}:{rule.Id}",
            });
            return "E-posta kuyruğuna alındı.";
        }
        catch (ArgumentException)
        {
            return "Alıcı e-postası geçersiz (bildirim gönderilmedi).";
        }
    }

    /// <summary>Yeniden atama: kural kullanıcı ve/veya kuyruk verir. (true: uygulandı ve satır yazıldı; false: yapılandırma geçersiz, uygulanmadı; null: geçici hata, yeniden denenecek.)</summary>
    private async Task<(bool? Applied, string Outcome)> ReassignAsync(
        SupportTicket ticket, SlaInstance instance, EscalationRule rule, DateTimeOffset thresholdAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var values = new JsonObject { ["lastActivityAt"] = now.ToString("O", CultureInfo.InvariantCulture) };
        if (rule.QueueId is { } queueId)
        {
            values["queueId"] = queueId.ToString();
        }

        if (rule.UserId is { } userId)
        {
            values["ownerId"] = userId.ToString();
            values["status"] = ticket.Status is TicketStatus.New or TicketStatus.Triage or TicketStatus.Reopened ? TicketStatus.Assigned : ticket.Status;
        }

        var reason = Truncate($"SLA eskalasyonu: {MetricLabel(instance.Metric)} %{rule.Percent} eşiği ({instance.PolicyName}).");
        using var _ = gate.Open([SupportModule.Permissions.TicketsAssign], TicketEventKinds.SlaEscalated, reason, (context, row, token) =>
        {
            context.Data.Set<SlaEscalation>().Add(Row(instance, rule, thresholdAt, "Yeniden atandı."));
            return Task.CompletedTask;
        });
        var result = await pipeline.UpdateAsync(new RecordCommand(SupportModule.Entities.Ticket, ticket.Id, values, ticket.Version) { Reason = "support.SlaEscalated" }, cancellationToken);
        if (result.IsSuccess)
        {
            return (true, "Yeniden atandı.");
        }

        return result.Error!.Code == RecordErrorCode.Conflict
            ? (null, string.Empty)
            : (false, Truncate($"Uygulanamadı: {result.Error.Message}"));
    }

    private static SlaEscalation Row(SlaInstance instance, EscalationRule rule, DateTimeOffset thresholdAt, string outcome) => new()
    {
        InstanceId = instance.Id,
        TicketId = instance.TicketId,
        RuleId = rule.Id,
        Metric = rule.Metric,
        Percent = rule.Percent,
        Action = rule.Action,
        ThresholdAt = thresholdAt,
        Outcome = Truncate(outcome, SlaEscalation.OutcomeMaxLength),
    };

    private static string MetricLabel(string metric) => metric == SlaMetric.FirstResponse ? "İlk yanıt" : "Çözüm";

    private static string Truncate(string text, int max = SupportTicket.ReasonMaxLength) => text.Length > max ? text[..max] : text;
}
