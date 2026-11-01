using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;
using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Records;

/// <summary>İş geçmişi satırı yazımı; iş güncellemesiyle aynı kaydetmede yazılır.</summary>
internal static class JobHistory
{
    public static void Add(OrdersStoreContext context, OperationJob job, string kind, string? from = null, string? to = null, string? note = null) =>
        context.Data.Set<OperationJobEvent>().Add(new OperationJobEvent
        {
            JobId = job.Id,
            OrderId = job.OrderId,
            Kind = kind,
            ActorId = context.CurrentUserId,
            FromStatus = from,
            ToStatus = to,
            Note = note is { Length: > SalesOrder.ReasonMaxLength } ? note[..SalesOrder.ReasonMaxLength] : note,
        });

    /// <summary>Yeni iş numarası: OP-yyyyMMdd-XXXXXX (karışmayan harf/rakam alfabesi).</summary>
    public static string NewNumber(DateTimeOffset createdAt)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"OP-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }
}

/// <summary>
/// Operasyon işi kayıt deposu (ADR-0023, dilim 4). İşler yalnızca iş akışı servisiyle (devir kabulünden üretim ya da elle açma) oluşur; durum, atama, gerçekleşen
/// tarihler ve uyarı işaretleri sistem alanıdır (genel uçtan yazılamaz). Başlık, öncelik, planlanan tarihler, lokasyon ve not açık işte düzenlenir. Görünürlük ve veri
/// kapsamı üst siparişi izler.
/// </summary>
internal sealed class OperationJobRecordStore(OrdersStoreContext context) : OrdersRecordStore<OperationJob>(context)
{
    public override string EntityKey => OrdersModule.Entities.Job;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.JobsManage;

    protected override bool UsesScope => true;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OperationJob>> Fields { get; } =
    [
        Field.Reference<OperationJob, SalesOrder>("orderId", "Sipariş bulunamadı.", j => j.OrderId, (j, v) => j.OrderId = v!.Value, required: true, createOnly: true),
        Field.Id<OperationJob>("handoverId", j => j.HandoverId, (j, v) => j.HandoverId = v).AsSystem(),
        Field.Text<OperationJob>("number", OperationJob.NumberMaxLength, j => j.Number, (j, v) => j.Number = v!).AsSystem(),
        Field.Text<OperationJob>("title", OperationJob.TitleMaxLength, j => j.Title, (j, v) => j.Title = v!, required: true),
        Field.Choice<OperationJob>("kind", JobKind.All, j => j.Kind, (j, v) => j.Kind = v).AsSystem(),
        Field.Text<OperationJob>("groupKey", OperationJob.KeyMaxLength, j => j.GroupKey, (j, v) => j.GroupKey = v).AsSystem(),
        Field.Text<OperationJob>("status", SalesOrder.StatusMaxLength, j => j.Status, (j, v) => j.Status = v!).AsSystem(),
        Field.Choice<OperationJob>("priority", HandoverPriority.All, j => j.Priority, (j, v) => j.Priority = v),
        Field.Owner<OperationJob>("responsibleUserId", j => j.ResponsibleUserId, (j, v) => j.ResponsibleUserId = v).AsSystem(),
        Field.Text<OperationJob>("teamName", OrderHandover.ShortMaxLength, j => j.TeamName, (j, v) => j.TeamName = v).AsSystem(),
        Field.Date<OperationJob>("plannedStartDate", j => j.PlannedStartDate, (j, v) => j.PlannedStartDate = v),
        Field.Date<OperationJob>("plannedEndDate", j => j.PlannedEndDate, (j, v) => j.PlannedEndDate = v),
        Field.Time<OperationJob>("actualStartAt", j => j.ActualStartAt, (j, v) => j.ActualStartAt = v).AsSystem(),
        Field.Time<OperationJob>("actualEndAt", j => j.ActualEndAt, (j, v) => j.ActualEndAt = v).AsSystem(),
        Field.Text<OperationJob>("location", SalesOrder.AddressMaxLength, j => j.Location, (j, v) => j.Location = v),
        Field.Text<OperationJob>("notes", OperationJob.TextMaxLength, j => j.Notes, (j, v) => j.Notes = v),
        Field.Text<OperationJob>("cancelReason", SalesOrder.ReasonMaxLength, j => j.CancelReason, (j, v) => j.CancelReason = v).AsSystem(),
        Field.Text<OperationJob>("source", SalesOrder.StatusMaxLength, j => j.Source, (j, v) => j.Source = v!).AsSystem(),
        Field.Time<OperationJob>("unassignedAlertAt", j => j.UnassignedAlertAt, (j, v) => j.UnassignedAlertAt = v).AsSystem(),
        Field.Time<OperationJob>("overdueAlertAt", j => j.OverdueAlertAt, (j, v) => j.OverdueAlertAt = v).AsSystem(),
        Field.Id<OperationJob>("createdBy", j => j.CreatedBy, (j, v) => j.CreatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OperationJob>> Filters { get; } = new Dictionary<string, RecordFilter<OperationJob>>
    {
        ["orderId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(j => j.OrderId == id) : null,
        ["status"] = (q, v, _) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } statuses && statuses.All(JobStatus.All.Contains)
            ? q.Where(j => statuses.Contains(j.Status))
            : null,
        ["kind"] = (q, v, _) => JobKind.All.Contains(v) ? q.Where(j => j.Kind == v) : null,
        ["responsibleUserId"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } user ? q.Where(j => j.ResponsibleUserId == user) : null,
        // Atanmamış işler: sorumlu kullanıcı da ekip de yok ("Atama bekliyor"); iş kaybolmaz, kuyrukta görünür.
        ["unassigned"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(j => (j.Status == JobStatus.AwaitingAssignment) == flag) : null,
        // Gecikenler: açık iş ve planlanan bitiş geçmiş.
        ["overdue"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(j => (j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled && j.PlannedEndDate != null && j.PlannedEndDate < DateOnly.FromDateTime(DateTime.UtcNow)) == flag)
            : null,
        ["plannedFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(j => j.PlannedEndDate >= d) : null,
        ["plannedTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(j => j.PlannedStartDate <= d) : null,
    };

    protected override IOrderedQueryable<OperationJob> Order(IQueryable<OperationJob> source) => source.OrderByDescending(j => j.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys => JobSort.All;

    protected override IOrderedQueryable<OperationJob> Order(IQueryable<OperationJob> source, string? sort) => sort switch
    {
        "plannedEnd" => source.OrderBy(j => j.PlannedEndDate == null).ThenBy(j => j.PlannedEndDate),
        "-plannedEnd" => source.OrderBy(j => j.PlannedEndDate == null).ThenByDescending(j => j.PlannedEndDate),
        "number" => source.OrderBy(j => j.Number),
        "-number" => source.OrderByDescending(j => j.Number),
        "title" => source.OrderBy(j => j.Title),
        "createdAt" => source.OrderBy(j => j.CreatedAt),
        _ => Order(source),
    };

    protected override IQueryable<OperationJob> Search(IQueryable<OperationJob> source, string term) =>
        source.Where(j => j.Title.Contains(term) || j.Number.Contains(term) || (j.TeamName != null && j.TeamName.Contains(term)));

    protected override IQueryable<OperationJob> ScopeToOwners(IQueryable<OperationJob> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(j => orders.Any(o => o.Id == j.OrderId && ids.Contains(o.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("orderId", out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var orderId))
        {
            return false;
        }

        var ids = owners.Select(id => (Guid?)id).ToList();
        return await Context.Data.Set<SalesOrder>().AsNoTracking().AnyAsync(o => o.Id == orderId && ids.Contains(o.OwnerId), cancellationToken);
    }

    protected override Task<JsonObject> PrepareNewAsync(OperationJob entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("orderId", "Operasyon işi yalnızca devir kabulünden ya da sipariş ekranındaki iş açma işleminden oluşur.")]);
        }

        entity.Number = JobHistory.NewNumber(DateTimeOffset.UtcNow);
        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task CreatedAsync(OperationJob entity, CancellationToken cancellationToken)
    {
        if (Context.Gate.JobEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override async Task AppliedAsync(OperationJob entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (!isNew && !Context.Gate.IsOpen && JobStatus.IsFinal(entity.Status))
        {
            errors.Add(new RecordFieldError("status", "Tamamlanmış ya da iptal edilmiş iş değiştirilemez."));
        }

        if (entity.PlannedStartDate is { } start && entity.PlannedEndDate is { } end && end < start)
        {
            errors.Add(new RecordFieldError("plannedEndDate", "Planlanan bitiş başlangıçtan önce olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (!isNew && Context.Gate.IsOpen && Context.Gate.JobEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override Task BeforeDeleteAsync(OperationJob entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Operasyon işi silinemez; geçmişi korunur. Vazgeçildiyse işi iptal edin.");
}

/// <summary>Liste sıralama anahtarları.</summary>
public static class JobSort
{
    public static readonly IReadOnlyList<string> All = ["plannedEnd", "-plannedEnd", "number", "-number", "title", "createdAt"];
}

/// <summary>
/// İş görevleri. Şablondan gelen görevler yalnızca tamamlandı olarak işaretlenir (başlığı değişmez, silinmez); kullanıcı kendi görevini ekler, düzenler ve
/// siler. Tamamlanmış ya da iptal edilmiş işin görevleri değişmez. Görünürlük ve veri kapsamı üst işi (siparişi) izler.
/// </summary>
internal sealed class OperationJobTaskRecordStore(OrdersStoreContext context) : OrdersRecordStore<OperationJobTask>(context)
{
    public override string EntityKey => OrdersModule.Entities.JobTask;

    protected override string ReadPermission => OrdersModule.Permissions.OrdersRead;

    protected override string WritePermission => OrdersModule.Permissions.JobsManage;

    protected override bool UsesScope => true;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<OperationJobTask>> Fields { get; } =
    [
        Field.Reference<OperationJobTask, OperationJob>("jobId", "İş bulunamadı.", t => t.JobId, (t, v) => t.JobId = v!.Value, required: true, createOnly: true),
        Field.Integer<OperationJobTask>("position", t => t.Position, (t, v) => t.Position = v ?? 0, max: 9999).AsSystem(),
        Field.Text<OperationJobTask>("title", OperationJobTask.TitleMaxLength, t => t.Title, (t, v) => t.Title = v!, required: true),
        Field.Flag<OperationJobTask>("isRequired", t => t.IsRequired, (t, v) => t.IsRequired = v).AsSystem(),
        Field.Flag<OperationJobTask>("isDone", t => t.IsDone, (t, v) => t.IsDone = v),
        Field.Id<OperationJobTask>("doneBy", t => t.DoneBy, (t, v) => t.DoneBy = v).AsSystem(),
        Field.Time<OperationJobTask>("doneAt", t => t.DoneAt, (t, v) => t.DoneAt = v).AsSystem(),
        Field.Text<OperationJobTask>("note", SalesOrder.ReasonMaxLength, t => t.Note, (t, v) => t.Note = v),
        Field.Flag<OperationJobTask>("isDefault", t => t.IsDefault, (t, v) => t.IsDefault = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OperationJobTask>> Filters { get; } = new Dictionary<string, RecordFilter<OperationJobTask>>
    {
        ["jobId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.JobId == id) : null,
    };

    protected override IOrderedQueryable<OperationJobTask> Order(IQueryable<OperationJobTask> source) => source.OrderBy(t => t.Position).ThenBy(t => t.CreatedAt);

    protected override IQueryable<OperationJobTask> Search(IQueryable<OperationJobTask> source, string term) => source.Where(t => t.Title.Contains(term));

    protected override IQueryable<OperationJobTask> ScopeToOwners(IQueryable<OperationJobTask> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var jobs = Context.Data.Set<OperationJob>();
        var orders = Context.Data.Set<SalesOrder>();
        return source.Where(t => jobs.Any(j => j.Id == t.JobId && orders.Any(o => o.Id == j.OrderId && ids.Contains(o.OwnerId))));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue("jobId", out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var jobId))
        {
            return false;
        }

        var ids = owners.Select(id => (Guid?)id).ToList();
        return await Context.Data.Set<OperationJob>().AsNoTracking().AnyAsync(
            j => j.Id == jobId && Context.Data.Set<SalesOrder>().Any(o => o.Id == j.OrderId && ids.Contains(o.OwnerId)), cancellationToken);
    }

    private string? titleBefore;

    protected override void OnUpdating(OperationJobTask entity) => titleBefore = entity.Title;

    protected override async Task<JsonObject> PrepareNewAsync(OperationJobTask entity, JsonObject values, CancellationToken cancellationToken)
    {
        // Kullanıcının eklediği görev zorunludur (eklenen iş yapılmadan iş tamamlanmaz); sıra sona eklenir.
        entity.IsRequired = true;
        if (values.TryGetPropertyValue("jobId", out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var jobId))
        {
            entity.Position = await Context.Data.Set<OperationJobTask>().CountAsync(t => t.JobId == jobId, cancellationToken) + 1;
        }

        return values;
    }

    protected override async Task AppliedAsync(OperationJobTask entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var job = await Context.Data.Set<OperationJob>().AsNoTracking().SingleOrDefaultAsync(j => j.Id == entity.JobId, cancellationToken)
            ?? throw new RecordValidationException([new RecordFieldError("jobId", "İş bulunamadı.")]);
        if (!Context.Gate.IsOpen && JobStatus.IsFinal(job.Status))
        {
            throw new RecordValidationException([new RecordFieldError("jobId", "Tamamlanmış ya da iptal edilmiş işin görevleri değiştirilemez.")]);
        }

        if (!isNew && entity.IsDefault && entity.Title != titleBefore)
        {
            throw new RecordValidationException([new RecordFieldError("title", "Şablondan gelen görevin başlığı değiştirilemez.")]);
        }

        if (values.ContainsKey("isDone"))
        {
            entity.DoneBy = entity.IsDone ? Context.CurrentUserId : null;
            entity.DoneAt = entity.IsDone ? DateTimeOffset.UtcNow : null;
        }
    }

    protected override async Task BeforeDeleteAsync(OperationJobTask entity, CancellationToken cancellationToken)
    {
        if (entity.IsDefault)
        {
            throw new RecordConflictException("Şablondan gelen görev silinemez; yalnızca tamamlandı olarak işaretlenir.");
        }

        var status = await Context.Data.Set<OperationJob>().AsNoTracking().Where(j => j.Id == entity.JobId).Select(j => j.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null || JobStatus.IsFinal(status))
        {
            throw new RecordValidationException([new RecordFieldError("jobId", "Tamamlanmış ya da iptal edilmiş işin görevleri değiştirilemez.")]);
        }
    }
}
