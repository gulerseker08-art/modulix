using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Lifecycle;

public sealed record SlaPauseView(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason);

public sealed record SlaEscalationView(string RuleId, int Percent, string Action, DateTimeOffset ThresholdAt, DateTimeOffset ExecutedAt, string? Outcome);

/// <summary>
/// Bir SLA örneğinin backend hesaplı görünümü. <see cref="State"/>: Running (hedefte), Paused (duraklatıldı), Breached (hedef aşıldı, hâlâ açık), Met (hedefte tamamlandı),
/// Missed (hedef aşıldıktan sonra tamamlandı), Cancelled. Arayüz sayacı yalnızca gösterimdir; otorite buradaki hesaptır.
/// </summary>
public sealed record SlaInstanceView(
    Guid Id, int Cycle, string Metric, string Status, string State, string PolicyName, int PolicyRevision, DateTimeOffset StartedAt, int TargetMinutes,
    DateTimeOffset? DueAt, DateTimeOffset? WarningAt, int WorkedMinutes, int RemainingMinutes, DateTimeOffset? CompletedAt, DateTimeOffset? BreachedAt,
    string? CalendarName, bool AlwaysOpen, DateTimeOffset ComputedAt, IReadOnlyList<SlaPauseView> Pauses, IReadOnlyList<SlaEscalationView> Escalations);

public sealed record SlaPreviewInput(string TypeKey, Guid? CategoryId, string Priority, Guid? PartyId, DateTimeOffset? StartAt);

public sealed record SlaPreviewMetric(string Metric, int TargetMinutes, DateTimeOffset DueAt, DateTimeOffset? WarningAt);

/// <summary>Örnek bir talep için "hangi politika seçilir, hedefler ne zaman" önizlemesi. Hiçbir şey yazmaz. Eşleşme yoksa <see cref="Matched"/> false ve talep hedefsizdir.</summary>
public sealed record SlaPreviewResult(
    bool Matched, Guid? PolicyId, string? PolicyName, int PolicyRevision, string? CalendarName, bool AlwaysOpen, bool PauseOnWaitingCustomer, bool PauseOnWaitingInternal,
    IReadOnlyList<SlaPreviewMetric> Metrics, IReadOnlyList<EscalationRule> Rules);

/// <summary>Talep SLA okuma, önizleme ve yetkili yeniden hesaplama. Görünürlük kayıt boru hattında doğrulanır (kapsam dışı talep "bulunamadı").</summary>
public interface ITicketSla
{
    Task<RecordResult<IReadOnlyList<SlaInstanceView>>> GetAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<RecordResult<SlaPreviewResult>> PreviewAsync(SlaPreviewInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Açık SLA örneklerini güncel politika ve takvimle yeniden anlık görüntüler (harcanan iş süresi korunur; ihlal edilmiş örnek değişmez). Ayrı yetki ve gerekçe ister;
    /// önce/sonra özeti tarihçeye ve denetim kaydına yazılır.
    /// </summary>
    Task<RecordResult<RecordData>> RecalculateAsync(Guid ticketId, long version, string? reason, CancellationToken cancellationToken);
}

internal sealed class TicketSla(
    IRecordPipeline pipeline,
    TicketWorkflowGate gate,
    ICurrentAccess access,
    SupportStoreContext storeContext,
    IChoiceLists choiceLists) : ITicketSla
{
    private const string Key = SupportModule.Entities.Ticket;

    public async Task<RecordResult<IReadOnlyList<SlaInstanceView>>> GetAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!ticket.IsSuccess)
        {
            return RecordResult<IReadOnlyList<SlaInstanceView>>.Fail(ticket.Error!.Code, ticket.Error.Message, ticket.Error.FieldErrors);
        }

        var data = storeContext.Data;
        var instances = await data.Set<SlaInstance>().AsNoTracking().Where(i => i.TicketId == ticketId).OrderBy(i => i.Cycle).ThenBy(i => i.Metric).ToListAsync(cancellationToken);
        var ids = instances.Select(i => i.Id).ToList();
        var pauses = await data.Set<SlaPause>().AsNoTracking().Where(p => ids.Contains(p.InstanceId)).OrderBy(p => p.StartedAt).ToListAsync(cancellationToken);
        var escalations = await data.Set<SlaEscalation>().AsNoTracking().Where(e => ids.Contains(e.InstanceId)).OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken);
        var now = storeContext.Time.GetUtcNow();
        return RecordResult<IReadOnlyList<SlaInstanceView>>.Ok([.. instances.Select(i => ToView(i, pauses, escalations, now))]);
    }

    internal static SlaInstanceView ToView(SlaInstance i, IReadOnlyList<SlaPause> pauses, IReadOnlyList<SlaEscalation> escalations, DateTimeOffset now)
    {
        var snapshot = SlaSnapshots.Parse(i.Snapshot);
        var calendar = SlaSnapshots.CalendarOf(snapshot);
        var target = TimeSpan.FromMinutes(i.TargetMinutes);
        var worked = i.Status == SlaStatus.Running
            ? SlaClock.WorkedAt(calendar, TimeSpan.FromSeconds(i.WorkedSeconds), i.RunningSince, now)
            : TimeSpan.FromSeconds(i.WorkedSeconds);
        var remaining = target > worked ? target - worked : TimeSpan.Zero;
        var state = i.Status switch
        {
            SlaStatus.Cancelled => "Cancelled",
            SlaStatus.Completed => i.BreachedAt is null ? "Met" : "Missed",
            SlaStatus.Paused => i.BreachedAt is null ? "Paused" : "Breached",
            _ => i.BreachedAt is not null || (i.DueAt is { } due && due <= now) ? "Breached" : "Running",
        };

        return new SlaInstanceView(
            i.Id, i.Cycle, i.Metric, i.Status, state, i.PolicyName, i.PolicyRevision, i.StartedAt, i.TargetMinutes, i.DueAt, i.WarningAt, (int)Math.Floor(worked.TotalMinutes),
            (int)Math.Ceiling(remaining.TotalMinutes), i.CompletedAt, i.BreachedAt ?? (i.Status == SlaStatus.Running && i.DueAt is { } d && d <= now ? d : null), snapshot.CalendarName,
            snapshot.Calendar.AlwaysOpen, now,
            [.. pauses.Where(p => p.InstanceId == i.Id).Select(p => new SlaPauseView(p.StartedAt, p.EndedAt, p.Reason))],
            [.. escalations.Where(e => e.InstanceId == i.Id).Select(e => new SlaEscalationView(e.RuleId, e.Percent, e.Action, e.ThresholdAt, e.CreatedAt, e.Outcome))]);
    }

    public async Task<RecordResult<SlaPreviewResult>> PreviewAsync(SlaPreviewInput input, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(SupportModule.Permissions.SlaManage, cancellationToken))
        {
            return RecordResult<SlaPreviewResult>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.SlaManage}' izni gerekir.");
        }

        var errors = new List<RecordFieldError>();
        if ((await choiceLists.GetAsync(SupportModule.Lists.TicketType, cancellationToken)).Find(input.TypeKey ?? string.Empty) is null)
        {
            errors.Add(new RecordFieldError("typeKey", "Listede olmayan değer."));
        }

        if (!TicketPriority.All.Contains(input.Priority ?? string.Empty))
        {
            errors.Add(new RecordFieldError("priority", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketPriority.All)}."));
        }

        if (input.CategoryId is { } categoryId && !await storeContext.Data.Set<TicketCategory>().AsNoTracking().AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            errors.Add(new RecordFieldError("categoryId", "Kategori bulunamadı."));
        }

        if (input.PartyId is { } partyId && !await storeContext.Data.Set<Modulix.Core.Contracts.SharedRecords.Party>().AsNoTracking().AnyAsync(p => p.Id == partyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("partyId", "Müşteri bulunamadı."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<SlaPreviewResult>.Fail(RecordErrorCode.ValidationFailed, "Önizleme girdileri geçersiz.", errors);
        }

        var selection = await SlaEngine.SelectAsync(storeContext, input.TypeKey!, input.CategoryId, input.Priority!, input.PartyId, cancellationToken);
        if (selection is null)
        {
            return RecordResult<SlaPreviewResult>.Ok(new SlaPreviewResult(false, null, null, 0, null, false, false, false, [], []));
        }

        var start = input.StartAt ?? storeContext.Time.GetUtcNow();
        var metrics = new List<SlaPreviewMetric>();
        var rules = EscalationRuleParser.Parse(selection.Policy.EscalationRules).Rules;
        foreach (var metric in SlaMetric.All)
        {
            if (SlaEngine.TargetOf(selection.Policy, metric) is not { } minutes)
            {
                continue;
            }

            var calendar = SlaSnapshots.CalendarOf(SlaEngine.Snapshot(selection.Policy, selection.Calendar, metric));
            var target = TimeSpan.FromMinutes(minutes);
            var warning = rules.Where(r => r.Metric == metric && r.Percent < 100).Select(r => (int?)r.Percent).Min();
            metrics.Add(new SlaPreviewMetric(
                metric, minutes, SlaClock.DueAt(calendar, start, target, TimeSpan.Zero),
                warning is { } percent ? SlaClock.ThresholdAt(calendar, start, target, TimeSpan.Zero, percent) : null));
        }

        var snapshot = SlaEngine.Snapshot(selection.Policy, selection.Calendar, SlaMetric.Resolution);
        return RecordResult<SlaPreviewResult>.Ok(new SlaPreviewResult(
            true, selection.Policy.Id, selection.Policy.Name, selection.Policy.Revision, selection.Calendar?.Name, snapshot.Calendar.AlwaysOpen, selection.Policy.PauseOnWaitingCustomer,
            selection.Policy.PauseOnWaitingInternal, metrics, rules));
    }

    public async Task<RecordResult<RecordData>> RecalculateAsync(Guid ticketId, long version, string? reason, CancellationToken cancellationToken)
    {
        // Önce kapsamla okunur (kapsam dışı 404), sonra izin (403), gerekçe (422) ve durum (409) denetlenir.
        var loaded = await pipeline.GetAsync(new RecordRef(Key, ticketId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var permission = SupportModule.Permissions.SlaRecalculate;
        if (!await access.HasPermissionAsync(permission, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        var openCount = await storeContext.Data.Set<SlaInstance>().AsNoTracking()
            .CountAsync(i => i.TicketId == ticketId && (i.Status == SlaStatus.Running || i.Status == SlaStatus.Paused), cancellationToken);
        if (openCount == 0)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Yeniden hesaplanacak açık SLA örneği yok.", [new RecordFieldError("status", "Açık SLA örneği yok.")]);
        }

        var values = new JsonObject { ["lastActivityAt"] = storeContext.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
        using (gate.Open([permission], TicketEventKinds.SlaRecalculationRequested, text, async (context, ticket, ct) =>
        {
            var summary = await SlaEngine.RecalculateAsync(context, ticket, ct);
            TicketHistory.Add(context, ticket, TicketEventKinds.SlaRecalculated, summary, ticket.Status, ticket.OwnerId, ticket.QueueId);
        }))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, ticketId, values, version) { Reason = "support.SlaRecalculated" }, cancellationToken);
        }
    }
}
