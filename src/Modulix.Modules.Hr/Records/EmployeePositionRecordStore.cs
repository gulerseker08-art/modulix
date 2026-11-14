using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Görev geçmişi (ADR-0042 bölüm 20). Yalnızca okunur: satırları çalışan kartı yazar (<see cref="EmployeePositions"/>), genel uçtan oluşturulamaz,
/// değiştirilemez, silinemez. Görünürlük çalışan kartıyla aynıdır (çalışan okuma izni; kart rehber gibi herkese açıktır).
/// </summary>
internal sealed class EmployeePositionRecordStore(HrStoreContext context) : HrRecordStore<EmployeePosition>(context)
{
    public override string EntityKey => HrModule.Entities.EmployeePosition;

    protected override string ReadPermission => HrModule.Permissions.EmployeesRead;

    protected override string WritePermission => HrModule.Permissions.EmployeesWrite;

    protected override string? CreateBlockedMessage => "Görev geçmişi çalışan kartı güncellenince yazılır.";

    protected override string? ImmutableMessage => "Görev geçmişi değiştirilmez; çalışan kartını güncelleyin.";

    protected override IReadOnlyList<RecordField<EmployeePosition>> Fields { get; } =
    [
        Field.Computed<EmployeePosition>("employeeId", p => p.EmployeeId.ToString()),
        Field.Computed<EmployeePosition>("startDate", p => p.StartDate.ToString("yyyy-MM-dd")),
        Field.Computed<EmployeePosition>("endDate", p => p.EndDate?.ToString("yyyy-MM-dd")),
        Field.Computed<EmployeePosition>("jobTitle", p => p.JobTitle),
        Field.Computed<EmployeePosition>("departmentKey", p => p.DepartmentKey),
        Field.Computed<EmployeePosition>("managerEmployeeId", p => p.ManagerEmployeeId?.ToString()),
        Field.Computed<EmployeePosition>("employmentType", p => p.EmploymentType),
        Field.Computed<EmployeePosition>("changedByUserId", p => p.ChangedByUserId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<EmployeePosition>> Filters { get; } = new Dictionary<string, RecordFilter<EmployeePosition>>
    {
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(p => p.EmployeeId == id) : null,
    };

    protected override IOrderedQueryable<EmployeePosition> Order(IQueryable<EmployeePosition> source) =>
        source.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedAt);

    protected override IQueryable<EmployeePosition> Search(IQueryable<EmployeePosition> source, string term) =>
        source.Where(p => p.JobTitle != null && p.JobTitle.Contains(term));
}

/// <summary>
/// Çalışan kartının görev dönemlerini yazar (ADR-0042 bölüm 20). Kart deposu doğrulamadan sonra çağırır; satırlar kartla aynı işlemde kaydedilir
/// (sözleşme geçmişiyle aynı kalıp). Geçmiş bu özellikten önce açılmış kartta ilk değişiklikte, kartın önceki değerleri işe giriş gününden başlayan
/// bir dönem olarak yazılır.
/// </summary>
internal static class EmployeePositions
{
    public static async Task RecordAsync(HrStoreContext context, Employee entity, bool isNew, DateOnly? effective, CancellationToken cancellationToken)
    {
        var positions = context.Data.Set<EmployeePosition>();
        var latest = isNew
            ? null
            : await positions.Where(p => p.EmployeeId == entity.Id).OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        if (isNew)
        {
            latest = From(entity, entity.HireDate, context.CurrentUserId);
            positions.Add(latest);
        }
        else if (latest is null)
        {
            var before = await context.Data.Set<Employee>().AsNoTracking().SingleAsync(e => e.Id == entity.Id, cancellationToken);
            latest = From(before, before.HireDate, changedBy: null);
            positions.Add(latest);
        }

        if (!Same(latest, entity))
        {
            var day = effective ?? DateOnly.FromDateTime(DateTime.UtcNow);
            if (day <= latest.StartDate)
            {
                throw new RecordValidationException([new RecordFieldError("positionEffectiveDate",
                    $"Geçerlilik günü önceki görevin başlangıcından ({latest.StartDate:dd.MM.yyyy}) sonra olmalıdır.")]);
            }

            if (entity.TerminationDate is { } left && day > left)
            {
                throw new RecordValidationException([new RecordFieldError("positionEffectiveDate", "Geçerlilik günü ayrılış tarihinden sonra olamaz.")]);
            }

            Close(latest, day.AddDays(-1));
            latest = From(entity, day, context.CurrentUserId);
            positions.Add(latest);
        }

        // Ayrılınca son dönem ayrılış günüyle kapanır; yeniden çalışmaya başlayınca (ayrılış tarihi silinince) yeniden açılır.
        if (entity.Status == Employee.Statuses.Terminated && entity.TerminationDate is { } end && end >= latest.StartDate)
        {
            Close(latest, end);
        }
        else if (entity.TerminationDate is null && latest.EndDate is not null)
        {
            Close(latest, null);
        }
    }

    private static bool Same(EmployeePosition position, Employee employee) =>
        position.JobTitle == employee.JobTitle && position.DepartmentKey == employee.DepartmentKey
        && position.ManagerEmployeeId == employee.ManagerEmployeeId && position.EmploymentType == employee.EmploymentType;

    private static void Close(EmployeePosition position, DateOnly? endDate)
    {
        if (position.EndDate != endDate)
        {
            position.EndDate = endDate;
            position.Touch();
        }
    }

    private static EmployeePosition From(Employee employee, DateOnly start, Guid? changedBy) => new()
    {
        EmployeeId = employee.Id,
        StartDate = start,
        JobTitle = employee.JobTitle,
        DepartmentKey = employee.DepartmentKey,
        ManagerEmployeeId = employee.ManagerEmployeeId,
        EmploymentType = employee.EmploymentType,
        ChangedByUserId = changedBy,
    };
}
