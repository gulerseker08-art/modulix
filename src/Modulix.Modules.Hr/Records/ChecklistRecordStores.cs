using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// İşe giriş/çıkış şablonları (ADR-0042 dilim 5). Ad tür içinde tektir; tür yalnızca oluştururken seçilir. Kullanılmış şablon silinmez (pasife alınır);
/// kullanılmamış şablon adımlarıyla birlikte silinir. Şablon değişikliği başlatılmış süreçleri etkilemez.
/// </summary>
internal sealed class ChecklistTemplateRecordStore(HrStoreContext context) : HrRecordStore<ChecklistTemplate>(context), IRecordLabelProvider
{
    public override string EntityKey => HrModule.Entities.ChecklistTemplate;

    protected override string ReadPermission => HrModule.Permissions.ChecklistsRead;

    protected override string WritePermission => HrModule.Permissions.ChecklistsManage;

    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        return await Context.Data.Set<ChecklistTemplate>().AsNoTracking().Where(t => wanted.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    protected override IReadOnlyList<RecordField<ChecklistTemplate>> Fields { get; } =
    [
        Field.Text<ChecklistTemplate>("name", ChecklistTemplate.NameMaxLength, t => t.Name, (t, v) => t.Name = v!, required: true),
        Field.Choice<ChecklistTemplate>("kind", Checklists.Kinds.All, t => t.Kind, (t, v) => t.Kind = v, createOnly: true),
        Field.Text<ChecklistTemplate>("description", ChecklistTemplate.TextMaxLength, t => t.Description, (t, v) => t.Description = v),
        Field.Flag<ChecklistTemplate>("isActive", t => t.IsActive, (t, v) => t.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ChecklistTemplate>> Filters { get; } = new Dictionary<string, RecordFilter<ChecklistTemplate>>
    {
        ["kind"] = (q, v) => Checklists.Kinds.All.Contains(v) ? q.Where(t => t.Kind == v) : null,
        ["isActive"] = (q, v) => bool.TryParse(v, out var active) ? q.Where(t => t.IsActive == active) : null,
    };

    protected override IOrderedQueryable<ChecklistTemplate> Order(IQueryable<ChecklistTemplate> source) => source.OrderBy(t => t.Kind).ThenBy(t => t.Name);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("name", "kind", "createdAt");

    protected override IOrderedQueryable<ChecklistTemplate> Order(IQueryable<ChecklistTemplate> source, string? sort) => sort?.TrimStart('-') switch
    {
        "name" => By(source, t => t.Name, sort.StartsWith('-')),
        "kind" => By(source, t => t.Kind, sort.StartsWith('-')).ThenBy(t => t.Name),
        "createdAt" => By(source, t => t.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<ChecklistTemplate> Search(IQueryable<ChecklistTemplate> source, string term) => source.Where(t => t.Name.Contains(term));

    protected override async Task AppliedAsync(ChecklistTemplate entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<ChecklistTemplate>().AsNoTracking().AnyAsync(t => t.Kind == entity.Kind && t.Name == entity.Name && t.Id != entity.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("name", "Bu adla aynı türde başka bir şablon var.")]);
        }
    }

    protected override async Task BeforeDeleteAsync(ChecklistTemplate entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<EmployeeChecklist>().AsNoTracking().AnyAsync(c => c.TemplateId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Bu şablonla süreç başlatılmış; şablon silinmez, pasife alın.");
        }

        Context.Data.Set<ChecklistTemplateItem>().RemoveRange(
            await Context.Data.Set<ChecklistTemplateItem>().Where(i => i.TemplateId == entity.Id).ToListAsync(cancellationToken));
    }
}

/// <summary>
/// Şablon adımları (ADR-0042 dilim 5). Sıra verilmezse son adımın 10 fazlasıdır. "Zimmet iadesi" görevi yalnızca çıkış şablonunda olur. Gün farkı
/// sürecin başlangıç tarihine eklenir (çıkışta eksi olabilir).
/// </summary>
internal sealed class ChecklistTemplateItemRecordStore(HrStoreContext context) : HrRecordStore<ChecklistTemplateItem>(context)
{
    public override string EntityKey => HrModule.Entities.ChecklistTemplateItem;

    protected override string ReadPermission => HrModule.Permissions.ChecklistsRead;

    protected override string WritePermission => HrModule.Permissions.ChecklistsManage;

    protected override IReadOnlyList<RecordField<ChecklistTemplateItem>> Fields { get; } =
    [
        Field.Reference<ChecklistTemplateItem, ChecklistTemplate>("templateId", "Şablon bulunamadı.", i => i.TemplateId, (i, v) => i.TemplateId = v!.Value, required: true, createOnly: true),
        Field.Integer<ChecklistTemplateItem>("sequence", i => i.Sequence, (i, v) => i.Sequence = v ?? 0, 1, 10_000),
        Field.Text<ChecklistTemplateItem>("title", ChecklistTemplateItem.TitleMaxLength, i => i.Title, (i, v) => i.Title = v!, required: true),
        Field.Text<ChecklistTemplateItem>("description", ChecklistTemplateItem.TextMaxLength, i => i.Description, (i, v) => i.Description = v),
        Field.Choice<ChecklistTemplateItem>("responsibleRole", Checklists.Responsibles.All, i => i.ResponsibleRole, (i, v) => i.ResponsibleRole = v),
        Field.Integer<ChecklistTemplateItem>("dueOffsetDays", i => i.DueOffsetDays, (i, v) => i.DueOffsetDays = v ?? 0,
            -ChecklistTemplateItem.MaxOffsetDays, ChecklistTemplateItem.MaxOffsetDays),
        Field.Choice<ChecklistTemplateItem>("taskType", Checklists.TaskTypes.All, i => i.TaskType, (i, v) => i.TaskType = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ChecklistTemplateItem>> Filters { get; } = new Dictionary<string, RecordFilter<ChecklistTemplateItem>>
    {
        ["templateId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(i => i.TemplateId == id) : null,
    };

    protected override IOrderedQueryable<ChecklistTemplateItem> Order(IQueryable<ChecklistTemplateItem> source) => source.OrderBy(i => i.Sequence).ThenBy(i => i.CreatedAt);

    protected override IQueryable<ChecklistTemplateItem> Search(IQueryable<ChecklistTemplateItem> source, string term) => source.Where(i => i.Title.Contains(term));

    protected override async Task AppliedAsync(ChecklistTemplateItem entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var kind = await Context.Data.Set<ChecklistTemplate>().AsNoTracking().Where(t => t.Id == entity.TemplateId).Select(t => t.Kind).SingleAsync(cancellationToken);
        if (entity.TaskType == Checklists.TaskTypes.AssetReturn && kind != Checklists.Kinds.Offboarding)
        {
            throw new RecordValidationException([new RecordFieldError("taskType", "\"Zimmet iadesi\" görevi yalnızca işten çıkış şablonunda olur.")]);
        }

        if (entity.Sequence == 0)
        {
            var last = await Context.Data.Set<ChecklistTemplateItem>().AsNoTracking().Where(i => i.TemplateId == entity.TemplateId && i.Id != entity.Id)
                .MaxAsync(i => (int?)i.Sequence, cancellationToken);
            entity.Sequence = (last ?? 0) + 10;
        }
    }
}

/// <summary>
/// Çalışan süreçleri (ADR-0042 dilim 5). Yalnızca <see cref="ChecklistWorkflow"/> başlatır (genel uçtan açılmaz, değiştirilmez, silinmez). Başlatırken:
/// şablon etkin ve en az bir adımlı olmalı; işe giriş süreci ayrılmış çalışana başlatılmaz; çalışanın aynı türde açık süreci olamaz. Görevler
/// eklendikçe ve kapandıkça ilerleme ve durum görev deposunca aynı işlemde güncellenir. Görünürlük: rolün kapsamındaki çalışanlar ve kullanıcıya
/// görev atanmış süreçler.
/// </summary>
internal sealed class EmployeeChecklistRecordStore(HrStoreContext context) : HrRecordStore<EmployeeChecklist>(context)
{
    private readonly Dictionary<Guid, string> names = [];

    public override string EntityKey => HrModule.Entities.EmployeeChecklist;

    protected override string ReadPermission => HrModule.Permissions.ChecklistsRead;

    protected override string WritePermission => HrModule.Permissions.ChecklistsManage;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Süreç /api/hr/employee-checklists/start ile başlatılır.";

    protected override string? UpdateBlockedMessage => Context.Gate.IsOpen ? null : "Süreç değiştirilmez; görevleri tamamlayın ya da süreci iptal edin.";

    protected override bool IsInScope(RecordData record, IReadOnlySet<Guid> owners)
    {
        if (base.IsInScope(record, owners))
        {
            return true;
        }

        var me = Context.CurrentUserId ?? Guid.Empty;
        return Context.Data.Set<ChecklistTask>().AsNoTracking().Any(t => t.ChecklistId == record.Id && t.AssigneeUserId == me);
    }

    protected override IQueryable<EmployeeChecklist> ScopeToOwners(IQueryable<EmployeeChecklist> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        var me = Context.CurrentUserId ?? Guid.Empty;
        var assigned = Context.Data.Set<ChecklistTask>().AsNoTracking().Where(t => t.AssigneeUserId == me).Select(t => t.ChecklistId);
        return source.Where(c => (c.OwnerId != null && ids.Contains(c.OwnerId.Value)) || assigned.Contains(c.Id));
    }

    protected override IReadOnlyList<RecordField<EmployeeChecklist>> Fields => fields ??=
    [
        Field.Reference<EmployeeChecklist, Employee>("employeeId", "Çalışan bulunamadı.", c => c.EmployeeId, (c, v) => c.EmployeeId = v!.Value, required: true, createOnly: true),
        Field.Reference<EmployeeChecklist, ChecklistTemplate>("templateId", "Şablon bulunamadı.", c => c.TemplateId, (c, v) => c.TemplateId = v!.Value, required: true, createOnly: true),
        Field.Date<EmployeeChecklist>("startDate", c => c.StartDate, (c, v) => c.StartDate = v!.Value, required: true),
        Field.Computed<EmployeeChecklist>("kind", c => c.Kind),
        Field.Computed<EmployeeChecklist>("name", c => c.Name),
        Field.Computed<EmployeeChecklist>("status", c => c.Status),
        Field.Computed<EmployeeChecklist>("taskCount", c => JsonValue.Create(c.TaskCount)),
        Field.Computed<EmployeeChecklist>("closedCount", c => JsonValue.Create(c.ClosedCount)),
        Field.Computed<EmployeeChecklist>("employeeUserId", c => c.OwnerId?.ToString()),
        Field.Computed<EmployeeChecklist>("startedByUserId", c => c.StartedByUserId?.ToString()),
        Field.Computed<EmployeeChecklist>("completedAt", c => c.CompletedAt?.ToString("O")),
        Field.Computed<EmployeeChecklist>("cancelReason", c => c.CancelReason),
        Field.Computed<EmployeeChecklist>("employeeName", c => names.GetValueOrDefault(c.EmployeeId)),
    ];

    private IReadOnlyList<RecordField<EmployeeChecklist>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<EmployeeChecklist>> Filters { get; } = new Dictionary<string, RecordFilter<EmployeeChecklist>>
    {
        ["status"] = (q, v) => v.Split(',') is var s && s.All(EmployeeChecklist.Statuses.All.Contains) ? q.Where(c => s.Contains(c.Status)) : null,
        ["kind"] = (q, v) => Checklists.Kinds.All.Contains(v) ? q.Where(c => c.Kind == v) : null,
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.EmployeeId == id) : null,
        ["templateId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.TemplateId == id) : null,
    };

    protected override IOrderedQueryable<EmployeeChecklist> Order(IQueryable<EmployeeChecklist> source) => source.OrderByDescending(c => c.StartDate).ThenByDescending(c => c.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("startDate", "kind", "status", "createdAt");

    protected override IOrderedQueryable<EmployeeChecklist> Order(IQueryable<EmployeeChecklist> source, string? sort) => sort?.TrimStart('-') switch
    {
        "startDate" => By(source, c => c.StartDate, sort.StartsWith('-')).ThenBy(c => c.CreatedAt),
        "kind" => By(source, c => c.Kind, sort.StartsWith('-')).ThenByDescending(c => c.StartDate),
        "status" => By(source, c => c.Status, sort.StartsWith('-')).ThenByDescending(c => c.StartDate),
        "createdAt" => By(source, c => c.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<EmployeeChecklist> Search(IQueryable<EmployeeChecklist> source, string term)
    {
        var people = Context.Data.Set<Core.Contracts.SharedRecords.Person>().AsNoTracking()
            .Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)).Select(p => p.Id);
        var employees = Context.Data.Set<Employee>().AsNoTracking().Where(e => people.Contains(e.PersonId)).Select(e => e.Id);
        return source.Where(c => employees.Contains(c.EmployeeId) || c.Name.Contains(term));
    }

    protected override Task PrepareAsync(IReadOnlyCollection<EmployeeChecklist> entities, CancellationToken cancellationToken) =>
        EmployeeNames.LoadAsync(Context, entities.Select(c => c.EmployeeId), names, cancellationToken);

    protected override Task BeforeDeleteAsync(EmployeeChecklist entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Süreçler silinmez; süreci iptal edin.");

    protected override async Task AppliedAsync(EmployeeChecklist entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew)
        {
            // Yalnızca iptal komutu (kapı açık) buraya gelir; ilerleme görev deposundan güncellenir.
            if (Context.Gate.Status == EmployeeChecklist.Statuses.Cancelled)
            {
                entity.Status = EmployeeChecklist.Statuses.Cancelled;
                entity.CancelReason = Context.Gate.Reason;
            }

            return;
        }

        var errors = new List<RecordFieldError>();
        var template = await Context.Data.Set<ChecklistTemplate>().AsNoTracking().SingleAsync(t => t.Id == entity.TemplateId, cancellationToken);
        if (!template.IsActive)
        {
            errors.Add(new RecordFieldError("templateId", "Pasif şablonla süreç başlatılamaz."));
        }
        else if (!await Context.Data.Set<ChecklistTemplateItem>().AsNoTracking().AnyAsync(i => i.TemplateId == template.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("templateId", "Şablonun hiç adımı yok."));
        }

        var employee = await Context.Data.Set<Employee>().AsNoTracking().Where(e => e.Id == entity.EmployeeId)
            .Select(e => new { e.UserId, e.Status }).SingleAsync(cancellationToken);
        if (template.Kind == Checklists.Kinds.Onboarding && employee.Status == Employee.Statuses.Terminated)
        {
            errors.Add(new RecordFieldError("employeeId", "Ayrılmış çalışana işe giriş süreci başlatılamaz."));
        }

        if (await Context.Data.Set<EmployeeChecklist>().AsNoTracking().AnyAsync(
                c => c.EmployeeId == entity.EmployeeId && c.Kind == template.Kind && c.Status == EmployeeChecklist.Statuses.Open && c.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("employeeId", template.Kind == Checklists.Kinds.Onboarding
                ? "Bu çalışanın açık bir işe giriş süreci var."
                : "Bu çalışanın açık bir işten çıkış süreci var."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.Kind = template.Kind;
        entity.Name = template.Name;
        entity.OwnerId = employee.UserId;
        entity.StartedByUserId = Context.CurrentUserId;
    }
}

/// <summary>
/// Süreç görevleri (ADR-0042 dilim 5). Yalnızca süreç başlatılırken (kapı açık) oluşur; sorumlu kullanıcı o anda çözülür (çalışan: kendi kullanıcısı,
/// yönetici: yöneticisinin kullanıcısı, İK: boş). Durum yalnızca komutla değişir; her değişiklikte sürecin ilerlemesi aynı işlemde güncellenir.
/// Görünürlük: rolün kapsamındaki çalışanların görevleri ve kullanıcıya atanmış görevler ("Görevlerim").
/// </summary>
internal sealed class ChecklistTaskRecordStore(HrStoreContext context) : HrRecordStore<ChecklistTask>(context)
{
    private readonly Dictionary<Guid, string> names = [];
    private readonly Dictionary<Guid, (string Name, string Kind)> checklists = [];

    public override string EntityKey => HrModule.Entities.ChecklistTask;

    protected override string ReadPermission => HrModule.Permissions.ChecklistsRead;

    protected override string WritePermission => HrModule.Permissions.ChecklistsManage;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Görevler süreç başlatılırken şablondan oluşur.";

    protected override string? UpdateBlockedMessage => Context.Gate.IsOpen ? null : "Görev değiştirilmez; tamamlayın, atlayın ya da yeniden açın.";

    protected override bool IsInScope(RecordData record, IReadOnlySet<Guid> owners) =>
        base.IsInScope(record, owners) || (Context.CurrentUserId is { } me && UserIn(record, "assigneeUserId", new HashSet<Guid> { me }));

    protected override IQueryable<ChecklistTask> ScopeToOwners(IQueryable<ChecklistTask> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        var me = Context.CurrentUserId ?? Guid.Empty;
        return source.Where(t => (t.OwnerId != null && ids.Contains(t.OwnerId.Value)) || t.AssigneeUserId == me);
    }

    protected override IReadOnlyList<RecordField<ChecklistTask>> Fields => fields ??=
    [
        Field.Reference<ChecklistTask, EmployeeChecklist>("checklistId", "Süreç bulunamadı.", t => t.ChecklistId, (t, v) => t.ChecklistId = v!.Value, required: true, createOnly: true),
        Field.Integer<ChecklistTask>("sequence", t => t.Sequence, (t, v) => t.Sequence = v ?? 0, 0, 10_000, createOnly: true),
        Field.Text<ChecklistTask>("title", ChecklistTemplateItem.TitleMaxLength, t => t.Title, (t, v) => t.Title = v!, required: true, createOnly: true),
        Field.Text<ChecklistTask>("description", ChecklistTemplateItem.TextMaxLength, t => t.Description, (t, v) => t.Description = v, createOnly: true),
        Field.Choice<ChecklistTask>("responsibleRole", Checklists.Responsibles.All, t => t.ResponsibleRole, (t, v) => t.ResponsibleRole = v, createOnly: true),
        Field.Date<ChecklistTask>("dueDate", t => t.DueDate, (t, v) => t.DueDate = v!.Value, required: true),
        Field.Choice<ChecklistTask>("taskType", Checklists.TaskTypes.All, t => t.TaskType, (t, v) => t.TaskType = v, createOnly: true),
        Field.Computed<ChecklistTask>("status", t => t.Status),
        Field.Computed<ChecklistTask>("employeeId", t => t.EmployeeId.ToString()),
        Field.Computed<ChecklistTask>("employeeUserId", t => t.OwnerId?.ToString()),
        Field.Computed<ChecklistTask>("assigneeUserId", t => t.AssigneeUserId?.ToString()),
        Field.Computed<ChecklistTask>("completedByUserId", t => t.CompletedByUserId?.ToString()),
        Field.Computed<ChecklistTask>("completedAt", t => t.CompletedAt?.ToString("O")),
        Field.Computed<ChecklistTask>("note", t => t.Note),
        Field.Computed<ChecklistTask>("employeeName", t => names.GetValueOrDefault(t.EmployeeId)),
        Field.Computed<ChecklistTask>("checklistName", t => checklists.TryGetValue(t.ChecklistId, out var c) ? c.Name : null),
        Field.Computed<ChecklistTask>("checklistKind", t => checklists.TryGetValue(t.ChecklistId, out var c) ? c.Kind : null),
    ];

    private IReadOnlyList<RecordField<ChecklistTask>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<ChecklistTask>> Filters { get; } = new Dictionary<string, RecordFilter<ChecklistTask>>
    {
        ["checklistId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.ChecklistId == id) : null,
        ["status"] = (q, v) => v.Split(',') is var s && s.All(ChecklistTask.Statuses.All.Contains) ? q.Where(t => s.Contains(t.Status)) : null,
        ["assigneeUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.AssigneeUserId == id) : null,
        ["responsibleRole"] = (q, v) => Checklists.Responsibles.All.Contains(v) ? q.Where(t => t.ResponsibleRole == v) : null,
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.EmployeeId == id) : null,
        ["dueBy"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(t => t.DueDate <= day) : null,
    };

    protected override IOrderedQueryable<ChecklistTask> Order(IQueryable<ChecklistTask> source) => source.OrderBy(t => t.Sequence).ThenBy(t => t.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("sequence", "dueDate", "status");

    protected override IOrderedQueryable<ChecklistTask> Order(IQueryable<ChecklistTask> source, string? sort) => sort?.TrimStart('-') switch
    {
        "sequence" => By(source, t => t.Sequence, sort.StartsWith('-')).ThenBy(t => t.CreatedAt),
        "dueDate" => By(source, t => t.DueDate, sort.StartsWith('-')).ThenBy(t => t.Sequence),
        "status" => By(source, t => t.Status, sort.StartsWith('-')).ThenBy(t => t.DueDate),
        _ => Order(source),
    };

    protected override IQueryable<ChecklistTask> Search(IQueryable<ChecklistTask> source, string term) => source.Where(t => t.Title.Contains(term));

    protected override async Task PrepareAsync(IReadOnlyCollection<ChecklistTask> entities, CancellationToken cancellationToken)
    {
        await EmployeeNames.LoadAsync(Context, entities.Select(t => t.EmployeeId), names, cancellationToken);
        Guid[] missing = [.. entities.Select(t => t.ChecklistId).Where(id => id != Guid.Empty && !checklists.ContainsKey(id)).Distinct()];
        if (missing.Length > 0)
        {
            foreach (var c in await Context.Data.Set<EmployeeChecklist>().AsNoTracking().Where(c => missing.Contains(c.Id))
                         .Select(c => new { c.Id, c.Name, c.Kind }).ToListAsync(cancellationToken))
            {
                checklists[c.Id] = (c.Name, c.Kind);
            }
        }
    }

    protected override Task BeforeDeleteAsync(ChecklistTask entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Görevler silinmez; atlayın.");

    protected override async Task AppliedAsync(ChecklistTask entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var checklist = await Context.Data.Set<EmployeeChecklist>().SingleAsync(c => c.Id == entity.ChecklistId, cancellationToken);
        if (isNew)
        {
            entity.EmployeeId = checklist.EmployeeId;
            entity.OwnerId = checklist.OwnerId;
            entity.AssigneeUserId = entity.ResponsibleRole switch
            {
                Checklists.Responsibles.Employee => checklist.OwnerId,
                Checklists.Responsibles.Manager => await ManagerUserAsync(checklist.EmployeeId, cancellationToken),
                _ => null,
            };
        }
        else if (Context.Gate.Status is { } status)
        {
            entity.Status = status;
            var closed = status != ChecklistTask.Statuses.Open;
            entity.CompletedByUserId = closed ? Context.CurrentUserId : null;
            entity.CompletedAt = closed ? DateTimeOffset.UtcNow : null;
            entity.Note = Context.Gate.Reason;
        }

        // İlerleme: diğer görevler + bu görev.
        var others = await Context.Data.Set<ChecklistTask>().AsNoTracking().Where(t => t.ChecklistId == checklist.Id && t.Id != entity.Id)
            .Select(t => t.Status).ToListAsync(cancellationToken);
        checklist.SetProgress(others.Count + 1, others.Count(s => s != ChecklistTask.Statuses.Open) + (entity.Status != ChecklistTask.Statuses.Open ? 1 : 0));
    }

    private async Task<Guid?> ManagerUserAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employees = Context.Data.Set<Employee>().AsNoTracking();
        var manager = await employees.Where(e => e.Id == employeeId).Select(e => e.ManagerEmployeeId).SingleAsync(cancellationToken);
        return manager is { } id
            ? await employees.Where(e => e.Id == id && e.Status != Employee.Statuses.Terminated).Select(e => e.UserId).SingleOrDefaultAsync(cancellationToken)
            : null;
    }
}
