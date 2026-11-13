using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Marketing.Audiences;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Records;

namespace Modulix.Modules.Marketing.Runs;

public sealed record RunSummary(
    string Status, int Total, IReadOnlyDictionary<string, int> Statuses, IReadOnlyDictionary<string, int> Codes, bool ProviderConfigured, string Provider,
    bool UnsubscribeLinkAvailable);

public sealed record DeliveryItem(Guid Id, Guid TargetId, Guid? PersonId, string Status, string? Code, int Attempts, string? ProviderMessageId, DateTimeOffset UpdatedAt);

public sealed record DeliveryPage(IReadOnlyList<DeliveryItem> Items, int Page, int PageSize, int TotalCount);

/// <param name="Processed">Bu çağrıda işlenen teslimat sayısı.</param>
/// <param name="Remaining">Hâlâ sırada ya da işlenmekte olan teslimat sayısı.</param>
public sealed record ProcessResult(int Processed, int Accepted, int Failed, int Skipped, int Unknown, int Requeued, int Remaining, bool Completed);

/// <param name="Error">İşlenemediyse nedeni (ör. sağlayıcı yapılandırılmamış); işlendiyse null.</param>
public sealed record RunningProcess(Guid RunId, int Processed, bool Completed, string? Error);

/// <summary>
/// Kampanya çalıştırma (ADR-0025, dilim 5): onay akışı, başlatma ve işleme. Durum geçişleri kayıt boru hattından (denetim, olay, sürüm) geçer; teslimat satırları
/// günlük yazıcıyla yazılır. Her gönderimden hemen önce adres yeniden çözülür, izin ve engel yeniden denetlenir, sıklık sınırı atomik ayrılır; anlık görüntü bu
/// denetimleri atlatmaz. Sağlayıcı yapılandırılmamışsa gönderim bloklanır. Sağlayıcı kabulünden sonra sonuç kaybolursa satır "belirsiz" olur ve kör yeniden denenmez.
/// </summary>
public interface ICampaignRuns
{
    /// <summary>Eylem: submit, approve, reject, reopen, start, pause, resume, cancel (<see cref="RunActions"/>).</summary>
    Task<RecordResult<RecordData>> ActAsync(Guid runId, string action, string? reason, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Çalışan çalıştırmanın sıradaki bir grubunu işler (işçi ya da elle çağrılır; tekrar çağrı güvenlidir).</summary>
    Task<RecordResult<ProcessResult>> ProcessAsync(Guid runId, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Arka plan işçisi için (dilim 10): aktif tenant'ta çalışan çalıştırmaların her birinden en çok <paramref name="maxBatches"/> grup işler. Çalıştırmanın başlatılması
    /// ve onayı yine kullanıcı eylemidir; işçi yalnızca zaten "çalışıyor" olan çalıştırmanın sıradaki satırlarını gönderir. Bir çalıştırmanın hatası diğerlerini durdurmaz.
    /// </summary>
    Task<IReadOnlyList<RunningProcess>> ProcessRunningAsync(int batchSize, int maxBatches, int maxRuns, CancellationToken cancellationToken);

    Task<RecordResult<RunSummary>> SummaryAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Teslimat satırları (alıcı görüntüleme yetkisi gerekir); adres içermez.</summary>
    Task<RecordResult<DeliveryPage>> DeliveriesAsync(Guid runId, string? status, string? code, int page, int pageSize, CancellationToken cancellationToken);
}

internal sealed class CampaignRuns(
    IRecordPipeline pipeline,
    IMarketingPolicyProvider policyProvider,
    IEnumerable<IMessageProvider> providers,
    IEnumerable<IAddressLookup> lookups,
    ICommunicationPreferences preferences,
    IFrequencyLimiter frequency,
    IUnsubscribeLinks unsubscribe,
    IJournalWriter journal,
    ITransactionLock locks,
    ICurrentAccess permissions,
    ITenantContextAccessor tenant,
    ITenantClock clock,
    DeliveryJournal deliveries,
    SnapshotRecipients snapshotRecipients,
    SegmentWorkflowGate gate,
    TimeProvider time) : ICampaignRuns
{
    private const int MaxAttempts = 3;
    private const int MaxBatch = 200;
    private const int SaveEvery = 25;
    private const int ProviderParallelism = 8;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string[]> ActionPermissions = new Dictionary<string, string[]>
    {
        [RunActions.Submit] = [MarketingModule.Permissions.CampaignsWrite],
        [RunActions.Reopen] = [MarketingModule.Permissions.CampaignsWrite, MarketingModule.Permissions.CampaignsApprove],
        [RunActions.Approve] = [MarketingModule.Permissions.CampaignsApprove],
        [RunActions.Reject] = [MarketingModule.Permissions.CampaignsApprove],
        [RunActions.Start] = [MarketingModule.Permissions.CampaignsSend],
        [RunActions.Pause] = [MarketingModule.Permissions.CampaignsSend],
        [RunActions.Resume] = [MarketingModule.Permissions.CampaignsSend],
        [RunActions.Cancel] = [MarketingModule.Permissions.CampaignsSend, MarketingModule.Permissions.CampaignsWrite],
    };

    // ---- Eylemler ----

    public async Task<RecordResult<RecordData>> ActAsync(Guid runId, string action, string? reason, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (!ActionPermissions.TryGetValue(action, out var needed))
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", ("action", "Geçersiz eylem."));
        }

        if (!await HasAnyAsync(needed, cancellationToken))
        {
            return Fail<RecordData>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{string.Join("' ya da '", needed)}' izni gerekir.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignRun, runId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var run = RunState.From(loaded.Value!);
        if (!RunTransitions.Allowed(action, run.Status))
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, $"'{StatusText(run.Status)}' durumundaki çalıştırmada bu işlem yapılamaz.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (RunTransitions.NeedsReason(action) && text is null)
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", ("reason", "Bu işlem için gerekçe zorunludur."));
        }

        if (text?.Length > CampaignRun.NoteMaxLength)
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Gerekçe çok uzun.", ("reason", $"Gerekçe en fazla {CampaignRun.NoteMaxLength} karakter olabilir."));
        }

        var policy = await policyProvider.GetAsync(cancellationToken);
        var now = time.GetUtcNow();
        var userId = tenant.Current?.UserId;
        var values = new JsonObject();
        using var open = gate.Open(needed);

        switch (action)
        {
            case RunActions.Submit:
                if (await ReadinessAsync(run, cancellationToken) is { } notReady)
                {
                    return notReady;
                }

                values["submittedBy"] = userId?.ToString();
                values["submittedAt"] = now.ToString("O");
                values["note"] = null;
                if (!policy.ApprovalRequired)
                {
                    values["approvedAt"] = now.ToString("O");
                    values["approvedSnapshotId"] = run.SnapshotId?.ToString();
                    values["approvedContentId"] = run.ContentId?.ToString();
                }

                break;

            case RunActions.Approve:
                if (run.SubmittedBy == userId)
                {
                    return Fail<RecordData>(RecordErrorCode.PermissionDenied, "Hazırlayan kendi çalıştırmasını onaylayamaz (görev ayrılığı).");
                }

                values["approvedBy"] = userId?.ToString();
                values["approvedAt"] = now.ToString("O");
                values["approvedSnapshotId"] = run.SnapshotId?.ToString();
                values["approvedContentId"] = run.ContentId?.ToString();
                values["note"] = null;
                break;

            case RunActions.Reject:
            case RunActions.Reopen:
                ClearApproval(values);
                values["note"] = text;
                break;

            case RunActions.Start:
                return await StartAsync(run, policy, expectedVersion, cancellationToken);

            case RunActions.Cancel:
                values["note"] = text;
                values["completedAt"] = now.ToString("O");
                break;
        }

        values["status"] = RunTransitions.Next(action, policy.ApprovalRequired);
        if (action == RunActions.Cancel && run.Status is RunStatuses.Running or RunStatuses.Paused)
        {
            return await pipeline.RunAtomicAsync(async ct =>
            {
                await locks.AcquireAsync(LockName(runId), ct);
                var update = await pipeline.UpdateAsync(new RecordCommand(MarketingModule.Entities.CampaignRun, runId, values, expectedVersion), ct);
                if (!update.IsSuccess)
                {
                    return update;
                }

                // İptalden sonra yeni iş çıkmaz: sıradaki satırlar iptal edilir. Sağlayıcıya verilmiş iletiler geri alınmış gibi gösterilmez.
                foreach (var queued in await deliveries.TrackedAsync(runId, DeliveryStatuses.Queued, int.MaxValue, ct))
                {
                    queued.Status = DeliveryStatuses.Cancelled;
                    queued.Code = "run-cancelled";
                    queued.UpdatedAt = now;
                }

                await journal.SaveAsync(ct);
                return update;
            }, cancellationToken);
        }

        return await pipeline.UpdateAsync(new RecordCommand(MarketingModule.Entities.CampaignRun, runId, values, expectedVersion), cancellationToken);
    }

    private async Task<RecordResult<RecordData>?> ReadinessAsync(RunState run, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var campaign = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, run.CampaignId), cancellationToken);
        if (!campaign.IsSuccess)
        {
            return RecordResult<RecordData>.Fail(campaign.Error!.Code, campaign.Error.Message, campaign.Error.FieldErrors);
        }

        if (campaign.Value!.Values["status"]?.GetValue<string>() == CampaignStatuses.Archived)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Arşivlenen kampanyada çalıştırma onaya gönderilemez.");
        }

        var channel = Enum.Parse<CommunicationChannel>(campaign.Value.Values["channel"]!.GetValue<string>());
        if (run.SnapshotId is not { } snapshotId)
        {
            errors.Add(new RecordFieldError("snapshotId", "Hedef kitle anlık görüntüsü seçilmelidir."));
        }
        else
        {
            var snapshot = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.AudienceSnapshot, snapshotId), cancellationToken);
            if (!snapshot.IsSuccess)
            {
                errors.Add(new RecordFieldError("snapshotId", "Hedef kitle anlık görüntüsü bulunamadı."));
            }
            else if ((snapshot.Value!.Values["eligibleCount"]?.GetValue<int>() ?? 0) == 0)
            {
                errors.Add(new RecordFieldError("snapshotId", "Dondurulmuş hedef kitlede uygun alıcı yok."));
            }
        }

        if (run.ContentId is not { } contentId)
        {
            errors.Add(new RecordFieldError("contentId", "İçerik sürümü seçilmelidir."));
        }
        else
        {
            var content = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignContent, contentId), cancellationToken);
            if (!content.IsSuccess)
            {
                errors.Add(new RecordFieldError("contentId", "İçerik sürümü bulunamadı."));
            }
            else
            {
                errors.AddRange(ContentRules.Validate(channel, content.Value!.Values["subject"]?.GetValue<string>(), content.Value.Values["body"]?.GetValue<string>())
                    .Select(e => new RecordFieldError("contentId", e.Message)));
            }
        }

        if (run.ScheduledAt is { } at && at <= time.GetUtcNow())
        {
            errors.Add(new RecordFieldError("scheduledAt", "Planlanan zaman geçmişte; yeni bir zaman seçin ya da boş bırakın."));
        }

        return errors.Count == 0
            ? null
            : RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Çalıştırma onaya hazır değil.", errors);
    }

    private async Task<RecordResult<RecordData>> StartAsync(RunState run, MarketingPolicy policy, long? expectedVersion, CancellationToken cancellationToken)
    {
        var campaign = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, run.CampaignId), cancellationToken);
        if (!campaign.IsSuccess)
        {
            return RecordResult<RecordData>.Fail(campaign.Error!.Code, campaign.Error.Message, campaign.Error.FieldErrors);
        }

        var info = CampaignInfo.From(campaign.Value!);
        if (SendBlock(policy, info.Channel) is { } blocked)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, blocked);
        }

        if (info.Status == CampaignStatuses.Archived)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Arşivlenen kampanya başlatılamaz.");
        }

        var now = time.GetUtcNow();
        if (run.ScheduledAt is { } scheduled && scheduled > now)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Planlanan zaman henüz gelmedi.");
        }

        var today = (await clock.GetAsync(cancellationToken)).Today;
        if (info.StartsOn is { } starts && today < starts)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Kampanya dönemi henüz başlamadı.");
        }

        if (info.EndsOn is { } ends && today > ends)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Kampanya dönemi sona erdi.");
        }

        if (run.SnapshotId is null || run.ContentId is null || run.ApprovedSnapshotId != run.SnapshotId || run.ApprovedContentId != run.ContentId)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Onaydan sonra içerik ya da hedef kitle değişmiş; çalıştırma yeniden onaylanmalıdır.");
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await locks.AcquireAsync(LockName(run.Id), ct);

            // Kilit alındıktan sonra yeniden oku: aynı anda ikinci başlatma çift teslimat satırı açamaz.
            var current = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignRun, run.Id), ct);
            if (!current.IsSuccess || RunState.From(current.Value!).Status != RunStatuses.Approved)
            {
                return Fail<RecordData>(RecordErrorCode.Conflict, "Çalıştırma başka bir istekle başlatılmış ya da durumu değişmiş.");
            }

            await MaterializeAsync(run, info, ct);
            await journal.SaveAsync(ct);
            return await pipeline.UpdateAsync(
                new RecordCommand(MarketingModule.Entities.CampaignRun, run.Id, new JsonObject
                {
                    ["status"] = RunStatuses.Running,
                    ["startedAt"] = time.GetUtcNow().ToString("O"),
                }, expectedVersion),
                ct);
        }, cancellationToken);
    }

    /// <summary>Anlık görüntünün uygun alıcılarından sıra satırları açar: adres yeniden çözülür; aynı adres tek satır; çözülemeyen "adres yok" olarak atlanır.</summary>
    private async Task MaterializeAsync(RunState run, CampaignInfo campaign, CancellationToken cancellationToken)
    {
        var rows = CampaignAudienceService.Unpack(await snapshotRecipients.ReadAsync(run.SnapshotId!.Value, cancellationToken))
            .Where(r => r.Status == AudienceStatuses.Eligible)
            .ToList();
        var lookup = lookups.FirstOrDefault(l => l.Supports(campaign.TargetType));
        var found = lookup is null
            ? []
            : (await lookup.LookupAsync(campaign.TargetType, campaign.Channel, [.. rows.Select(r => new AddressLookup(r.TargetId, r.ContactPointId))], cancellationToken))
                .ToDictionary(a => (a.TargetId, a.ContactPointId), a => a.Address);

        var tenantId = tenant.Current!.TenantId;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            var normalized = found.TryGetValue((row.TargetId, row.ContactPointId), out var raw) ? CommunicationAddress.Normalize(campaign.Channel, raw) : null;
            var gone = normalized is null;
            var key = DeliveryKeys.For(tenantId, run.Id, campaign.Channel, normalized ?? $"gone:{row.TargetId:N}:{row.ContactPointId:N}");
            if (!keys.Add(key))
            {
                continue;
            }

            journal.Add(new CampaignDelivery
            {
                RunId = run.Id,
                TargetId = row.TargetId,
                PersonId = row.PersonId,
                ContactPointId = row.ContactPointId,
                IdempotencyKey = key,
                Status = gone ? DeliveryStatuses.Skipped : DeliveryStatuses.Queued,
                Code = gone ? "address-gone" : null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    // ---- İşleme ----

    public async Task<RecordResult<ProcessResult>> ProcessAsync(Guid runId, int batchSize, CancellationToken cancellationToken)
    {
        if (!await HasAnyAsync([MarketingModule.Permissions.CampaignsSend], cancellationToken))
        {
            return Fail<ProcessResult>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.CampaignsSend}' izni gerekir.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignRun, runId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<ProcessResult>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var run = RunState.From(loaded.Value!);
        if (run.Status != RunStatuses.Running)
        {
            return Fail<ProcessResult>(RecordErrorCode.Conflict, $"Yalnızca çalışan çalıştırma işlenir (durum: {StatusText(run.Status)}). Duraklatılan ya da iptal edilen çalıştırmada yeni iş çıkmaz.");
        }

        var campaignResult = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, run.CampaignId), cancellationToken);
        if (!campaignResult.IsSuccess)
        {
            return RecordResult<ProcessResult>.Fail(campaignResult.Error!.Code, campaignResult.Error.Message, campaignResult.Error.FieldErrors);
        }

        var campaign = CampaignInfo.From(campaignResult.Value!);
        var policy = await policyProvider.GetAsync(cancellationToken);
        if (SendBlock(policy, campaign.Channel) is { } blocked)
        {
            return Fail<ProcessResult>(RecordErrorCode.Conflict, blocked);
        }

        var provider = providers.First(p => p.Key == policy.Provider);
        var content = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignContent, run.ContentId!.Value), cancellationToken);
        if (!content.IsSuccess)
        {
            return RecordResult<ProcessResult>.Fail(content.Error!.Code, content.Error.Message, content.Error.FieldErrors);
        }

        var now = time.GetUtcNow();
        var size = Math.Clamp(batchSize, 1, MaxBatch);
        var claimed = new List<CampaignDelivery>();
        var claim = await pipeline.RunAtomicAsync(async ct =>
        {
            await locks.AcquireAsync(LockName(runId), ct);

            // Uzun süredir "işleniyor" kalan satırın sağlayıcıya gidip gitmediği bilinmez: kör yeniden deneme çift gönderim yaratabilir, bu yüzden belirsiz sayılır.
            foreach (var stale in await deliveries.StaleSendingAsync(runId, now - StaleAfter, ct))
            {
                stale.Status = DeliveryStatuses.Unknown;
                stale.Code = "stale-sending";
                stale.UpdatedAt = now;
            }

            foreach (var delivery in await deliveries.TrackedAsync(runId, DeliveryStatuses.Queued, size, ct))
            {
                delivery.Status = DeliveryStatuses.Sending;
                delivery.Attempts++;
                delivery.UpdatedAt = now;
                claimed.Add(delivery);
            }

            await journal.SaveAsync(ct);
            return RecordResult<bool>.Ok(true);
        }, cancellationToken);
        if (!claim.IsSuccess)
        {
            return RecordResult<ProcessResult>.Fail(claim.Error!.Code, claim.Error.Message, claim.Error.FieldErrors);
        }

        // İşlem bitince izleme durumu korunmayabilir: satırları yeniden izleyerek yazılacak değişikliklerin kaydedilmesini güvenceye alır.
        var claimedIds = claimed.Select(d => d.Id).ToList();
        claimed = claimedIds.Count == 0 ? [] : await deliveries.TrackedByIdsAsync(claimedIds, cancellationToken);

        var counts = new Tally();
        if (claimed.Count > 0)
        {
            await SendBatchAsync(claimed, campaign, provider, policy, content.Value!, counts, cancellationToken);
        }

        var remaining = await deliveries.AnyOpenAsync(runId, cancellationToken);
        var completed = false;
        if (!remaining)
        {
            using var open = gate.Open([MarketingModule.Permissions.CampaignsSend]);
            var done = await pipeline.UpdateAsync(
                new RecordCommand(MarketingModule.Entities.CampaignRun, runId, new JsonObject
                {
                    ["status"] = RunStatuses.Completed,
                    ["completedAt"] = time.GetUtcNow().ToString("O"),
                }),
                cancellationToken);
            completed = done.IsSuccess;
        }

        var counts2 = await deliveries.CountsAsync(runId, cancellationToken);
        var open2 = counts2.GetValueOrDefault(DeliveryStatuses.Queued) + counts2.GetValueOrDefault(DeliveryStatuses.Sending);
        return RecordResult<ProcessResult>.Ok(new ProcessResult(claimed.Count, counts.Accepted, counts.Failed, counts.Skipped, counts.Unknown, counts.Requeued, open2, completed));
    }

    public async Task<IReadOnlyList<RunningProcess>> ProcessRunningAsync(int batchSize, int maxBatches, int maxRuns, CancellationToken cancellationToken)
    {
        var results = new List<RunningProcess>();
        foreach (var runId in await deliveries.RunningRunIdsAsync(Math.Clamp(maxRuns, 1, 50), cancellationToken))
        {
            var processed = 0;
            var completed = false;
            string? error = null;
            for (var i = 0; i < Math.Max(maxBatches, 1) && !completed; i++)
            {
                var step = await ProcessAsync(runId, batchSize, cancellationToken);
                if (!step.IsSuccess)
                {
                    error = step.Error!.Message;
                    break;
                }

                processed += step.Value!.Processed;
                completed = step.Value.Completed;
                if (step.Value.Processed == 0)
                {
                    break;
                }
            }

            results.Add(new RunningProcess(runId, processed, completed, error));
        }

        return results;
    }

    private async Task SendBatchAsync(
        List<CampaignDelivery> batch, CampaignInfo campaign, IMessageProvider provider, MarketingPolicy policy, RecordData content, Tally counts, CancellationToken cancellationToken)
    {
        var today = (await clock.GetAsync(cancellationToken)).Today;
        var lookup = lookups.FirstOrDefault(l => l.Supports(campaign.TargetType));
        var found = lookup is null
            ? []
            : (await lookup.LookupAsync(campaign.TargetType, campaign.Channel, [.. batch.Select(d => new AddressLookup(d.TargetId, d.ContactPointId))], cancellationToken))
                .ToDictionary(a => (a.TargetId, a.ContactPointId));

        var normalized = batch.ToDictionary(
            d => d.Id,
            d => found.TryGetValue((d.TargetId, d.ContactPointId), out var raw) ? CommunicationAddress.Normalize(campaign.Channel, raw.Address) : null);
        var variables = batch.ToDictionary(d => d.Id, d => found.TryGetValue((d.TargetId, d.ContactPointId), out var raw) ? raw.Variables : null);

        // İzin ve engel her gönderimden hemen önce yeniden değerlendirilir (anlık görüntüdeki durum geçerli sayılmaz).
        var decisions = normalized.Values.Any(a => a is not null)
            ? await preferences.EvaluateManyAsync(
                campaign.Channel, CommunicationPurpose.Marketing, [.. normalized.Values.Where(a => a is not null).Select(a => a!).Distinct(StringComparer.Ordinal)], campaign.Topic, cancellationToken)
            : new Dictionary<string, CommunicationDecision>();

        var subject = content.Values["subject"]?.GetValue<string>();
        var body = content.Values["body"]!.GetValue<string>();

        // Dilim 10: satırlar SaveEvery'lik gruplarla işlenir. Grup başına tek bağlantı üretimi ve tek günlük kaydı; sağlayıcı çağrıları sınırlı paralel.
        // Çökme penceresi bir grupla sınırlıdır: kaydedilmemiş satırlar "işleniyor" kalır ve süre dolunca "belirsiz" olur (kör yeniden deneme yok).
        foreach (var chunk in batch.Chunk(SaveEvery))
        {
            var pending = new List<(CampaignDelivery Delivery, string Address)>();
            foreach (var delivery in chunk)
            {
                var address = normalized[delivery.Id];
                if (address is null)
                {
                    Finish(delivery, DeliveryStatuses.Skipped, "address-gone", counts);
                }
                else if (!decisions[address].IsAllowed)
                {
                    Finish(delivery, DeliveryStatuses.Skipped, decisions[address].Code, counts);
                }
                else if (policy.DailyLimit > 0 && !await frequency.TryReserveAsync(campaign.Channel, address, today, policy.DailyLimit, cancellationToken))
                {
                    Finish(delivery, DeliveryStatuses.Skipped, "frequency-limit", counts);
                }
                else
                {
                    pending.Add((delivery, address));
                }
            }

            await SendPendingAsync(pending, variables, campaign, provider, policy, today, subject, body, counts, cancellationToken);
            await journal.SaveAsync(cancellationToken);
        }
    }

    private async Task SendPendingAsync(
        List<(CampaignDelivery Delivery, string Address)> pending, Dictionary<Guid, IReadOnlyDictionary<string, string>?> variables, CampaignInfo campaign, IMessageProvider provider, MarketingPolicy policy, DateOnly today,
        string? subject, string body, Tally counts, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        // Bağlantılar iletiden önce kaydedilir; üretilemezse ileti gönderilmez ve ayrılan sıklık hakkı geri verilir.
        var links = await unsubscribe.IssueManyAsync(campaign.Channel, [.. pending.Select(p => (p.Address, p.Delivery.IdempotencyKey))], cancellationToken);
        var ready = new List<(CampaignDelivery Delivery, string Address, UnsubscribeLink Link)>();
        for (var i = 0; i < pending.Count; i++)
        {
            if (links[i] is { } link)
            {
                ready.Add((pending[i].Delivery, pending[i].Address, link));
            }
            else
            {
                await ReleaseAsync(campaign, pending[i].Address, today, policy, cancellationToken);
                Finish(pending[i].Delivery, DeliveryStatuses.Failed, "unsubscribe-link-unavailable", counts);
            }
        }

        // Yalnızca sağlayıcı çağrısı paraleldir; veritabanı işleri (bu bağlam iş parçacığı güvenli değildir) sıralı kalır.
        var results = new ProviderResult[ready.Count];
        using var limiter = new SemaphoreSlim(ProviderParallelism);
        await Task.WhenAll(ready.Select(async (item, index) =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                results[index] = await CallProviderAsync(item.Delivery, item.Address, item.Link, campaign, provider, subject, body, variables[item.Delivery.Id], cancellationToken);
            }
            finally
            {
                limiter.Release();
            }
        }));

        for (var i = 0; i < ready.Count; i++)
        {
            await ApplyResultAsync(ready[i].Delivery, ready[i].Address, results[i], campaign, policy, today, counts, cancellationToken);
        }
    }

    private static async Task<ProviderResult> CallProviderAsync(
        CampaignDelivery delivery, string address, UnsubscribeLink link, CampaignInfo campaign, IMessageProvider provider, string? subject, string body,
        IReadOnlyDictionary<string, string>? variables, CancellationToken cancellationToken)
    {
        try
        {
            // Kişiye özel değişkenler gönderim anında doldurulur (dilim 11); alt bilgi doldurmadan sonra eklenir, değerler bağlantıya karışmaz.
            var personalSubject = subject is null ? null : ContentTemplate.Render(subject, variables);
            var personalBody = ContentTemplate.Render(body, variables);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProviderTimeout);
            return await provider.SendAsync(
                new MessageRequest(delivery.IdempotencyKey, campaign.Channel, address, personalSubject, ContentRules.WithUnsubscribeFooter(personalBody, link.PageUrl), link), timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Sağlayıcı iletiyi kabul etmiş olabilir: sonuç bilinmiyor, kör yeniden deneme yok, sıklık hakkı korunur.
            return new ProviderResult(ProviderOutcome.Unknown, null, ex is OperationCanceledException ? "timeout" : "provider-exception");
        }
    }

    private async Task ApplyResultAsync(
        CampaignDelivery delivery, string address, ProviderResult result, CampaignInfo campaign, MarketingPolicy policy, DateOnly today, Tally counts,
        CancellationToken cancellationToken)
    {
        delivery.ProviderMessageId = result.ProviderMessageId;
        switch (result.Outcome)
        {
            case ProviderOutcome.Accepted:
                Finish(delivery, DeliveryStatuses.Accepted, null, counts);
                break;

            case ProviderOutcome.TransientFailure when delivery.Attempts < MaxAttempts:
                await ReleaseAsync(campaign, address, today, policy, cancellationToken);
                delivery.Status = DeliveryStatuses.Queued;
                delivery.Code = result.ErrorCode;
                delivery.UpdatedAt = time.GetUtcNow();
                counts.Requeued++;
                break;

            case ProviderOutcome.Unknown:
                Finish(delivery, DeliveryStatuses.Unknown, result.ErrorCode ?? "unknown-result", counts);
                break;

            default:
                await ReleaseAsync(campaign, address, today, policy, cancellationToken);
                Finish(delivery, DeliveryStatuses.Failed, result.ErrorCode ?? "provider-failed", counts);
                break;
        }
    }

    private async Task ReleaseAsync(CampaignInfo campaign, string address, DateOnly today, MarketingPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.DailyLimit > 0)
        {
            await frequency.ReleaseAsync(campaign.Channel, address, today, cancellationToken);
        }
    }

    private void Finish(CampaignDelivery delivery, string status, string? code, Tally counts)
    {
        delivery.Status = status;
        delivery.Code = code;
        delivery.UpdatedAt = time.GetUtcNow();
        switch (status)
        {
            case DeliveryStatuses.Accepted: counts.Accepted++; break;
            case DeliveryStatuses.Failed: counts.Failed++; break;
            case DeliveryStatuses.Skipped: counts.Skipped++; break;
            case DeliveryStatuses.Unknown: counts.Unknown++; break;
        }
    }

    // ---- Okuma ----

    public async Task<RecordResult<RunSummary>> SummaryAsync(Guid runId, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignRun, runId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<RunSummary>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var statuses = await deliveries.CountsAsync(runId, cancellationToken);
        var policy = await policyProvider.GetAsync(cancellationToken);
        return RecordResult<RunSummary>.Ok(new RunSummary(
            RunState.From(loaded.Value!).Status, statuses.Values.Sum(), statuses, await deliveries.CodesAsync(runId, cancellationToken),
            policy.HasProvider && providers.Any(p => p.Key == policy.Provider), policy.Provider, unsubscribe.IsAvailable));
    }

    public async Task<RecordResult<DeliveryPage>> DeliveriesAsync(Guid runId, string? status, string? code, int page, int pageSize, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignRun, runId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<DeliveryPage>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.RecipientsRead, cancellationToken))
        {
            return Fail<DeliveryPage>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.RecipientsRead}' izni gerekir.");
        }

        var size = Math.Clamp(pageSize, 1, 200);
        var number = Math.Clamp(page, 1, 100_000);
        var (items, total) = await deliveries.ListAsync(runId, status, code, number, size, cancellationToken);
        return RecordResult<DeliveryPage>.Ok(new DeliveryPage(
            [.. items.Select(d => new DeliveryItem(d.Id, d.TargetId, d.PersonId, d.Status, d.Code, d.Attempts, d.ProviderMessageId, d.UpdatedAt))], number, size, total));
    }

    // ---- Yardımcılar ----

    private string? SendBlock(MarketingPolicy policy, CommunicationChannel channel)
    {
        if (!policy.HasProvider)
        {
            return "Gönderim sağlayıcısı yapılandırılmamış; gerçek gönderim bloklu. Pazarlama ayarlarında bir sağlayıcı seçilene kadar çalıştırma başlatılamaz.";
        }

        var provider = providers.FirstOrDefault(p => p.Key == policy.Provider);
        if (provider is null || !provider.Supports(channel))
        {
            return $"Seçilen sağlayıcı ('{policy.Provider}') bu kanalı desteklemiyor ya da kayıtlı değil.";
        }

        // Abonelikten çıkma bağlantısı olmayan pazarlama iletisi gönderilmez (ADR-0025, dilim 6).
        return unsubscribe.IsAvailable
            ? null
            : "Abonelikten çıkma bağlantısı üretilemiyor (uygulamanın genel adresi, App:PublicUrl, ayarlı değil); pazarlama iletisi bağlantısız gönderilmez.";
    }

    private async Task<bool> HasAnyAsync(IReadOnlyList<string> needed, CancellationToken cancellationToken)
    {
        foreach (var permission in needed)
        {
            if (await permissions.HasPermissionAsync(permission, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private string LockName(Guid runId) => $"marketing-run:{tenant.Current!.TenantId:N}:{runId:N}";

    private static void ClearApproval(JsonObject values)
    {
        foreach (var field in new[] { "submittedBy", "submittedAt", "approvedBy", "approvedAt", "approvedSnapshotId", "approvedContentId" })
        {
            values[field] = null;
        }
    }

    private static string StatusText(string status) => status switch
    {
        RunStatuses.Draft => "taslak",
        RunStatuses.PendingApproval => "onay bekliyor",
        RunStatuses.Approved => "onaylandı",
        RunStatuses.Running => "çalışıyor",
        RunStatuses.Paused => "duraklatıldı",
        RunStatuses.Completed => "tamamlandı",
        _ => "iptal edildi",
    };

    private static RecordResult<T> Fail<T>(RecordErrorCode code, string message, params (string Field, string Message)[] fields) =>
        RecordResult<T>.Fail(code, message, fields.Length == 0 ? null : [.. fields.Select(f => new RecordFieldError(f.Field, f.Message))]);

    private sealed class Tally
    {
        public int Accepted;
        public int Failed;
        public int Skipped;
        public int Unknown;
        public int Requeued;
    }

    private sealed record RunState(
        Guid Id, Guid CampaignId, string Status, Guid? SnapshotId, Guid? ContentId, DateTimeOffset? ScheduledAt, Guid? SubmittedBy, Guid? ApprovedSnapshotId, Guid? ApprovedContentId)
    {
        public static RunState From(RecordData data)
        {
            var v = data.Values;
            return new RunState(
                data.Id,
                Guid.Parse(v["campaignId"]!.GetValue<string>()),
                v["status"]!.GetValue<string>(),
                GuidOrNull(v["snapshotId"]),
                GuidOrNull(v["contentId"]),
                v["scheduledAt"] is { } at ? DateTimeOffset.Parse(at.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
                GuidOrNull(v["submittedBy"]),
                GuidOrNull(v["approvedSnapshotId"]),
                GuidOrNull(v["approvedContentId"]));
        }

        private static Guid? GuidOrNull(JsonNode? node) => node is null ? null : Guid.Parse(node.GetValue<string>());
    }

    private sealed record CampaignInfo(SegmentTarget TargetType, CommunicationChannel Channel, string Status, DateOnly? StartsOn, DateOnly? EndsOn, string? Topic)
    {
        public static CampaignInfo From(RecordData data)
        {
            var v = data.Values;
            return new CampaignInfo(
                Enum.Parse<SegmentTarget>(v["target"]!.GetValue<string>()),
                Enum.Parse<CommunicationChannel>(v["channel"]!.GetValue<string>()),
                v["status"]!.GetValue<string>(),
                v["startsOn"] is { } s ? DateOnly.Parse(s.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
                v["endsOn"] is { } e ? DateOnly.Parse(e.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : null,
                v["topic"]?.GetValue<string>());
        }
    }
}
