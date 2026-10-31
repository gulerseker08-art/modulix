using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Zamanlayıcı turunun özeti.</summary>
public sealed record ContractSchedulerRun(
    int Activated,
    int PeriodsExpired,
    int Terminated,
    int AutoRenewed,
    int RemindersCreated,
    int RemindersSkipped,
    int EscalationsCreated,
    int SignaturesExpired,
    int ApprovalReminders,
    int CommitmentsBreached);

/// <summary>
/// Sözleşme zamanlanmış işleri (ADR-0022, dilim 8): otomatik uzatma, planlı fesih, yürürlük geçişleri (başlangıç/bitiş), yenileme ve eskalasyon hatırlatmaları, onay gecikmesi,
/// imza süre dolumu ve taahhüt değerlendirmesi. Bir tenant için "contracts.scheduler" sistem kimliğiyle çalıştırılır (host kilit ve tenant kapsamını sağlar). <b>Tekrar çalıştırmak
/// güvenlidir</b>: dönem kararı, hatırlatma ve eskalasyon (sözleşme, dönem, tür, eşik) benzersiz kayıtlarla bir kez üretilir; kesinti sonrası birikmiş eşiklerden yalnızca en dar olanı için
/// aktivite açılır, geniş olanlar "atlandı" işaretlenir; kaçırılmış dönemler sırayla (en fazla 24) tamamlanır. Bir sözleşmedeki hata diğerlerini durdurmaz. Müşteriye bildirim gitmez.
/// </summary>
public interface IContractScheduler
{
    Task<ContractSchedulerRun> RunAsync(CancellationToken cancellationToken);
}

internal sealed class ContractScheduler(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ContractReader reader,
    ContractApprovalEngine approvals,
    IContractSignatures signatures,
    IContractCommitments commitments,
    TimeProvider time,
    IContractPolicyProvider? policy = null,
    IFollowUpActivities? followUps = null) : IContractScheduler
{
    private const string Key = ContractsModule.Entities.Contract;
    private const int BatchSize = 500;
    private const int MaxCatchUpPeriods = 24;

    private sealed class Counters
    {
        public int Activated, PeriodsExpired, Terminated, AutoRenewed, RemindersCreated, RemindersSkipped, EscalationsCreated, ApprovalReminders;
    }

    public async Task<ContractSchedulerRun> RunAsync(CancellationToken cancellationToken)
    {
        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var today = ContractPeriod.Today(now, TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc);
        var counters = new Counters();

        foreach (var candidate in await reader.SchedulerContractsAsync(BatchSize, cancellationToken))
        {
            try
            {
                await ProcessAsync(candidate.Id, settings, today, now, counters, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Tek sözleşmenin hatası (ör. eşzamanlı değişiklik) turu durdurmaz; bir sonraki tur yeniden dener.
                _ = exception;
            }
        }

        var signaturesExpired = 0;
        foreach (var contractId in await reader.OverdueSignatureContractsAsync(now, 100, cancellationToken))
        {
            if ((await signatures.ExpireAsync(contractId, cancellationToken)).IsSuccess)
            {
                signaturesExpired++;
            }
        }

        foreach (var (contract, version) in await reader.OverdueApprovalsAsync(now, 100, cancellationToken))
        {
            try
            {
                counters.ApprovalReminders += await RemindApprovalAsync(contract, version, today, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _ = exception;
            }
        }

        var breached = (await commitments.EvaluateAsync(null, cancellationToken)) is { IsSuccess: true } evaluation ? evaluation.Value!.Breached : 0;
        return new ContractSchedulerRun(
            counters.Activated, counters.PeriodsExpired, counters.Terminated, counters.AutoRenewed, counters.RemindersCreated, counters.RemindersSkipped, counters.EscalationsCreated,
            signaturesExpired, counters.ApprovalReminders, breached);
    }

    private async Task ProcessAsync(Guid contractId, ContractPolicySettings settings, DateOnly today, DateTimeOffset now, Counters counters, CancellationToken cancellationToken)
    {
        var (record, c) = await LoadAsync(contractId, cancellationToken);
        if (record is null || c is null)
        {
            return;
        }

        // 1) Otomatik uzatma (kaçırılan dönemler sırayla); koşullar saf kuraldadır.
        for (var i = 0; i < MaxCatchUpPeriods; i++)
        {
            var decided = c.EndDate is { } end && (await reader.RenewalsAsync(c.Id, cancellationToken)).Any(r => r.PeriodEnd == end);
            var (eligible, _) = ContractRenewalRules.AutoRenewal(
                c.RenewalMode, c.RenewalPeriodMonths, c.IsIndefinite, c.SignatureStatus, c.EffectiveStatus, settings.AllowAutoRenewal, c.NoticeDeadline, c.EndDate, decided, today);
            if (!eligible)
            {
                break;
            }

            var period = c.EndDate!.Value;
            var (_, newEnd) = ContractRenewalRules.NextTerm(period, c.RenewalPeriodMonths!.Value);
            var extended = await CommitAsync(
                record, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.AutoRenewed, $"{period:dd.MM.yyyy} → {newEnd:dd.MM.yyyy} (otomatik yenileme, {c.RenewalPeriodMonths} ay)",
                new JsonObject { ["endDate"] = newEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                (ctx, contract, _) =>
                {
                    ContractRenewalWriter.AddRenewal(ctx, contract.Id, period, RenewalOutcome.AutoExtended, "Scheduler", newEnd);
                    return Task.CompletedTask;
                },
                cancellationToken);
            if (!extended.IsSuccess)
            {
                return;
            }

            counters.AutoRenewed++;
            (record, c) = await LoadAsync(contractId, cancellationToken);
            if (record is null || c is null)
            {
                return;
            }
        }

        // 2) Planlı fesih.
        if (c.TerminationDate is { } terminationDate && terminationDate <= today && c.EffectiveStatus != EffectiveStatus.Terminated)
        {
            var terminated = await CommitAsync(
                record, [ContractsModule.Permissions.ContractsWrite], ContractEventKinds.Terminated, $"{terminationDate:dd.MM.yyyy}: {c.TerminationReason}",
                new JsonObject { ["effectiveStatus"] = EffectiveStatus.Terminated },
                (ctx, contract, ct) => ContractRenewalWriter.CancelCommitmentsAsync(ctx, contract.Id, "Sözleşme feshedildi.", ct), cancellationToken);
            if (terminated.IsSuccess)
            {
                counters.Terminated++;
            }

            return;
        }

        // 3) Yürürlük geçişi (başlangıç geldi → aktif; bitiş geçti → süresi doldu). Süresi dolan sözleşme geri aktifleşmez.
        if (ContractDates.Evaluate(c.EffectiveStatus, today, c.StartDate, c.EndDate, c.IsIndefinite) is { } target)
        {
            var moved = await CommitAsync(
                record, [ContractsModule.Permissions.ContractsWrite], target == EffectiveStatus.Expired ? ContractEventKinds.PeriodExpired : ContractEventKinds.Activated,
                target == EffectiveStatus.Expired ? $"Dönem {c.EndDate:dd.MM.yyyy} tarihinde sona erdi." : $"Başlangıç tarihi ({c.StartDate:dd.MM.yyyy}) geldi.",
                new JsonObject { ["effectiveStatus"] = target }, (_, _, _) => Task.CompletedTask, cancellationToken);
            if (moved.IsSuccess)
            {
                if (target == EffectiveStatus.Expired) { counters.PeriodsExpired++; } else { counters.Activated++; }
                (record, c) = await LoadAsync(contractId, cancellationToken);
                if (record is null || c is null)
                {
                    return;
                }
            }
        }

        // 4–5) Yenileme hatırlatmaları ve eskalasyon: yalnızca yürürlükteki, süreli, bu dönem için kararı olmayan sözleşme.
        if (c.EffectiveStatus != EffectiveStatus.Active || c.IsIndefinite || c.EndDate is not { } periodEnd
            || (await reader.RenewalsAsync(c.Id, cancellationToken)).Any(r => r.PeriodEnd == periodEnd))
        {
            return;
        }

        var existing = (await reader.RemindersAsync(c.Id, cancellationToken)).Where(r => r.PeriodKey == periodEnd).ToList();
        if (ContractRenewalRules.Target(c.NoticeDeadline, c.EndDate, c.IsIndefinite) is { } deadline)
        {
            var recorded = existing.Where(r => r.Kind == ReminderKind.Renewal && !ReminderStatus.IsRetryable(r.Status)).Select(r => r.ThresholdDays).ToHashSet();
            var (fire, missed) = ContractRenewalRules.DueThresholds(today, deadline, ContractRenewalRules.ParseThresholds(settings.RenewalReminderDays), recorded);
            var retry = existing.FirstOrDefault(r => r.Kind == ReminderKind.Renewal && ReminderStatus.IsRetryable(r.Status));
            if (fire is null && retry is not null && today <= deadline)
            {
                fire = retry.ThresholdDays;
            }

            if (fire is { } threshold)
            {
                var subject = $"Sözleşme yenileme: {c.Title} ({c.Number}) — son bildirim {deadline:dd.MM.yyyy}, {threshold} gün eşiği";
                var description = $"{c.Title} sözleşmesinde {(c.NoticeDeadline is null ? "bitişe" : "yenilememe/fesih bildirim tarihine")} {deadline.DayNumber - today.DayNumber} gün kaldı. Yenileme yöntemi: {c.RenewalMode}. Yenileme taslağı açın ya da yenilememe kararı verin.";
                var (status, activityId, note) = await CreateActivityAsync(c, c.OwnerId, subject, description, deadline, cancellationToken);
                var existingRetry = retry is not null && retry.ThresholdDays == threshold ? retry : null;
                var committed = await CommitAsync(
                    record, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.RenewalReminder, subject, new JsonObject(),
                    async (ctx, contract, ct) =>
                    {
                        if (existingRetry is not null)
                        {
                            await ContractRenewalWriter.UpdateReminderAsync(ctx, existingRetry.Id, status, activityId, note, ct);
                        }
                        else
                        {
                            ContractRenewalWriter.AddReminder(ctx, contract.Id, periodEnd, ReminderKind.Renewal, threshold, status, activityId, note);
                        }

                        foreach (var older in missed)
                        {
                            ContractRenewalWriter.AddReminder(ctx, contract.Id, periodEnd, ReminderKind.Renewal, older, ReminderStatus.Skipped, null, "Zamanlanmış iş bu eşikte çalışmadı; daha dar eşik için hatırlatma açıldı.");
                        }
                    },
                    cancellationToken);
                if (committed.IsSuccess)
                {
                    if (status == ReminderStatus.Created) { counters.RemindersCreated++; }
                    counters.RemindersSkipped += missed.Count + (status == ReminderStatus.Skipped ? 1 : 0);
                    (record, c) = await LoadAsync(contractId, cancellationToken);
                    if (record is null || c is null)
                    {
                        return;
                    }
                }
            }
        }

        if (ContractRenewalRules.NoticePassed(today, c.NoticeDeadline) && !existing.Any(r => r.Kind == ReminderKind.Escalation))
        {
            var subject = $"Yenileme kararı gecikti: {c.Title} ({c.Number}) — son bildirim tarihi {c.NoticeDeadline:dd.MM.yyyy} geçti";
            var (status, activityId, note) = await CreateActivityAsync(
                c, c.EscalationUserId, subject, $"{c.Title} sözleşmesi için son bildirim tarihi geçti ve yenileme kararı yok. Dönem bitişi {c.EndDate:dd.MM.yyyy}.", c.EndDate!.Value, cancellationToken);
            var committed = await CommitAsync(
                record, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.RenewalReminder, subject, new JsonObject(),
                (ctx, contract, _) =>
                {
                    ContractRenewalWriter.AddReminder(ctx, contract.Id, periodEnd, ReminderKind.Escalation, 0, status, activityId, note);
                    return Task.CompletedTask;
                },
                cancellationToken);
            if (committed.IsSuccess && status == ReminderStatus.Created)
            {
                counters.EscalationsCreated++;
            }
        }
    }

    /// <summary>Onay hedef tarihi geçmiş sözleşme için (bir kez) adlı mevcut adım onaylayıcılarına takip aktivitesi açar; adlı onaylayıcı yoksa işaretlenir ama aktivite açılmaz.</summary>
    private async Task<int> RemindApprovalAsync(Contract contract, ContractVersion version, DateOnly today, CancellationToken cancellationToken)
    {
        var due = DateOnly.FromDateTime(version.ApprovalDueAt!.Value.UtcDateTime);
        if ((await reader.RemindersAsync(contract.Id, cancellationToken)).Any(r => r.Kind == ReminderKind.Approval && r.PeriodKey == due && r.ThresholdDays == version.ApprovalRound))
        {
            return 0;
        }

        var record = await pipeline.GetAsync(new RecordRef(Key, contract.Id), cancellationToken);
        if (!record.IsSuccess)
        {
            return 0;
        }

        var state = await approvals.StateAsync(contract, cancellationToken);
        var recipients = state.Current is { } step ? step.Approvers.Where(a => !step.ApprovedBy.Contains(a)).ToList() : [];
        var created = 0;
        var note = recipients.Count == 0 ? "Adlı onaylayıcı yok (onay izni olan herkes onaylayabilir); aktivite açılmadı." : null;
        foreach (var userId in recipients)
        {
            var (status, _, _) = await CreateActivityAsync(
                contract, userId, $"Sözleşme onayı gecikti: {contract.Title} ({contract.Number})",
                $"{contract.Title} sözleşmesi iç onayınızı bekliyor; hedef tarih ({due:dd.MM.yyyy}) geçti.", today, cancellationToken);
            created += status == ReminderStatus.Created ? 1 : 0;
        }

        var committed = await CommitAsync(
            record.Value!, [ContractsModule.Permissions.RenewalsManage], ContractEventKinds.RenewalReminder, "Onay hedef tarihi geçti: onaylayıcılara hatırlatma", new JsonObject(),
            (ctx, c, _) =>
            {
                ContractRenewalWriter.AddReminder(ctx, c.Id, due, ReminderKind.Approval, version.ApprovalRound, recipients.Count == 0 ? ReminderStatus.Skipped : ReminderStatus.Created, null, note);
                return Task.CompletedTask;
            },
            cancellationToken);
        return committed.IsSuccess ? created : 0;
    }

    private async Task<(string Status, Guid? ActivityId, string? Note)> CreateActivityAsync(
        Contract contract, Guid? assignee, string subject, string description, DateOnly dueDate, CancellationToken cancellationToken)
    {
        if (assignee is null)
        {
            return (ReminderStatus.Skipped, null, "Atanacak kullanıcı yok (sorumlu ya da eskalasyon kullanıcısı tanımlı değil); aktivite açılmadı.");
        }

        if (followUps is null)
        {
            return (ReminderStatus.Unavailable, null, "Aktivite modülü kurulu değil.");
        }

        var result = await followUps.CreateAsync(new FollowUpRequest(subject, description, dueDate, assignee, ContractsModule.Entities.Contract, contract.Id), cancellationToken);
        return result.IsCreated
            ? (ReminderStatus.Created, result.ActivityId, null)
            : (result.Unavailable ? ReminderStatus.Unavailable : ReminderStatus.Failed, null, result.Error);
    }

    private async Task<(RecordData? Record, Contract? Contract)> LoadAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, contractId), cancellationToken);
        return record.IsSuccess ? (record.Value, await reader.ContractAsync(contractId, cancellationToken)) : (null, null);
    }

    private async Task<RecordResult<RecordData>> CommitAsync(
        RecordData record, IReadOnlyList<string> permissions, string eventKind, string? reason, JsonObject values, Func<ContractsStoreContext, Contract, CancellationToken, Task> effect,
        CancellationToken cancellationToken)
    {
        using (gate.Open(permissions, eventKind, reason, effect))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, record.Id, values, record.Version) { Reason = $"contract.{eventKind}" }, cancellationToken);
        }
    }
}
