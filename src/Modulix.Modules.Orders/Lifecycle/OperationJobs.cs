using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders.Lifecycle;

public sealed record JobTaskView(Guid Id, long Version, int Position, string Title, bool IsRequired, bool IsDone, Guid? DoneBy, DateTimeOffset? DoneAt, string? Note, bool IsDefault);

public sealed record JobLineView(Guid OrderLineId, string Name, string Kind, decimal Quantity, string? Unit);

/// <summary>Bağımlılık görünümü: <c>DependsOn</c> listesinde işin beklediği işler, <c>Dependents</c> listesinde bu işi bekleyen işler.</summary>
public sealed record JobLinkView(Guid JobId, string Number, string Title, string Status);

public sealed record JobEventView(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Note);

public sealed record JobActionState(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

/// <summary>İş görünümü: kayıt alanları, bağlı kalemler, görevler, bağımlılıklar, yapılabilir eylemler, geçmiş ve ilerleme (tamamlanan görev yüzdesi).</summary>
public sealed record OperationJobView(
    RecordData Job, string OrderNumber, IReadOnlyList<JobLineView> Lines, IReadOnlyList<JobTaskView> Tasks, IReadOnlyList<JobLinkView> DependsOn,
    IReadOnlyList<JobLinkView> Dependents, IReadOnlyList<JobActionState> Actions, IReadOnlyList<JobEventView> History, int ProgressPercent);

/// <summary>Elle iş açma girdisi: bağlanacak kalemler zorunlu; atama isteğe bağlıdır (yoksa "Atama bekliyor").</summary>
public sealed record ManualJobInput(
    string? Title, IReadOnlyList<Guid>? LineIds, string? Priority, Guid? ResponsibleUserId, string? TeamName, DateOnly? PlannedStartDate, DateOnly? PlannedEndDate,
    string? Location, string? Notes);

/// <summary>Eylem girdisi: <c>cancel</c> için gerekçe; <c>assign</c> için sorumlu kullanıcı ve/veya ekip.</summary>
public sealed record JobActionInput(string? Reason, Guid? ResponsibleUserId, string? TeamName);

/// <summary>
/// Operasyon işi iş akışı (ADR-0023, dilim 4). Kabul edilen devirden işler ayarlı stratejiyle devir kabulüyle aynı işlemde bir kez üretilir (yeniden deneme ve
/// tekrar kabul mükerrer iş üretmez); kullanıcı ayrıca elle iş açabilir. İş siparişe ve ilgili kalemlerine bağlıdır, görevleri ürün/hizmet türü şablonundan gelir.
/// Atanacak sorumlu/ekip yoksa iş "Atama bekliyor" durumunda görünür. Bağımlı iş tamamlanmadan iş başlatılamaz; zorunlu görevler bitmeden tamamlanamaz.
/// Eylemler (atama, başlatma, tamamlama, iptal) ve bağımlılık değişiklikleri sunucuda doğrulanır, geçmişe yazılır ve audit'e düşer.
/// </summary>
public interface IOperationJobs
{
    Task<RecordResult<OperationJobView>> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<RecordResult<OperationJobView>> CreateManualAsync(Guid orderId, ManualJobInput input, CancellationToken cancellationToken);

    Task<RecordResult<OperationJobView>> RunAsync(Guid jobId, string action, long version, JobActionInput input, CancellationToken cancellationToken);

    /// <summary>Bu işin <paramref name="dependsOnJobId"/> işi tamamlanmadan başlamamasını sağlar (aynı sipariş, döngü yok).</summary>
    Task<RecordResult<OperationJobView>> AddDependencyAsync(Guid jobId, Guid dependsOnJobId, CancellationToken cancellationToken);

    Task<RecordResult<OperationJobView>> RemoveDependencyAsync(Guid jobId, Guid dependsOnJobId, CancellationToken cancellationToken);
}

/// <summary>Devir kabulünden iş üretimi (modül içi; devir servisi kabul işleminde çağırır).</summary>
internal interface IOperationJobGenerator
{
    /// <summary>
    /// Kabul edilmiş devirden ayarlı stratejiye göre işleri üretir; aynı grup için açık iş varsa atlanır (idempotent). Çağıran, kendi işlem ve kapı izniyle çalışır.
    /// Üretilen işlerin numaralarını döner.
    /// </summary>
    Task<RecordResult<IReadOnlyList<string>>> GenerateAsync(Guid handoverId, string permission, CancellationToken cancellationToken);
}

internal sealed class OperationJobs(
    IRecordPipeline pipeline,
    OrderWorkflowGate gate,
    ICurrentAccess access,
    OrderReader reader,
    OrdersStoreContext storeContext,
    TimeProvider time,
    IOrderPolicyProvider? policy = null) : IOperationJobs, IOperationJobGenerator
{
    private const string Key = OrdersModule.Entities.Job;

    private static readonly IReadOnlyDictionary<string, string> PermissionOf = new Dictionary<string, string>
    {
        [JobAction.Assign] = OrdersModule.Permissions.JobsAssign,
        [JobAction.Start] = OrdersModule.Permissions.JobsManage,
        [JobAction.Complete] = OrdersModule.Permissions.JobsManage,
        [JobAction.Cancel] = OrdersModule.Permissions.JobsManage,
    };

    public async Task<RecordResult<OperationJobView>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, jobId), cancellationToken);
        return record.IsSuccess
            ? RecordResult<OperationJobView>.Ok(await ViewAsync(record.Value!, cancellationToken))
            : RecordResult<OperationJobView>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
    }

    // ---- Devir kabulünden üretim

    public async Task<RecordResult<IReadOnlyList<string>>> GenerateAsync(Guid handoverId, string permission, CancellationToken cancellationToken)
    {
        var handover = await reader.HandoverAsync(handoverId, cancellationToken);
        var order = handover is null ? null : await reader.OrderAsync(handover.OrderId, cancellationToken);
        if (handover is null || order is null)
        {
            return RecordResult<IReadOnlyList<string>>.Fail(RecordErrorCode.NotFound, "Devir ya da sipariş bulunamadı.");
        }

        var settings = policy is null ? OrderPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var lines = (await reader.LinesAsync(order.Id, cancellationToken)).Where(l => !l.IsCancelled && l.EffectiveQuantity > 0m).ToList();
        var planned = JobGenerationPlanner.Plan(order.Number, [.. lines.Select(l => new JobLineInput(l.Id, l.Name, l.Kind))], settings.JobStrategy, settings.JobServiceAfterDelivery);
        var existing = (await reader.JobsOfOrderAsync(order.Id, cancellationToken)).Where(j => j.GroupKey != null && j.Status != JobStatus.Cancelled).ToDictionary(j => j.GroupKey!, j => j.Id);
        var inOpenJobs = await reader.LinesInOpenJobsAsync(order.Id, cancellationToken);
        var created = new Dictionary<string, Guid>(existing);
        var numbers = new List<string>();
        foreach (var plan in planned)
        {
            if (existing.ContainsKey(plan.GroupKey))
            {
                continue;
            }

            // Başka bir açık işte olan kalem bu işe de bağlanmaz (bir kalem aynı anda yalnızca bir açık işte).
            var lineIds = plan.LineIds.Where(id => !inOpenJobs.Contains(id)).ToList();
            if (lineIds.Count == 0)
            {
                continue;
            }

            Guid? dependsOn = plan.DependsOnGroupKey is { } key && created.TryGetValue(key, out var dependencyId) ? dependencyId : null;
            var result = await CreateJobAsync(
                order, handover.Id, plan.Title, plan.Kind, plan.GroupKey, lineIds, handover.Priority, handover.ResponsibleUserId, handover.ResponsibleTeam,
                handover.TargetStartDate, handover.TargetEndDate, handover.Location, null, "Handover", dependsOn, JobEventKinds.Generated, $"Devir kabulünden üretildi ({settings.JobStrategy}).", permission, cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<IReadOnlyList<string>>.Fail(result.Error!.Code, "Operasyon işi üretilemedi: " + result.Error.Message, result.Error.FieldErrors);
            }

            created[plan.GroupKey] = result.Value!.Id;
            numbers.Add(result.Value.Values["number"]?.GetValue<string>() ?? string.Empty);
        }

        return RecordResult<IReadOnlyList<string>>.Ok(numbers);
    }

    // ---- Elle iş açma

    public async Task<RecordResult<OperationJobView>> CreateManualAsync(Guid orderId, ManualJobInput input, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(OrdersModule.Entities.Order, orderId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<OperationJobView>(loaded.Error!);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.JobsManage))
        {
            return RecordResult<OperationJobView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{OrdersModule.Permissions.JobsManage}' izni gerekir.");
        }

        var order = (await reader.OrderAsync(orderId, cancellationToken))!;
        if (order.Status != OrderStatus.Approved)
        {
            return Invalid<OperationJobView>("orderId", "Operasyon işi yalnızca onaylanmış siparişe açılır.");
        }

        var handover = await HandoverOfAsync(orderId, cancellationToken);
        if (handover is not { Status: HandoverStatus.Accepted })
        {
            return Invalid<OperationJobView>("orderId", "Operasyon işi açmadan önce satış devri operasyon tarafından kabul edilmiş olmalıdır.");
        }

        var all = await reader.LinesAsync(orderId, cancellationToken);
        var active = all.Where(l => !l.IsCancelled && l.EffectiveQuantity > 0m).ToDictionary(l => l.Id);
        var ids = input.LineIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
        {
            return Invalid<OperationJobView>("lineIds", "İşe bağlanacak en az bir sipariş kalemi seçin.");
        }

        if (ids.Any(id => !active.ContainsKey(id)))
        {
            return Invalid<OperationJobView>("lineIds", "Seçilen kalemlerden biri bu siparişte yok ya da iptal edilmiş.");
        }

        var inOpen = await reader.LinesInOpenJobsAsync(orderId, cancellationToken);
        if (ids.Where(inOpen.Contains).Select(id => active[id].Name).ToList() is { Count: > 0 } taken)
        {
            return RecordResult<OperationJobView>.Fail(RecordErrorCode.Conflict, "Seçilen kalemler zaten açık bir işte: " + string.Join(", ", taken) + ".", [new RecordFieldError("lineIds", "Kalem başka bir açık işte.")]);
        }

        var kinds = ids.Select(id => active[id].Kind == LineKinds.Product).Distinct().ToList();
        var kind = kinds.Count > 1 ? JobKind.Mixed : kinds[0] ? JobKind.Delivery : JobKind.Service;
        var title = string.IsNullOrWhiteSpace(input.Title) ? $"{(ids.Count == 1 ? active[ids[0]].Name : "Sipariş işi")} — {order.Number}" : input.Title.Trim();
        var priority = string.IsNullOrWhiteSpace(input.Priority) ? HandoverPriority.Normal : input.Priority;
        var created = await pipeline.RunAtomicAsync(async ct =>
        {
            var job = await CreateJobAsync(
                order, handover.Id, title, kind, null, ids, priority, input.ResponsibleUserId, Normalize(input.TeamName), input.PlannedStartDate, input.PlannedEndDate,
                Normalize(input.Location), Normalize(input.Notes), "Manual", null, JobEventKinds.CreatedManually, "Elle açıldı.", OrdersModule.Permissions.JobsManage, ct);
            return job.IsSuccess ? RecordResult<OperationJobView>.Ok(await ViewAsync(job.Value!, ct)) : Fail<OperationJobView>(job.Error!);
        }, cancellationToken);
        return created;
    }

    // ---- Eylemler

    public async Task<RecordResult<OperationJobView>> RunAsync(Guid jobId, string action, long version, JobActionInput input, CancellationToken cancellationToken)
    {
        if (!PermissionOf.TryGetValue(action, out var permission))
        {
            return Invalid<OperationJobView>("action", "Bilinmeyen eylem.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(Key, jobId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return Fail<OperationJobView>(loaded.Error!);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return RecordResult<OperationJobView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir.");
        }

        var job = (await reader.JobAsync(jobId, cancellationToken))!;
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (JobTransitions.RequiresReason(action) && reason is null)
        {
            return Invalid<OperationJobView>("reason", "Gerekçe zorunludur.");
        }

        var state = await StateOfAsync(job, cancellationToken);
        var rule = JobTransitions.Can(action, state);
        if (!rule.Allowed)
        {
            return RecordResult<OperationJobView>.Fail(RecordErrorCode.Conflict, rule.Message ?? "Bu işlem şu an yapılamaz.", [new RecordFieldError(rule.Code ?? "status", rule.Message ?? string.Empty)]);
        }

        var now = time.GetUtcNow();
        var values = new JsonObject();
        string kind;
        string? note = null;
        string toStatus;
        switch (action)
        {
            case JobAction.Assign:
                var team = input.TeamName is null ? job.TeamName : Normalize(input.TeamName);
                var user = input.ResponsibleUserId ?? job.ResponsibleUserId;
                if (input.ResponsibleUserId is null && input.TeamName is null)
                {
                    return Invalid<OperationJobView>("responsibleUserId", "Sorumlu kullanıcı ya da ekip girin.");
                }

                if (user is null && team is null)
                {
                    return Invalid<OperationJobView>("responsibleUserId", "Sorumlu kullanıcı ya da ekipten en az biri kalmalıdır.");
                }

                if (input.ResponsibleUserId is { } assignee)
                {
                    values["responsibleUserId"] = assignee.ToString();
                }

                if (input.TeamName is not null)
                {
                    values["teamName"] = team;
                }

                toStatus = JobTransitions.StatusAfterAssign(job.Status, true);
                values["status"] = toStatus;
                kind = JobEventKinds.Assigned;
                note = reason;
                break;
            case JobAction.Start:
                toStatus = JobStatus.InProgress;
                values["status"] = toStatus;
                values["actualStartAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                kind = JobEventKinds.Started;
                break;
            case JobAction.Complete:
                toStatus = JobStatus.Completed;
                values["status"] = toStatus;
                values["actualEndAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                kind = JobEventKinds.Completed;
                break;
            default:
                toStatus = JobStatus.Cancelled;
                values["status"] = toStatus;
                values["cancelReason"] = reason;
                kind = JobEventKinds.Cancelled;
                note = reason;
                break;
        }

        // Geçiş, geçmiş satırı ile aynı kaydetmede yazılır; sürüm çakışmasında hiçbiri yazılmaz.
        using (gate.Open([permission], kind, note, jobEffect: (ctx, j, token) =>
               {
                   JobHistory.Add(ctx, j, kind, job.Status, toStatus, note);
                   return Task.CompletedTask;
               }))
        {
            var updated = await pipeline.UpdateAsync(new RecordCommand(Key, jobId, values, version) { Reason = $"job.{kind}" }, cancellationToken);
            return updated.IsSuccess
                ? RecordResult<OperationJobView>.Ok(await ViewAsync(updated.Value!, cancellationToken))
                : Fail<OperationJobView>(updated.Error!);
        }
    }

    public async Task<RecordResult<OperationJobView>> AddDependencyAsync(Guid jobId, Guid dependsOnJobId, CancellationToken cancellationToken)
    {
        var prepared = await PrepareDependencyAsync(jobId, dependsOnJobId, adding: true, cancellationToken);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var (job, other) = prepared.Value!;
        return await pipeline.RunAtomicAsync(async ct =>
        {
            storeContext.Data.Set<OperationJobDependency>().Add(new OperationJobDependency { JobId = job.Id, DependsOnJobId = other.Id, OrderId = job.OrderId });
            using var _ = gate.Open([OrdersModule.Permissions.JobsManage], JobEventKinds.DependencyAdded, other.Number, jobEffect: (ctx, j, token) =>
            {
                j.Touch();
                JobHistory.Add(ctx, j, JobEventKinds.DependencyAdded, j.Status, j.Status, $"{other.Number} tamamlanmadan başlatılamaz.");
                return Task.CompletedTask;
            });
            var touched = await pipeline.UpdateAsync(new RecordCommand(Key, job.Id, new JsonObject(), job.Version) { Reason = "job.DependencyAdded" }, ct);
            return touched.IsSuccess ? RecordResult<OperationJobView>.Ok(await ViewAsync(touched.Value!, ct)) : Fail<OperationJobView>(touched.Error!);
        }, cancellationToken);
    }

    public async Task<RecordResult<OperationJobView>> RemoveDependencyAsync(Guid jobId, Guid dependsOnJobId, CancellationToken cancellationToken)
    {
        var prepared = await PrepareDependencyAsync(jobId, dependsOnJobId, adding: false, cancellationToken);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var (job, other) = prepared.Value!;
        return await pipeline.RunAtomicAsync(async ct =>
        {
            var edge = await storeContext.Data.Set<OperationJobDependency>().SingleAsync(d => d.JobId == job.Id && d.DependsOnJobId == other.Id, ct);
            storeContext.Data.Set<OperationJobDependency>().Remove(edge);
            using var _ = gate.Open([OrdersModule.Permissions.JobsManage], JobEventKinds.DependencyRemoved, other.Number, jobEffect: (ctx, j, token) =>
            {
                j.Touch();
                JobHistory.Add(ctx, j, JobEventKinds.DependencyRemoved, j.Status, j.Status, $"{other.Number} bağımlılığı kaldırıldı.");
                return Task.CompletedTask;
            });
            var touched = await pipeline.UpdateAsync(new RecordCommand(Key, job.Id, new JsonObject(), job.Version) { Reason = "job.DependencyRemoved" }, ct);
            return touched.IsSuccess ? RecordResult<OperationJobView>.Ok(await ViewAsync(touched.Value!, ct)) : Fail<OperationJobView>(touched.Error!);
        }, cancellationToken);
    }

    private sealed record DependencyPair(OperationJob Job, OperationJob Other);

    private sealed record PreparedDependency(DependencyPair? Value, RecordResult<OperationJobView>? Error);

    private async Task<PreparedDependency> PrepareDependencyAsync(Guid jobId, Guid dependsOnJobId, bool adding, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(Key, jobId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return new(null, Fail<OperationJobView>(loaded.Error!));
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(OrdersModule.Permissions.JobsManage))
        {
            return new(null, RecordResult<OperationJobView>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{OrdersModule.Permissions.JobsManage}' izni gerekir."));
        }

        var job = (await reader.JobAsync(jobId, cancellationToken))!;
        var otherRecord = await pipeline.GetAsync(new RecordRef(Key, dependsOnJobId), cancellationToken);
        var other = otherRecord.IsSuccess ? await reader.JobAsync(dependsOnJobId, cancellationToken) : null;
        if (other is null || other.OrderId != job.OrderId)
        {
            return new(null, Invalid<OperationJobView>("dependsOnJobId", "Bağımlı olunacak iş bu siparişte bulunamadı."));
        }

        if (JobStatus.IsFinal(job.Status))
        {
            return new(null, Invalid<OperationJobView>("jobId", "Tamamlanmış ya da iptal edilmiş işin bağımlılığı değiştirilemez."));
        }

        var edges = await reader.JobEdgesAsync(job.OrderId, cancellationToken);
        var exists = edges.Any(e => e.JobId == job.Id && e.DependsOnJobId == other.Id);
        if (adding)
        {
            if (exists)
            {
                return new(null, RecordResult<OperationJobView>.Fail(RecordErrorCode.Conflict, "Bu bağımlılık zaten var."));
            }

            if (JobDependencyRules.WouldCycle(edges.Select(e => (e.JobId, e.DependsOnJobId)), job.Id, other.Id))
            {
                return new(null, Invalid<OperationJobView>("dependsOnJobId", job.Id == other.Id ? "İş kendisine bağımlı olamaz." : "Bu bağımlılık döngü oluşturur."));
            }
        }
        else if (!exists)
        {
            return new(null, RecordResult<OperationJobView>.Fail(RecordErrorCode.NotFound, "Bu bağımlılık yok."));
        }

        return new(new DependencyPair(job, other), null);
    }

    // ---- Üretim ve görünüm yardımcıları

    private async Task<RecordResult<RecordData>> CreateJobAsync(
        SalesOrder order, Guid handoverId, string title, string kind, string? groupKey, IReadOnlyList<Guid> lineIds, string priority, Guid? responsibleUserId,
        string? teamName, DateOnly? plannedStart, DateOnly? plannedEnd, string? location, string? notes, string source, Guid? dependsOnJobId, string eventKind, string eventNote,
        string permission, CancellationToken cancellationToken)
    {
        var assigned = responsibleUserId is not null || !string.IsNullOrWhiteSpace(teamName);
        var status = JobTransitions.InitialStatus(assigned);
        var values = new JsonObject
        {
            ["orderId"] = order.Id.ToString(),
            ["handoverId"] = handoverId.ToString(),
            ["title"] = title.Length > OperationJob.TitleMaxLength ? title[..OperationJob.TitleMaxLength] : title,
            ["kind"] = kind,
            ["groupKey"] = groupKey,
            ["status"] = status,
            ["priority"] = HandoverPriority.All.Contains(priority) ? priority : HandoverPriority.Normal,
            ["responsibleUserId"] = responsibleUserId?.ToString(),
            ["teamName"] = teamName,
            ["plannedStartDate"] = plannedStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["plannedEndDate"] = plannedEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["location"] = location,
            ["notes"] = notes,
            ["source"] = source,
        };
        var templates = OperationTemplates.For(kind);
        using var _ = gate.Open([permission], eventKind, eventNote, jobEffect: (ctx, job, token) =>
        {
            foreach (var lineId in lineIds)
            {
                ctx.Data.Set<OperationJobLine>().Add(new OperationJobLine { JobId = job.Id, OrderId = order.Id, OrderLineId = lineId });
            }

            var position = 0;
            foreach (var text in templates)
            {
                ctx.Data.Set<OperationJobTask>().Add(new OperationJobTask { JobId = job.Id, Position = ++position, Title = text, IsRequired = true, IsDefault = true });
            }

            if (dependsOnJobId is { } dependency)
            {
                ctx.Data.Set<OperationJobDependency>().Add(new OperationJobDependency { JobId = job.Id, DependsOnJobId = dependency, OrderId = order.Id });
            }

            JobHistory.Add(ctx, job, eventKind, null, job.Status, eventNote);
            return Task.CompletedTask;
        });
        return await pipeline.CreateAsync(new RecordCommand(Key, null, values) { Reason = $"job.{eventKind}" }, cancellationToken);
    }

    private async Task<JobState> StateOfAsync(OperationJob job, CancellationToken cancellationToken)
    {
        var order = (await reader.OrderAsync(job.OrderId, cancellationToken))!;
        var tasks = await reader.JobTasksAsync(job.Id, cancellationToken);
        var edges = (await reader.JobEdgesAsync(job.OrderId, cancellationToken)).Where(e => e.JobId == job.Id).ToList();
        var open = 0;
        foreach (var edge in edges)
        {
            if (await reader.JobAsync(edge.DependsOnJobId, cancellationToken) is { Status: not JobStatus.Completed })
            {
                open++;
            }
        }

        return new JobState(job.Status, job.ResponsibleUserId is not null || !string.IsNullOrWhiteSpace(job.TeamName), open, tasks.Count(t => t.IsRequired && !t.IsDone), order.Status);
    }

    private async Task<OrderHandover?> HandoverOfAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var query = await pipeline.QueryAsync(
            new RecordQuery(OrdersModule.Entities.Handover, 1, 1, null, new Dictionary<string, string> { ["orderId"] = orderId.ToString() }), cancellationToken);
        return query.IsSuccess && query.Value!.Items.Count > 0 ? await reader.HandoverAsync(query.Value.Items[0].Id, cancellationToken) : null;
    }

    private async Task<OperationJobView> ViewAsync(RecordData record, CancellationToken cancellationToken)
    {
        var job = (await reader.JobAsync(record.Id, cancellationToken))!;
        var order = (await reader.OrderAsync(job.OrderId, cancellationToken))!;
        var orderLines = (await reader.LinesAsync(job.OrderId, cancellationToken)).ToDictionary(l => l.Id);
        var links = await reader.JobLinksAsync(job.Id, cancellationToken);
        var tasks = await reader.JobTasksAsync(job.Id, cancellationToken);
        var edges = await reader.JobEdgesAsync(job.OrderId, cancellationToken);
        var jobs = (await reader.JobsOfOrderAsync(job.OrderId, cancellationToken)).ToDictionary(j => j.Id);
        JobLinkView Link(Guid id) => new(id, jobs[id].Number, jobs[id].Title, jobs[id].Status);
        var state = await StateOfAsync(job, cancellationToken);
        var granted = await access.GetPermissionsAsync(cancellationToken);
        var actions = new List<JobActionState>();
        foreach (var action in JobAction.All.Where(a => granted.Contains(PermissionOf[a])))
        {
            var rule = JobTransitions.Can(action, state);
            actions.Add(new JobActionState(action, rule.Allowed, rule.Code, rule.Message, JobTransitions.RequiresReason(action)));
        }

        var events = await reader.JobEventsAsync(job.Id, cancellationToken);
        var done = tasks.Count(t => t.IsDone);
        return new OperationJobView(
            record,
            order.Number,
            [.. links.Where(l => orderLines.ContainsKey(l.OrderLineId)).Select(l => new JobLineView(l.OrderLineId, orderLines[l.OrderLineId].Name, orderLines[l.OrderLineId].Kind, orderLines[l.OrderLineId].EffectiveQuantity, orderLines[l.OrderLineId].UnitName))],
            [.. tasks.Select(t => new JobTaskView(t.Id, t.Version, t.Position, t.Title, t.IsRequired, t.IsDone, t.DoneBy, t.DoneAt, t.Note, t.IsDefault))],
            [.. edges.Where(e => e.JobId == job.Id && jobs.ContainsKey(e.DependsOnJobId)).Select(e => Link(e.DependsOnJobId))],
            [.. edges.Where(e => e.DependsOnJobId == job.Id && jobs.ContainsKey(e.JobId)).Select(e => Link(e.JobId))],
            actions,
            [.. events.Select(e => new JobEventView(e.Id, e.Kind, e.CreatedAt, e.ActorId, e.FromStatus, e.ToStatus, e.Note))],
            tasks.Count == 0 ? (job.Status == JobStatus.Completed ? 100 : 0) : (int)Math.Round(100.0 * done / tasks.Count));
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RecordResult<T> Fail<T>(RecordError error) => RecordResult<T>.Fail(error.Code, error.Message, error.FieldErrors);

    private static RecordResult<T> Invalid<T>(string field, string message) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);
}
