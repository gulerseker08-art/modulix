using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Okurun hedef kitle bilgisi: Core departmanı ve üstleri (<see cref="UnitPath"/>; hedef departman bu yolda ise içerik ona yöneliktir, böylece üst
/// departmana hedeflenen içerik alt departmandakilere de görünür) ve çalışan kartındaki eski İK departmanı (eşlenmemiş eski içerik için).
/// </summary>
internal sealed record HrReader(Guid[] UnitPath, string? LegacyDepartmentKey);

/// <summary>Duyuru ve anketlerin hedef kitlesi (ADR-0042 bölüm 22): hedef Core departmanıdır.</summary>
internal static class HrAudience
{
    /// <summary>
    /// Okurun departmanı kullanıcının Core departmanıdır (üyeliğinin bağlı olduğu birim); üyelikte departman yoksa etkin çalışan kartındaki departman.
    /// Kartı ve departmanı olmayan okur yalnızca herkese açık içeriği görür.
    /// </summary>
    public static async Task<HrReader> ReaderAsync(HrStoreContext context, CancellationToken cancellationToken)
    {
        var me = context.CurrentUserId ?? Guid.Empty;
        var card = await context.Data.Set<Employee>().AsNoTracking()
            .Where(e => e.UserId == me && e.Status != Employee.Statuses.Terminated)
            .Select(e => new { e.BusinessUnitId, e.DepartmentKey })
            .FirstOrDefaultAsync(cancellationToken);
        Guid? unit = (await context.BusinessUnits.UnitsOfUsersAsync([me], cancellationToken)).TryGetValue(me, out var member) ? member : card?.BusinessUnitId;
        Guid[] path = unit is { } id && (await context.BusinessUnits.ListAsync(cancellationToken)).FirstOrDefault(u => u.Id == id) is { } info
            ? [.. info.PathIds]
            : [];
        return new HrReader(path, card?.DepartmentKey);
    }

    /// <summary>Hedef olarak seçilen departman ağaçta bulunmalı; yeni seçimde etkin olmalıdır (kapalı departmanda kalan eski hedef korunur).</summary>
    public static async Task ValidateAsync(HrStoreContext context, Guid? unit, Guid? previous, CancellationToken cancellationToken)
    {
        if (unit is not { } id || id == previous)
        {
            return;
        }

        var info = (await context.BusinessUnits.ListAsync(cancellationToken)).FirstOrDefault(u => u.Id == id);
        if (info is null || !info.IsActive)
        {
            throw new RecordValidationException([new RecordFieldError("audienceBusinessUnitId", info is null ? "Departman bulunamadı." : "Kapalı departman seçilemez.")]);
        }
    }
}
