using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>Çalışan kimliği → ad soyad (kişi kaydından); izin kayıtlarının listede çalışanı göstermesi için toplu yüklenir.</summary>
internal static class EmployeeNames
{
    public static async Task LoadAsync(HrStoreContext context, IEnumerable<Guid> employeeIds, Dictionary<Guid, string> names, CancellationToken cancellationToken)
    {
        Guid[] missing = [.. employeeIds.Where(id => id != Guid.Empty && !names.ContainsKey(id)).Distinct()];
        if (missing.Length == 0)
        {
            return;
        }

        var rows = await context.Data.Set<Employee>().AsNoTracking().Where(e => missing.Contains(e.Id))
            .Join(context.Data.Set<Person>().AsNoTracking(), e => e.PersonId, p => p.Id, (e, p) => new { e.Id, Name = p.FirstName + " " + p.LastName })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            names[row.Id] = row.Name.Trim();
        }
    }
}

/// <summary>
/// Resmî tatil ve idari izin günleri (ADR-0042 dilim 2). Tarih tenant içinde tektir; yanlış girilen gün silinebilir. Listedeki değişiklik
/// açılmış taleplerin gün sayısını değiştirmez (talep açılırken hesaplanır).
/// </summary>
internal sealed class HolidayRecordStore(HrStoreContext context) : HrRecordStore<Holiday>(context)
{
    public override string EntityKey => HrModule.Entities.Holiday;

    protected override string ReadPermission => HrModule.Permissions.LeavesRead;

    protected override string WritePermission => HrModule.Permissions.LeavesManage;

    protected override IReadOnlyList<RecordField<Holiday>> Fields { get; } =
    [
        Field.Date<Holiday>("date", h => h.Date, (h, v) => h.Date = v!.Value, required: true),
        Field.Text<Holiday>("name", Holiday.NameMaxLength, h => h.Name, (h, v) => h.Name = v!, required: true),
        Field.Flag<Holiday>("halfDay", h => h.HalfDay, (h, v) => h.HalfDay = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Holiday>> Filters { get; } = new Dictionary<string, RecordFilter<Holiday>>
    {
        ["year"] = (q, v) => int.TryParse(v, out var year) && year is >= 2000 and <= 2100 ? q.Where(h => h.Date.Year == year) : null,
    };

    protected override IOrderedQueryable<Holiday> Order(IQueryable<Holiday> source) => source.OrderBy(h => h.Date);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("date", "name");

    protected override IOrderedQueryable<Holiday> Order(IQueryable<Holiday> source, string? sort) => sort?.TrimStart('-') switch
    {
        "date" => By(source, h => h.Date, sort.StartsWith('-')),
        "name" => By(source, h => h.Name, sort.StartsWith('-')).ThenBy(h => h.Date),
        _ => Order(source),
    };

    protected override IQueryable<Holiday> Search(IQueryable<Holiday> source, string term) => source.Where(h => h.Name.Contains(term));

    protected override async Task AppliedAsync(Holiday entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<Holiday>().AsNoTracking().AnyAsync(h => h.Date == entity.Date && h.Id != entity.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("date", "Bu tarih tatil listesinde zaten var.")]);
        }
    }
}

/// <summary>
/// Yıllık izin hakkı (ADR-0042 dilim 2). Çalışan ve yıl yalnızca oluştururken verilir; bir çalışanın bir yılda tek hakkı olur. Hak ve devreden gün
/// İK tarafından girilir; kullanılan gün yalnızca izin talebi iş akışınca güncellenir ve hak, kullanılandan aşağı çekilemez. Kayıt sahibi çalışanın
/// kullanıcısıdır (veri kapsamı "kendi" olan çalışan yalnızca kendi hakkını görür). İzin kullanılmış hak silinmez.
/// </summary>
internal sealed class LeaveBalanceRecordStore(HrStoreContext context) : HrRecordStore<LeaveBalance>(context)
{
    private readonly Dictionary<Guid, string> names = [];

    public override string EntityKey => HrModule.Entities.LeaveBalance;

    protected override string ReadPermission => HrModule.Permissions.LeavesRead;

    protected override string WritePermission => HrModule.Permissions.LeavesManage;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override IQueryable<LeaveBalance> ScopeToOwners(IQueryable<LeaveBalance> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        return source.Where(b => b.OwnerId != null && ids.Contains(b.OwnerId.Value));
    }

    protected override IReadOnlyList<RecordField<LeaveBalance>> Fields => fields ??=
    [
        Field.Reference<LeaveBalance, Employee>("employeeId", "Çalışan bulunamadı.", b => b.EmployeeId, (b, v) => b.EmployeeId = v!.Value, required: true, createOnly: true),
        Field.Integer<LeaveBalance>("year", b => b.Year, (b, v) => b.Year = v!.Value, 2000, 2100, required: true, createOnly: true),
        Field.Decimal<LeaveBalance>("entitledDays", b => b.EntitledDays, (b, v) => b.EntitledDays = v ?? 0m, 1, 0m, LeaveBalance.MaxDays, required: true),
        Field.Decimal<LeaveBalance>("carriedOverDays", b => b.CarriedOverDays, (b, v) => b.CarriedOverDays = v ?? 0m, 1, 0m, LeaveBalance.MaxDays),
        Field.Text<LeaveBalance>("notes", LeaveBalance.NotesMaxLength, b => b.Notes, (b, v) => b.Notes = v),
        Field.Computed<LeaveBalance>("usedDays", b => JsonValue.Create(b.UsedDays)),
        Field.Computed<LeaveBalance>("remainingDays", b => JsonValue.Create(b.RemainingDays)),
        Field.Computed<LeaveBalance>("employeeUserId", b => b.OwnerId?.ToString()),
        Field.Computed<LeaveBalance>("employeeName", b => names.GetValueOrDefault(b.EmployeeId)),
    ];

    private IReadOnlyList<RecordField<LeaveBalance>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<LeaveBalance>> Filters { get; } = new Dictionary<string, RecordFilter<LeaveBalance>>
    {
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(b => b.EmployeeId == id) : null,
        ["year"] = (q, v) => int.TryParse(v, out var year) && year is >= 2000 and <= 2100 ? q.Where(b => b.Year == year) : null,
    };

    private IQueryable<Person> People => Context.Data.Set<Person>().AsNoTracking();

    private IQueryable<Employee> Employees => Context.Data.Set<Employee>().AsNoTracking();

    protected override IOrderedQueryable<LeaveBalance> Order(IQueryable<LeaveBalance> source) =>
        source.OrderByDescending(b => b.Year).ThenBy(b => Employees.Where(e => e.Id == b.EmployeeId)
            .Join(People, e => e.PersonId, p => p.Id, (e, p) => p.FirstName + " " + p.LastName).FirstOrDefault());

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("year", "employee", "entitledDays", "usedDays", "createdAt");

    protected override IOrderedQueryable<LeaveBalance> Order(IQueryable<LeaveBalance> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "year" => By(source, b => b.Year, desc).ThenBy(b => b.CreatedAt),
            "employee" => By(source, b => Employees.Where(e => e.Id == b.EmployeeId)
                .Join(People, e => e.PersonId, p => p.Id, (e, p) => p.FirstName + " " + p.LastName).FirstOrDefault(), desc).ThenByDescending(b => b.Year),
            "entitledDays" => By(source, b => b.EntitledDays, desc).ThenBy(b => b.CreatedAt),
            "usedDays" => By(source, b => b.UsedDays, desc).ThenBy(b => b.CreatedAt),
            "createdAt" => By(source, b => b.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<LeaveBalance> Search(IQueryable<LeaveBalance> source, string term)
    {
        var people = People.Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)).Select(p => p.Id);
        var employees = Employees.Where(e => people.Contains(e.PersonId)).Select(e => e.Id);
        return source.Where(b => employees.Contains(b.EmployeeId));
    }

    protected override Task PrepareAsync(IReadOnlyCollection<LeaveBalance> entities, CancellationToken cancellationToken) =>
        EmployeeNames.LoadAsync(Context, entities.Select(b => b.EmployeeId), names, cancellationToken);

    protected override async Task AppliedAsync(LeaveBalance entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (isNew)
        {
            if (await Context.Data.Set<LeaveBalance>().AsNoTracking().AnyAsync(b => b.EmployeeId == entity.EmployeeId && b.Year == entity.Year, cancellationToken))
            {
                errors.Add(new RecordFieldError("year", "Bu çalışanın bu yıl için izin hakkı zaten tanımlı."));
            }

            entity.OwnerId = await Employees.Where(e => e.Id == entity.EmployeeId).Select(e => e.UserId).SingleOrDefaultAsync(cancellationToken);
        }

        if (entity.RemainingDays < 0)
        {
            errors.Add(new RecordFieldError("entitledDays", $"Hak ve devreden gün toplamı kullanılan günden ({entity.UsedDays:0.#}) az olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(LeaveBalance entity, CancellationToken cancellationToken) =>
        entity.UsedDays > 0
            ? throw new RecordConflictException("İzin kullanılmış hak silinemez; hak gününü düzeltin.")
            : Task.CompletedTask;
}

/// <summary>
/// İzin talepleri (ADR-0042 dilim 2). Talep açılırken: çalışan ayrılmamış olmalı; "izin yönetimi" izni olmayan kullanıcı yalnızca kendi kartına
/// (bağlı kullanıcı kendisi) talep açar; bitiş başlangıçtan önce olamaz ve talep tek takvim yılı içinde kalır; yarım gün yalnızca tek günlük
/// talepte; gün sayısı <see cref="LeaveCalendar"/> ile hesaplanır ve sıfır olamaz; aynı çalışanın bekleyen ya da onaylı talebiyle çakışamaz;
/// yıllık izinde o yılın hakkı tanımlı olmalı ve kalan gün, bekleyen yıllık taleplerle birlikte yetmelidir. Sahip (çalışanın kullanıcısı) ve
/// onaylayan (yöneticinin kullanıcısı) talep anında yazılır. Talep genel uçtan değiştirilmez ve silinmez; durum <see cref="LeaveWorkflow"/>
/// komutlarıyla değişir (onayda yıllık izin aynı işlemde bakiyeden düşer, onaylı iznin iptali geri ekler). Görünürlük: rolün kapsamındaki
/// sahipler ve onaylayanı kendisi olan talepler (yönetici astının talebini "kendi" kapsamıyla da görür).
/// </summary>
internal sealed class LeaveRequestRecordStore(HrStoreContext context) : HrRecordStore<LeaveRequest>(context)
{
    private readonly Dictionary<Guid, string> names = [];

    public override string EntityKey => HrModule.Entities.LeaveRequest;

    protected override string ReadPermission => HrModule.Permissions.LeavesRead;

    protected override string CreatePermission => HrModule.Permissions.LeavesRequest;

    protected override string WritePermission => HrModule.Permissions.LeavesManage;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override bool IsInScope(RecordData record, IReadOnlySet<Guid> owners) =>
        base.IsInScope(record, owners) || (Context.CurrentUserId is { } me && UserIn(record, "approverUserId", new HashSet<Guid> { me }));

    protected override IQueryable<LeaveRequest> ScopeToOwners(IQueryable<LeaveRequest> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        var me = Context.CurrentUserId ?? Guid.Empty;
        return source.Where(r => (r.OwnerId != null && ids.Contains(r.OwnerId.Value)) || r.ApproverUserId == me);
    }

    protected override string? UpdateBlockedMessage =>
        Context.Gate.IsOpen ? null : "İzin talebi değiştirilemez; talebi iptal edip yenisini açın.";

    protected override IReadOnlyList<RecordField<LeaveRequest>> Fields => fields ??=
    [
        Field.Reference<LeaveRequest, Employee>("employeeId", "Çalışan bulunamadı.", r => r.EmployeeId, (r, v) => r.EmployeeId = v!.Value, required: true, createOnly: true),
        Field.ListChoice<LeaveRequest>("leaveTypeKey", HrModule.Lists.LeaveType, r => r.LeaveTypeKey, (r, v) => r.LeaveTypeKey = v ?? string.Empty),
        Field.Date<LeaveRequest>("startDate", r => r.StartDate, (r, v) => r.StartDate = v!.Value, required: true),
        Field.Date<LeaveRequest>("endDate", r => r.EndDate, (r, v) => r.EndDate = v!.Value, required: true),
        Field.Flag<LeaveRequest>("halfDay", r => r.HalfDay, (r, v) => r.HalfDay = v),
        Field.Text<LeaveRequest>("description", LeaveRequest.TextMaxLength, r => r.Description, (r, v) => r.Description = v),

        // Sunucunun yazdığı alanlar: genel uçtan gelen değer yok sayılır.
        Field.Computed<LeaveRequest>("days", r => JsonValue.Create(r.Days)),
        Field.Computed<LeaveRequest>("leaveCategory", r => r.LeaveCategory),
        Field.Computed<LeaveRequest>("status", r => r.Status),
        Field.Computed<LeaveRequest>("employeeUserId", r => r.OwnerId?.ToString()),
        Field.Computed<LeaveRequest>("approverUserId", r => r.ApproverUserId?.ToString()),
        Field.Computed<LeaveRequest>("decidedByUserId", r => r.DecidedByUserId?.ToString()),
        Field.Computed<LeaveRequest>("decidedAt", r => r.DecidedAt?.ToString("O")),
        Field.Computed<LeaveRequest>("decisionReason", r => r.DecisionReason),
        Field.Computed<LeaveRequest>("employeeName", r => names.GetValueOrDefault(r.EmployeeId)),
    ];

    private IReadOnlyList<RecordField<LeaveRequest>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<LeaveRequest>> Filters { get; } = new Dictionary<string, RecordFilter<LeaveRequest>>
    {
        ["status"] = (q, v) => v.Split(',') is var statuses && statuses.All(LeaveRequest.Statuses.All.Contains) ? q.Where(r => statuses.Contains(r.Status)) : null,
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(r => r.EmployeeId == id) : null,
        ["leaveTypeKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(r => r.LeaveTypeKey == v) : null,
        ["approverUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(r => r.ApproverUserId == id) : null,
        ["employeeUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(r => r.OwnerId == id) : null,

        // Takvim: bu aralıkla kesişen talepler (from ve to yyyy-AA-gg).
        ["from"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(r => r.EndDate >= day) : null,
        ["to"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(r => r.StartDate <= day) : null,
    };

    private IQueryable<Person> People => Context.Data.Set<Person>().AsNoTracking();

    private IQueryable<Employee> Employees => Context.Data.Set<Employee>().AsNoTracking();

    protected override IOrderedQueryable<LeaveRequest> Order(IQueryable<LeaveRequest> source) => source.OrderByDescending(r => r.StartDate);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("startDate", "employee", "leaveTypeKey", "days", "status", "createdAt");

    protected override IOrderedQueryable<LeaveRequest> Order(IQueryable<LeaveRequest> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "startDate" => By(source, r => r.StartDate, desc).ThenBy(r => r.CreatedAt),
            "employee" => By(source, r => Employees.Where(e => e.Id == r.EmployeeId)
                .Join(People, e => e.PersonId, p => p.Id, (e, p) => p.FirstName + " " + p.LastName).FirstOrDefault(), desc).ThenByDescending(r => r.StartDate),
            "leaveTypeKey" => By(source, r => r.LeaveTypeKey, desc).ThenByDescending(r => r.StartDate),
            "days" => By(source, r => r.Days, desc).ThenByDescending(r => r.StartDate),
            "status" => By(source, r => r.Status, desc).ThenByDescending(r => r.StartDate),
            "createdAt" => By(source, r => r.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<LeaveRequest> Search(IQueryable<LeaveRequest> source, string term)
    {
        var people = People.Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)).Select(p => p.Id);
        var employees = Employees.Where(e => people.Contains(e.PersonId)).Select(e => e.Id);
        return source.Where(r => employees.Contains(r.EmployeeId) || (r.Description != null && r.Description.Contains(term)));
    }

    protected override Task PrepareAsync(IReadOnlyCollection<LeaveRequest> entities, CancellationToken cancellationToken) =>
        EmployeeNames.LoadAsync(Context, entities.Select(r => r.EmployeeId), names, cancellationToken);

    protected override Task BeforeDeleteAsync(LeaveRequest entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("İzin talepleri silinmez; talebi iptal edin.");

    protected override async Task AppliedAsync(LeaveRequest entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew)
        {
            await ApplyDecisionAsync(entity, cancellationToken);
            return;
        }

        var errors = new List<RecordFieldError>();
        var employee = await Employees.Where(e => e.Id == entity.EmployeeId)
            .Select(e => new { e.UserId, e.Status, e.ManagerEmployeeId }).SingleAsync(cancellationToken);

        if (employee.Status == Employee.Statuses.Terminated)
        {
            errors.Add(new RecordFieldError("employeeId", "Ayrılmış çalışana izin talebi açılamaz."));
        }

        if (employee.UserId != Context.CurrentUserId && !await Context.Access.HasPermissionAsync(HrModule.Permissions.LeavesManage, cancellationToken))
        {
            errors.Add(new RecordFieldError("employeeId", "Yalnızca kendiniz için izin talebi açabilirsiniz."));
        }

        var choice = (await Context.ChoiceLists.GetAsync(HrModule.Lists.LeaveType, cancellationToken)).Find(entity.LeaveTypeKey);
        entity.LeaveCategory = choice?.Category ?? string.Empty;

        if (entity.EndDate < entity.StartDate)
        {
            errors.Add(new RecordFieldError("endDate", "Bitiş tarihi başlangıçtan önce olamaz."));
        }
        else if (entity.EndDate.Year != entity.StartDate.Year)
        {
            errors.Add(new RecordFieldError("endDate", "Talep tek takvim yılı içinde kalmalıdır; yıl sonunu aşan izni iki talebe bölün."));
        }
        else if (entity.HalfDay && entity.EndDate != entity.StartDate)
        {
            errors.Add(new RecordFieldError("halfDay", "Yarım gün yalnızca tek günlük talepte seçilebilir."));
        }
        else
        {
            entity.Days = await LeaveCalendar.DaysAsync(Context, entity.StartDate, entity.EndDate, entity.HalfDay, cancellationToken);
            if (entity.Days <= 0)
            {
                errors.Add(new RecordFieldError("startDate", "Seçilen aralıkta iş günü yok (hafta sonu ya da resmî tatil)."));
            }
            else if (await Context.Data.Set<LeaveRequest>().AsNoTracking().AnyAsync(
                         r => r.EmployeeId == entity.EmployeeId && r.Id != entity.Id && LeaveRequest.Statuses.Active.Contains(r.Status)
                             && r.StartDate <= entity.EndDate && r.EndDate >= entity.StartDate, cancellationToken))
            {
                errors.Add(new RecordFieldError("startDate", "Bu tarihlerde bekleyen ya da onaylanmış başka bir izin talebi var."));
            }
            else if (entity.LeaveCategory == HrModule.LeaveCategories.Annual && await BalanceErrorAsync(entity, cancellationToken) is { } balanceError)
            {
                errors.Add(new RecordFieldError("leaveTypeKey", balanceError));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.OwnerId = employee.UserId;
        var approver = employee.ManagerEmployeeId is { } manager
            ? await Employees.Where(e => e.Id == manager && e.Status != Employee.Statuses.Terminated).Select(e => e.UserId).SingleOrDefaultAsync(cancellationToken)
            : null;
        entity.ApproverUserId = approver == employee.UserId ? null : approver;
    }

    /// <summary>Yıllık izin: o yılın hakkı tanımlı mı ve kalan gün, bekleyen yıllık taleplerle birlikte yetiyor mu?</summary>
    private async Task<string?> BalanceErrorAsync(LeaveRequest entity, CancellationToken cancellationToken)
    {
        var year = entity.StartDate.Year;
        var balance = await Context.Data.Set<LeaveBalance>().AsNoTracking()
            .SingleOrDefaultAsync(b => b.EmployeeId == entity.EmployeeId && b.Year == year, cancellationToken);
        if (balance is null)
        {
            return $"{year} yılı için yıllık izin hakkı tanımlanmamış; İK'ya başvurun.";
        }

        var pending = await Context.Data.Set<LeaveRequest>().AsNoTracking()
            .Where(r => r.EmployeeId == entity.EmployeeId && r.Id != entity.Id && r.Status == LeaveRequest.Statuses.Pending
                && r.LeaveCategory == HrModule.LeaveCategories.Annual && r.StartDate.Year == year)
            .SumAsync(r => r.Days, cancellationToken);
        return balance.RemainingDays - pending >= entity.Days
            ? null
            : $"Yetersiz izin bakiyesi: kalan {balance.RemainingDays:0.#} gün, onay bekleyen {pending:0.#} gün, istenen {entity.Days:0.#} gün.";
    }

    /// <summary>
    /// Komutla gelen durum değişikliği (kapı açık): kararı veren ve zamanı yazılır; yıllık izin onayda bakiyeden düşer, onaylı iznin iptalinde geri
    /// eklenir. Bakiye satırı aynı işlemde sürümüyle güncellenir: eşzamanlı iki onay aynı bakiyeyi aşamaz (biri 409 alır).
    /// </summary>
    private async Task ApplyDecisionAsync(LeaveRequest entity, CancellationToken cancellationToken)
    {
        if (Context.Gate.Status is not { } status)
        {
            return;
        }

        var previous = entity.Status;
        entity.Status = status;
        entity.DecidedByUserId = Context.CurrentUserId;
        entity.DecidedAt = DateTimeOffset.UtcNow;
        entity.DecisionReason = Context.Gate.Reason;

        var approving = status == LeaveRequest.Statuses.Approved;
        var releasing = status == LeaveRequest.Statuses.Cancelled && previous == LeaveRequest.Statuses.Approved;
        if (entity.LeaveCategory != HrModule.LeaveCategories.Annual || !(approving || releasing))
        {
            return;
        }

        var balance = await Context.Data.Set<LeaveBalance>()
            .SingleOrDefaultAsync(b => b.EmployeeId == entity.EmployeeId && b.Year == entity.StartDate.Year, cancellationToken)
            ?? throw new RecordConflictException($"{entity.StartDate.Year} yılı için yıllık izin hakkı tanımlanmamış; önce hakkı girin.");
        if (approving && balance.RemainingDays < entity.Days)
        {
            throw new RecordConflictException($"Yetersiz izin bakiyesi: kalan {balance.RemainingDays:0.#} gün, istenen {entity.Days:0.#} gün.");
        }

        balance.AddUsed(approving ? entity.Days : -entity.Days);
    }
}
