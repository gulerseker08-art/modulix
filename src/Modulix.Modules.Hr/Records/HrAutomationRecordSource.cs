using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Hr.Automation;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// İK'nın tarama kaynağı (ADR-0027 tarama tetikleyicileri, ADR-0042 bölüm 18): "bugün hangi kayıtlar bu tetikleyiciye uyuyor" sorusunu yanıtlar ve
/// yalnızca <b>kayıt kimlikleri</b> döner (kimliğe göre artan, anahtar tabanlı sayfa).
/// <list type="bullet">
///   <item><b>Zamanlanmış</b> tarama açık kayıtları kapsar: ayrılmamış çalışan, onay bekleyen ya da onaylanmış izin, zimmetteki demirbaş, açık görev.</item>
///   <item><b>Tarihe bağlı</b> tarama durumdan bağımsız, tarih alanı hedef güne düşen kayıtlardır; durum koşulu kuralda yazılır.</item>
/// </list>
/// Tarih sütunlarının tek kaynağı <see cref="HrAutomation"/> tablolarıdır; beyanla ayrışmaz (birim test). Hareketsizlik taraması yoktur.
/// </summary>
internal sealed class HrAutomationRecordSource(HrStoreContext context) : IAutomationRecordSource
{
    public Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken) => query.EntityKey switch
    {
        HrModule.Entities.Employee => ScanAsync(query, HrAutomation.EmployeeDateColumns, e => e.Status != Employee.Statuses.Terminated, cancellationToken),
        HrModule.Entities.LeaveRequest => ScanAsync(query, HrAutomation.LeaveDateColumns,
            r => r.Status == LeaveRequest.Statuses.Pending || r.Status == LeaveRequest.Statuses.Approved, cancellationToken),
        HrModule.Entities.AssetAssignment => ScanAsync(query, HrAutomation.AssignmentDateColumns, a => a.Status == AssetAssignment.Statuses.Active, cancellationToken),
        HrModule.Entities.ChecklistTask => ScanAsync(query, HrAutomation.TaskDateColumns, t => t.Status == ChecklistTask.Statuses.Open, cancellationToken),
        _ => throw new InvalidOperationException("Bilinmeyen kayıt türü."),
    };

    private async Task<AutomationScanPage> ScanAsync<TEntity>(
        AutomationScanQuery query,
        IReadOnlyDictionary<string, Expression<Func<TEntity, DateOnly?>>> dateColumns,
        Expression<Func<TEntity, bool>> open,
        CancellationToken cancellationToken)
        where TEntity : HrEntity
    {
        var records = context.Data.Set<TEntity>().AsNoTracking();
        if (query.AfterRecordId is { } cursor)
        {
            records = records.Where(e => e.Id.CompareTo(cursor) > 0);
        }

        switch (query.Kind)
        {
            case AutomationScanKind.Scheduled:
                records = records.Where(open);
                break;

            case AutomationScanKind.DateRelative when query.Field is { } field && dateColumns.TryGetValue(field, out var column) && query.TargetDate is { } target:
                records = records.Where(OnDay(column, target));
                break;

            case AutomationScanKind.DateRelative:
                throw new InvalidOperationException("Desteklenmeyen tarih alanı.");

            default:
                throw new InvalidOperationException("Desteklenmeyen tarama türü.");
        }

        var ids = await records.OrderBy(e => e.Id).Select(e => e.Id).Take(query.PageSize).ToListAsync(cancellationToken);
        return new AutomationScanPage(ids);
    }

    /// <summary><c>kolon == hedef</c> ifadesi; sütun seçicisinin parametresi korunur (EF sorguya çevirir).</summary>
    private static Expression<Func<TEntity, bool>> OnDay<TEntity>(Expression<Func<TEntity, DateOnly?>> column, DateOnly target) =>
        Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(column.Body, Expression.Constant((DateOnly?)target, typeof(DateOnly?))), column.Parameters);
}
