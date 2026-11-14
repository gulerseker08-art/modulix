using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>Eski İK departman değerinin Core departmanına eşlemesi: değer, adı, bulunan Core departmanı (yoksa boş) ve etkilenen kayıt sayıları.</summary>
public sealed record DepartmentMappingRow(
    string DepartmentKey, string Label, Guid? BusinessUnitId, string? BusinessUnitName, int EmployeeCount, int AnnouncementCount, int SurveyCount);

/// <summary>
/// İK departman listesinden (<c>hr.department</c>) Core departman ağacına geçiş (ADR-0042 bölüm 22). Değer, Core'da aynı ada (ya da aynı ağaç yoluna)
/// sahip tek bir etkin departmanla eşleşir (<see cref="BusinessUnitNames"/>); eşleşmeyen ya da birden çok departmana uyan değer boş kalır ve önizlemede
/// görünür (Core'da departman açılıp yeniden denenir). Uygulama yalnızca henüz Core departmanı olmayan çalışan kartlarına, duyurulara ve anketlere yazar
/// ve kayıt boru hattından geçer (yetki, denetim kaydı, olay).
/// </summary>
public interface IDepartmentMapping
{
    /// <summary>Core departman ağacı (seçici için); çalışan okuma izni ister.</summary>
    Task<RecordResult<IReadOnlyList<BusinessUnitInfo>>> UnitsAsync(CancellationToken cancellationToken);

    /// <summary>Eşleme önizlemesi; yazmaz. Çalışan yazma izni ister.</summary>
    Task<RecordResult<IReadOnlyList<DepartmentMappingRow>>> PreviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Eşleşen değerleri uygular: hesabı olmayan çalışana eşleşen departman yazılır, hesabı bağlı çalışan kullanıcının Core departmanını alır (kart
    /// kaydı bunu yapar); duyuru ve anket hedefleri, uygulayanın onları yönetme izni varsa eşlenir. Güncellenen kayıt sayısı döner.
    /// </summary>
    Task<RecordResult<int>> ApplyAsync(CancellationToken cancellationToken);
}

internal sealed class DepartmentMapping(IRecordPipeline pipeline, HrStoreContext context) : IDepartmentMapping
{
    public async Task<RecordResult<IReadOnlyList<BusinessUnitInfo>>> UnitsAsync(CancellationToken cancellationToken) =>
        await context.Access.HasPermissionAsync(HrModule.Permissions.EmployeesRead, cancellationToken)
            ? RecordResult<IReadOnlyList<BusinessUnitInfo>>.Ok(await context.BusinessUnits.ListAsync(cancellationToken))
            : RecordResult<IReadOnlyList<BusinessUnitInfo>>.Fail(RecordErrorCode.PermissionDenied, "Departmanları görme yetkiniz yok.");

    public async Task<RecordResult<IReadOnlyList<DepartmentMappingRow>>> PreviewAsync(CancellationToken cancellationToken)
    {
        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.EmployeesWrite, cancellationToken))
        {
            return RecordResult<IReadOnlyList<DepartmentMappingRow>>.Fail(RecordErrorCode.PermissionDenied, "Departman eşleme yetkiniz yok.");
        }

        return RecordResult<IReadOnlyList<DepartmentMappingRow>>.Ok(await RowsAsync(cancellationToken));
    }

    public async Task<RecordResult<int>> ApplyAsync(CancellationToken cancellationToken)
    {
        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.EmployeesWrite, cancellationToken))
        {
            return RecordResult<int>.Fail(RecordErrorCode.PermissionDenied, "Departman eşleme yetkiniz yok.");
        }

        var mapped = (await RowsAsync(cancellationToken)).Where(r => r.BusinessUnitId is not null).ToDictionary(r => r.DepartmentKey, r => r.BusinessUnitId!.Value);
        string[] keys = [.. mapped.Keys];
        var pending = await context.Data.Set<Employee>().AsNoTracking()
            .Where(e => e.BusinessUnitId == null && e.DepartmentKey != null && keys.Contains(e.DepartmentKey))
            .Select(e => new { e.Id, e.Version, e.UserId, e.DepartmentKey })
            .ToListAsync(cancellationToken);

        var updated = 0;
        foreach (var employee in pending)
        {
            // Hesabı bağlı çalışanda kart, kullanıcının Core departmanını kendisi alır; boş güncelleme yeter.
            var values = employee.UserId is null ? new JsonObject { ["businessUnitId"] = mapped[employee.DepartmentKey!].ToString() } : [];
            if (await UpdateAsync(HrModule.Entities.Employee, employee.Id, employee.Version, values, cancellationToken) is { } failure)
            {
                return failure;
            }

            updated++;
        }

        // Duyuru ve anket hedefleri yalnızca onları yönetme izni olan kullanıcı uyguladığında eşlenir; diğerleri önizlemede kalır.
        if (await context.Access.HasPermissionAsync(HrModule.Permissions.AnnouncementsManage, cancellationToken))
        {
            var announcements = await context.Data.Set<Announcement>().AsNoTracking()
                .Where(a => a.AudienceBusinessUnitId == null && a.AudienceDepartmentKey != null && keys.Contains(a.AudienceDepartmentKey))
                .Select(a => new { a.Id, a.Version, a.AudienceDepartmentKey })
                .ToListAsync(cancellationToken);
            foreach (var announcement in announcements)
            {
                var values = new JsonObject { ["audienceBusinessUnitId"] = mapped[announcement.AudienceDepartmentKey!].ToString() };
                if (await UpdateAsync(HrModule.Entities.Announcement, announcement.Id, announcement.Version, values, cancellationToken) is { } failure)
                {
                    return failure;
                }

                updated++;
            }
        }

        if (await context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken))
        {
            var surveys = await context.Data.Set<Survey>().AsNoTracking()
                .Where(s => s.AudienceBusinessUnitId == null && s.AudienceDepartmentKey != null && keys.Contains(s.AudienceDepartmentKey))
                .Select(s => new { s.Id, s.Version, s.AudienceDepartmentKey })
                .ToListAsync(cancellationToken);
            foreach (var survey in surveys)
            {
                var values = new JsonObject { ["audienceBusinessUnitId"] = mapped[survey.AudienceDepartmentKey!].ToString() };
                if (await UpdateAsync(HrModule.Entities.Survey, survey.Id, survey.Version, values, cancellationToken) is { } failure)
                {
                    return failure;
                }

                updated++;
            }
        }

        return RecordResult<int>.Ok(updated);
    }

    private async Task<RecordResult<int>?> UpdateAsync(string entityKey, Guid id, long version, JsonObject values, CancellationToken cancellationToken)
    {
        var result = await pipeline.UpdateAsync(new RecordCommand(entityKey, id, values, version), cancellationToken);
        return result.IsSuccess ? null : RecordResult<int>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    private async Task<IReadOnlyList<DepartmentMappingRow>> RowsAsync(CancellationToken cancellationToken)
    {
        var list = await context.ChoiceLists.GetAsync(HrModule.Lists.Department, cancellationToken);
        var units = await context.BusinessUnits.ListAsync(cancellationToken);
        var employees = await Count(context.Data.Set<Employee>().AsNoTracking().Where(e => e.BusinessUnitId == null).Select(e => e.DepartmentKey), cancellationToken);
        var announcements = await Count(
            context.Data.Set<Announcement>().AsNoTracking().Where(a => a.AudienceBusinessUnitId == null).Select(a => a.AudienceDepartmentKey), cancellationToken);
        var surveys = await Count(context.Data.Set<Survey>().AsNoTracking().Where(s => s.AudienceBusinessUnitId == null).Select(s => s.AudienceDepartmentKey), cancellationToken);

        return [.. list.Values
            .Select(value =>
            {
                var matches = BusinessUnitNames.Find(units, value.Label);
                var unit = matches.Count == 1 ? matches[0] : null;
                return new DepartmentMappingRow(value.Key, value.Label, unit?.Id, unit?.PathNames,
                    employees.GetValueOrDefault(value.Key), announcements.GetValueOrDefault(value.Key), surveys.GetValueOrDefault(value.Key));
            })
            .Where(r => r.EmployeeCount + r.AnnouncementCount + r.SurveyCount > 0 || r.BusinessUnitId is not null)];
    }

    private static async Task<Dictionary<string, int>> Count(IQueryable<string?> keys, CancellationToken cancellationToken) =>
        (await keys.Where(k => k != null).GroupBy(k => k!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        .ToDictionary(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase);
}
