using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Sla;

/// <summary>Örneğe yazılan politika + takvim anlık görüntüsü. Sonradan politika ya da takvim değişse de açık örnek bu görüntüyle yaşar.</summary>
public sealed record SlaSnapshot(
    Guid PolicyId, int Revision, string PolicyName, string Metric, int TargetMinutes, bool PauseOnWaitingCustomer, bool PauseOnWaitingInternal,
    Guid? CalendarId, string? CalendarName, CalendarSpec Calendar, IReadOnlyList<EscalationRule> Rules);

public static class SlaSnapshots
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(SlaSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    public static SlaSnapshot Parse(string json) => JsonSerializer.Deserialize<SlaSnapshot>(json, Json)!;

    public static WorkCalendar CalendarOf(SlaSnapshot snapshot) => snapshot.Calendar.AlwaysOpen ? WorkCalendar.AlwaysOpen : new WorkCalendar(snapshot.Calendar);
}

/// <summary>Seçilen politika ve takvimi.</summary>
internal sealed record SlaSelection(SlaPolicy Policy, SlaCalendar? Calendar);

/// <summary>
/// SLA motoru (ADR-0024 dilim 2): talebin yaşam döngüsüne bağlı SLA örneklerini kayıt boru hattıyla aynı işlemde yönetir. Talep açılınca politika seçilir ve örnekler başlar;
/// bekleme durumları politikaya göre sayacı duraklatır/sürdürür; ilk insan yanıtı ve çözüm örneği tamamlar; yeniden açma yeni döngü başlatır; öncelik değişimi kalan
/// iş süresini koruyarak hedefi yeniden hesaplar (ihlal edilmiş örnek hiçbir zaman silinmez ya da geri alınmaz); iptal örnekleri iptal eder. Hesap backend'dedir.
/// </summary>
internal static class SlaEngine
{
    public static async Task OnCreatedAsync(SupportStoreContext context, SupportTicket ticket, CancellationToken cancellationToken)
    {
        var now = context.Time.GetUtcNow();
        var created = new List<SlaInstance>();
        if (await SelectAsync(context, ticket.TypeKey, ticket.CategoryId, ticket.Priority, ticket.PartyId, cancellationToken) is { } selection)
        {
            foreach (var metric in SlaMetric.All)
            {
                if (Start(context, ticket, selection, metric, cycle: 1, now) is { } instance)
                {
                    created.Add(instance);
                }
            }
        }

        Sync(ticket, created);
    }

    /// <summary>Talep güncellendikten (durum, öncelik, ilk yanıt) sonra SLA örneklerini eşitler.</summary>
    public static async Task OnUpdatedAsync(
        SupportStoreContext context, SupportTicket ticket, string? beforeStatus, string? beforePriority, DateTimeOffset? beforeFirstResponse, CancellationToken cancellationToken)
    {
        var now = context.Time.GetUtcNow();
        var open = await OpenAsync(context, ticket.Id, cancellationToken);
        var created = new List<SlaInstance>();
        var pauses = await OpenPausesAsync(context, open, cancellationToken);

        if (ticket.Priority != beforePriority && open.Count > 0)
        {
            await ReselectAsync(context, ticket, open, cancellationToken);
        }

        if (beforeFirstResponse is null && ticket.FirstResponseAt is { } firstResponseAt)
        {
            foreach (var instance in open.Where(i => i.Metric == SlaMetric.FirstResponse && i.Status is SlaStatus.Running or SlaStatus.Paused))
            {
                Complete(instance, firstResponseAt, pauses);
            }
        }

        switch (ticket.Status)
        {
            case TicketStatus.Cancelled:
            case TicketStatus.Merged:
                foreach (var instance in open.Where(IsOpen))
                {
                    Cancel(instance, ticket.Status == TicketStatus.Merged ? "Talep başka bir talebe birleştirildi." : "Talep iptal edildi.", now, pauses);
                }

                break;

            case TicketStatus.Resolved:
                // Çözümle ilk yanıt hedefi de kapanır (hedef, çözüm anında geçmişse ihlal kaydı korunur).
                foreach (var instance in open.Where(IsOpen))
                {
                    Complete(instance, now, pauses);
                }

                break;

            case TicketStatus.Reopened when beforeStatus != TicketStatus.Reopened:
                // Yeniden açma yeni bir çözüm döngüsü başlatır; önceki döngü örnekleri olduğu gibi saklanır. İlk yanıt yeniden ölçülmez.
                if (await SelectAsync(context, ticket.TypeKey, ticket.CategoryId, ticket.Priority, ticket.PartyId, cancellationToken, await ContractIdsAsync(context, ticket.Id, null, null, cancellationToken)) is { } selection
                    && Start(context, ticket, selection, SlaMetric.Resolution, ticket.ReopenCount + 1, now) is { } cycle)
                {
                    created.Add(cycle);
                }

                break;

            default:
                foreach (var instance in open.Where(IsOpen))
                {
                    Reconcile(context, instance, ticket.Status, now, pauses);
                }

                break;
        }

        Sync(ticket, open.Where(IsOpen).Concat(created));
    }

    /// <summary>Yetkili yeniden hesaplama: açık örnekleri güncel politika ve takvimle yeniden anlık görüntüler (harcanan iş süresi korunur). Önce/sonra özeti denetim için döner.</summary>
    public static async Task<string> RecalculateAsync(SupportStoreContext context, SupportTicket ticket, CancellationToken cancellationToken)
    {
        var open = (await OpenAsync(context, ticket.Id, cancellationToken)).Where(IsOpen).ToList();
        var before = string.Join("; ", open.Select(Describe));
        await ReselectAsync(context, ticket, open, cancellationToken, force: true);
        Sync(ticket, open);
        return $"Önce: {(before.Length == 0 ? "açık SLA yok" : before)} — Sonra: {(open.Count == 0 ? "açık SLA yok" : string.Join("; ", open.Select(Describe)))}";
    }

    /// <summary>
    /// Talebe sözleşme bağlandı ya da bağlantısı kaldırıldı: açık örneklerin politikası güncel bağlı sözleşmelerle yeniden seçilir (değişen politika ya da hedef varsa yeniden anlık görüntülenir;
    /// ihlal edilmiş örnek değişmez). Yeni/kaldırılan bağ henüz kaydedilmediğinden kimlik ayrıca verilir.
    /// </summary>
    public static async Task OnLinksChangedAsync(SupportStoreContext context, SupportTicket ticket, Guid? added, Guid? removed, CancellationToken cancellationToken)
    {
        var open = (await OpenAsync(context, ticket.Id, cancellationToken)).Where(IsOpen).ToList();
        if (open.Count == 0)
        {
            return;
        }

        await ReselectAsync(context, ticket, open, cancellationToken, contractIds: await ContractIdsAsync(context, ticket.Id, added, removed, cancellationToken));
        Sync(ticket, open);
    }

    /// <summary>Talebe bağlı sözleşme kimlikleri; henüz kaydedilmemiş ekleme/çıkarma ayrıca verilir.</summary>
    internal static async Task<IReadOnlyList<Guid>> ContractIdsAsync(SupportStoreContext context, Guid ticketId, Guid? added, Guid? removed, CancellationToken cancellationToken)
    {
        var ids = await context.Data.Set<TicketLink>().AsNoTracking().Where(l => l.TicketId == ticketId && l.EntityKey == TicketLinkKinds.Contract).Select(l => l.RecordId).ToListAsync(cancellationToken);
        if (added is { } add && !ids.Contains(add))
        {
            ids.Add(add);
        }

        if (removed is { } remove)
        {
            ids.Remove(remove);
        }

        return ids;
    }

    // ---- Politika seçimi

    public static async Task<SlaSelection?> SelectAsync(
        SupportStoreContext context, string typeKey, Guid? categoryId, string priority, Guid? partyId, CancellationToken cancellationToken, IReadOnlyList<Guid>? contractIds = null)
    {
        var policies = await context.Data.Set<SlaPolicy>().AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);
        if (policies.Count == 0)
        {
            return null;
        }

        var chain = new List<Guid>();
        if (categoryId is { } id)
        {
            chain.Add(id);
            if (await context.Data.Set<TicketCategory>().AsNoTracking().Where(c => c.Id == id).Select(c => c.ParentId).FirstOrDefaultAsync(cancellationToken) is { } parent)
            {
                chain.Add(parent);
            }
        }

        var chosen = SlaPolicySelector.Select(
            policies.Select(p => new SlaPolicyCandidate(p.Id, p.Name, p.Precedence, p.IsDefault, p.IsActive, p.TypeKey, p.CategoryId, p.Priority, p.PartyId, p.ContractId)),
            new SlaTicketFacts(typeKey, chain, priority, partyId, contractIds));
        if (chosen is null)
        {
            return null;
        }

        var policy = policies.First(p => p.Id == chosen.Id);
        var calendar = policy.CalendarId is { } calendarId
            ? await context.Data.Set<SlaCalendar>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, cancellationToken)
            : null;
        return new SlaSelection(policy, calendar);
    }

    public static int? TargetOf(SlaPolicy policy, string metric) => metric == SlaMetric.FirstResponse ? policy.FirstResponseMinutes : policy.ResolutionMinutes;

    public static SlaSnapshot Snapshot(SlaPolicy policy, SlaCalendar? calendar, string metric)
    {
        var spec = CalendarSpec.TwentyFourSeven;
        if (calendar is not null)
        {
            var (parsed, _) = CalendarSpecParser.Parse(calendar.TimeZoneId, calendar.IsAlwaysOpen, calendar.WeeklyHours, calendar.Holidays);
            spec = parsed ?? spec;
        }

        return new SlaSnapshot(
            policy.Id, policy.Revision, policy.Name, metric, TargetOf(policy, metric) ?? 0, policy.PauseOnWaitingCustomer, policy.PauseOnWaitingInternal,
            calendar?.Id, calendar?.Name, spec, EscalationRuleParser.Parse(policy.EscalationRules).Rules);
    }

    // ---- Örnek yaşam döngüsü

    private static SlaInstance? Start(SupportStoreContext context, SupportTicket ticket, SlaSelection selection, string metric, int cycle, DateTimeOffset now)
    {
        if (TargetOf(selection.Policy, metric) is not { } minutes)
        {
            return null;
        }

        var snapshot = Snapshot(selection.Policy, selection.Calendar, metric);
        var instance = new SlaInstance
        {
            TicketId = ticket.Id,
            Cycle = cycle,
            Metric = metric,
            Status = SlaStatus.Running,
            PolicyId = selection.Policy.Id,
            PolicyRevision = selection.Policy.Revision,
            PolicyName = selection.Policy.Name,
            Snapshot = SlaSnapshots.Serialize(snapshot),
            TargetMinutes = minutes,
            StartedAt = now,
            RunningSince = now,
        };
        Recompute(instance, snapshot);
        context.Data.Set<SlaInstance>().Add(instance);

        // Bekleme durumunda açılan talepte sayaç hemen duraklatılır (ör. yeniden açılan talep zaten müşteri beklemesindeyse).
        if (DesiredPaused(snapshot, ticket.Status))
        {
            Pause(context, instance, snapshot, ticket.Status, now);
        }

        return instance;
    }

    private static void Recompute(SlaInstance instance, SlaSnapshot snapshot)
    {
        if (instance.Status != SlaStatus.Running || instance.RunningSince is not { } since)
        {
            instance.WarningAt = null;
            if (instance.Status == SlaStatus.Paused)
            {
                instance.DueAt = null;
            }

            return;
        }

        var calendar = SlaSnapshots.CalendarOf(snapshot);
        var target = TimeSpan.FromMinutes(instance.TargetMinutes);
        var worked = TimeSpan.FromSeconds(instance.WorkedSeconds);
        instance.DueAt = SlaClock.DueAt(calendar, since, target, worked);
        var warning = snapshot.Rules.Where(r => r.Metric == instance.Metric && r.Percent < 100).Select(r => (int?)r.Percent).Min();
        instance.WarningAt = warning is { } percent ? SlaClock.ThresholdAt(calendar, since, target, worked, percent) : null;
    }

    private static void Pause(SupportStoreContext context, SlaInstance instance, SlaSnapshot snapshot, string reason, DateTimeOffset now)
    {
        if (instance.RunningSince is { } since)
        {
            instance.WorkedSeconds += (long)Math.Floor(SlaSnapshots.CalendarOf(snapshot).WorkingTimeBetween(since, now).TotalSeconds);
        }

        instance.RunningSince = null;
        instance.Status = SlaStatus.Paused;
        instance.DueAt = null;
        instance.WarningAt = null;
        instance.Touch();
        context.Data.Set<SlaPause>().Add(new SlaPause { InstanceId = instance.Id, TicketId = instance.TicketId, StartedAt = now, Reason = reason });
    }

    private static void Resume(SlaInstance instance, SlaSnapshot snapshot, DateTimeOffset now, IReadOnlyList<SlaPause> pauses)
    {
        foreach (var pause in pauses.Where(p => p.InstanceId == instance.Id && p.EndedAt is null))
        {
            pause.EndedAt = now;
            pause.Touch();
        }

        instance.Status = SlaStatus.Running;
        instance.RunningSince = now;
        Recompute(instance, snapshot);
        instance.Touch();
    }

    private static void Reconcile(SupportStoreContext context, SlaInstance instance, string status, DateTimeOffset now, IReadOnlyList<SlaPause> pauses)
    {
        var snapshot = SlaSnapshots.Parse(instance.Snapshot);
        var desired = DesiredPaused(snapshot, status);
        if (desired && instance.Status == SlaStatus.Running)
        {
            Pause(context, instance, snapshot, status, now);
        }
        else if (!desired && instance.Status == SlaStatus.Paused)
        {
            Resume(instance, snapshot, now, pauses);
        }
    }

    /// <summary>Müşteri beklemesi ile iç ekip beklemesi eşit değildir: hangi bekleme sayacı durdurur politikanın anlık görüntüsünde yazar (varsayılan: yalnızca müşteri).</summary>
    private static bool DesiredPaused(SlaSnapshot snapshot, string status) =>
        (status == TicketStatus.WaitingCustomer && snapshot.PauseOnWaitingCustomer) || (status == TicketStatus.WaitingInternal && snapshot.PauseOnWaitingInternal);

    private static void Complete(SlaInstance instance, DateTimeOffset at, IReadOnlyList<SlaPause> pauses)
    {
        var snapshot = SlaSnapshots.Parse(instance.Snapshot);
        if (instance.Status == SlaStatus.Running)
        {
            if (instance.RunningSince is { } since)
            {
                instance.WorkedSeconds += (long)Math.Floor(SlaSnapshots.CalendarOf(snapshot).WorkingTimeBetween(since, at).TotalSeconds);
            }

            // İhlal gerçek hedef anına göre değerlendirilir: iş geç çalışsa da hedef anı geçtiyse ihlal, hedef anıyla kaydedilir.
            if (instance.BreachedAt is null && instance.DueAt is { } due && at > due)
            {
                instance.BreachedAt = due;
            }
        }

        foreach (var pause in pauses.Where(p => p.InstanceId == instance.Id && p.EndedAt is null))
        {
            pause.EndedAt = at;
            pause.Touch();
        }

        instance.Status = SlaStatus.Completed;
        instance.CompletedAt = at;
        instance.RunningSince = null;
        instance.WarningAt = null;
        instance.Touch();
    }

    private static void Cancel(SlaInstance instance, string reason, DateTimeOffset at, IReadOnlyList<SlaPause> pauses)
    {
        foreach (var pause in pauses.Where(p => p.InstanceId == instance.Id && p.EndedAt is null))
        {
            pause.EndedAt = at;
            pause.Touch();
        }

        instance.Status = SlaStatus.Cancelled;
        instance.CancelReason = reason;
        instance.RunningSince = null;
        instance.WarningAt = null;
        instance.Touch();
    }

    /// <summary>
    /// Öncelik değişimi ya da yeniden hesaplama: güncel özelliklerle politika yeniden seçilir. Harcanan iş süresi korunur, hedef anı kalan süreden yeniden hesaplanır
    /// (deterministik). İhlal edilmiş örnek değiştirilmez (öncelik düşürerek ihlal silinemez); eşleşen politika yoksa ya da politikada o ölçüt için hedef yoksa mevcut hedef korunur.
    /// </summary>
    private static async Task ReselectAsync(SupportStoreContext context, SupportTicket ticket, IReadOnlyList<SlaInstance> open, CancellationToken cancellationToken, bool force = false, IReadOnlyList<Guid>? contractIds = null)
    {
        if (await SelectAsync(context, ticket.TypeKey, ticket.CategoryId, ticket.Priority, ticket.PartyId, cancellationToken, contractIds ?? await ContractIdsAsync(context, ticket.Id, null, null, cancellationToken)) is not { } selection)
        {
            return;
        }

        foreach (var instance in open.Where(i => IsOpen(i) && i.BreachedAt is null))
        {
            if (TargetOf(selection.Policy, instance.Metric) is not { } minutes)
            {
                continue;
            }

            if (!force && instance.PolicyId == selection.Policy.Id && instance.PolicyRevision == selection.Policy.Revision && instance.TargetMinutes == minutes)
            {
                continue;
            }

            var oldSnapshot = SlaSnapshots.Parse(instance.Snapshot);
            var snapshot = Snapshot(selection.Policy, selection.Calendar, instance.Metric);
            instance.PolicyId = selection.Policy.Id;
            instance.PolicyRevision = selection.Policy.Revision;
            instance.PolicyName = selection.Policy.Name;
            instance.TargetMinutes = minutes;
            instance.Snapshot = SlaSnapshots.Serialize(snapshot);
            if (instance.Status == SlaStatus.Running && instance.RunningSince is { } since)
            {
                // Bugüne kadar harcanan süre eski takvimle sayılır ve birikir; sayaç şimdiden yeni takvimle sürer.
                var now = context.Time.GetUtcNow();
                instance.WorkedSeconds += (long)Math.Floor(SlaSnapshots.CalendarOf(oldSnapshot).WorkingTimeBetween(since, now).TotalSeconds);
                instance.RunningSince = now;
            }

            Recompute(instance, snapshot);
            instance.Touch();
        }
    }

    // ---- Okuma ve eşitleme

    private static bool IsOpen(SlaInstance instance) => instance.Status is SlaStatus.Running or SlaStatus.Paused;

    private static Task<List<SlaInstance>> OpenAsync(SupportStoreContext context, Guid ticketId, CancellationToken cancellationToken) =>
        context.Data.Set<SlaInstance>().Where(i => i.TicketId == ticketId && (i.Status == SlaStatus.Running || i.Status == SlaStatus.Paused)).ToListAsync(cancellationToken);

    private static async Task<List<SlaPause>> OpenPausesAsync(SupportStoreContext context, IReadOnlyList<SlaInstance> open, CancellationToken cancellationToken)
    {
        if (open.Count == 0)
        {
            return [];
        }

        var ids = open.Select(i => i.Id).ToList();
        return await context.Data.Set<SlaPause>().Where(p => ids.Contains(p.InstanceId) && p.EndedAt == null).ToListAsync(cancellationToken);
    }

    /// <summary>Listeler için talebe çalışan sayaçların hedef anını kopyalar (otorite SLA örneğidir).</summary>
    private static void Sync(SupportTicket ticket, IEnumerable<SlaInstance> open)
    {
        var running = open.Where(i => i.Status == SlaStatus.Running).ToList();
        ticket.FirstResponseDueAt = running.FirstOrDefault(i => i.Metric == SlaMetric.FirstResponse)?.DueAt;
        ticket.ResolutionDueAt = running.FirstOrDefault(i => i.Metric == SlaMetric.Resolution)?.DueAt;
    }

    private static string Describe(SlaInstance i) =>
        $"{i.Metric} d{i.Cycle} {i.PolicyName} r{i.PolicyRevision} hedef {i.TargetMinutes} dk, bitiş {(i.DueAt is { } due ? due.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z" : "duraklatıldı")}";
}
