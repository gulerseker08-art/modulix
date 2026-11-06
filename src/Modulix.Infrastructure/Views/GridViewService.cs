using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Views;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Views;

/// <summary>
/// Kişisel liste görünümleri (ADR-0036). Her sorgu oturumdaki üyeliğe (<see cref="TenantContext.MembershipId"/>) göre süzülür: başkasının görünümü ne okunur ne
/// değiştirilir (bulunamadı). Tenant filtresi ve RLS ayrıca uygulanır. Kişisel tercih olduğu için denetim kaydına yazılmaz.
/// </summary>
internal sealed class GridViewService(ModulixDbContext db, ITenantContextAccessor tenantAccessor) : IGridViewService
{
    public async Task<IReadOnlyList<GridViewInfo>> ListAsync(string screenKey, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rows = await db.GridViews.AsNoTracking()
            .Where(v => v.MembershipId == tenant.MembershipId && v.ScreenKey == screenKey)
            .OrderBy(v => v.Name)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToInfo)];
    }

    public async Task<RecordResult<GridViewInfo>> CreateAsync(GridViewInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (GridViewRules.Validate(input) is { Count: > 0 } errors)
        {
            return RecordResult<GridViewInfo>.Fail(RecordErrorCode.ValidationFailed, "Görünüm geçersiz.", errors);
        }

        var mine = db.GridViews.Where(v => v.MembershipId == tenant.MembershipId && v.ScreenKey == input.ScreenKey);
        var name = input.Name.Trim();
        if (await mine.AnyAsync(v => v.Name == name, cancellationToken))
        {
            return NameTaken();
        }

        if (await mine.CountAsync(cancellationToken) >= GridViewRules.MaxViewsPerScreen)
        {
            return RecordResult<GridViewInfo>.Fail(
                RecordErrorCode.ValidationFailed,
                $"Bir ekran için en fazla {GridViewRules.MaxViewsPerScreen} görünüm kaydedebilirsiniz.",
                [new RecordFieldError("name", $"En fazla {GridViewRules.MaxViewsPerScreen} görünüm kaydedebilirsiniz.")]);
        }

        var view = new GridView(tenant.MembershipId, input.ScreenKey);
        view.Set(input);
        db.GridViews.Add(view);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı ikinci kayıt: tekil ad dizini çakışır.
            return NameTaken();
        }

        return RecordResult<GridViewInfo>.Ok(ToInfo(view));
    }

    public async Task<RecordResult<GridViewInfo>> UpdateAsync(Guid id, GridViewInput input, long version, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var view = await db.GridViews.SingleOrDefaultAsync(v => v.Id == id && v.MembershipId == tenant.MembershipId, cancellationToken);
        if (view is null)
        {
            return RecordResult<GridViewInfo>.Fail(RecordErrorCode.NotFound, "Görünüm bulunamadı.");
        }

        // Ekran değiştirilemez: görünüm başka ekranın sütunlarıyla çalışmaz.
        if (!string.Equals(view.ScreenKey, input.ScreenKey, StringComparison.Ordinal))
        {
            return RecordResult<GridViewInfo>.Fail(
                RecordErrorCode.ValidationFailed, "Görünüm başka bir ekrana taşınamaz.", [new RecordFieldError("screenKey", "Görünüm başka bir ekrana taşınamaz.")]);
        }

        if (GridViewRules.Validate(input) is { Count: > 0 } errors)
        {
            return RecordResult<GridViewInfo>.Fail(RecordErrorCode.ValidationFailed, "Görünüm geçersiz.", errors);
        }

        if (view.Version != version)
        {
            return Conflict(view.Version, version);
        }

        var name = input.Name.Trim();
        if (await db.GridViews.AnyAsync(
                v => v.MembershipId == tenant.MembershipId && v.ScreenKey == view.ScreenKey && v.Name == name && v.Id != id, cancellationToken))
        {
            return NameTaken();
        }

        view.Set(input);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(version + 1, version);
        }

        return RecordResult<GridViewInfo>.Ok(ToInfo(view));
    }

    public async Task<RecordResult<bool>> DeleteAsync(Guid id, long version, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var view = await db.GridViews.SingleOrDefaultAsync(v => v.Id == id && v.MembershipId == tenant.MembershipId, cancellationToken);
        if (view is null)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.NotFound, "Görünüm bulunamadı.");
        }

        if (view.Version != version)
        {
            return RecordResult<bool>.Fail(
                RecordErrorCode.Conflict,
                $"Görünüm siz düzenlerken değişti (beklenen sürüm {version}, güncel sürüm {view.Version}). Güncel halini alıp tekrar deneyin.");
        }

        db.GridViews.Remove(view);
        await db.SaveChangesAsync(cancellationToken);
        return RecordResult<bool>.Ok(true);
    }

    private static GridViewInfo ToInfo(GridView v) =>
        new(v.Id, v.Version, v.ScreenKey, v.Name, GridViewRules.SplitColumns(v.Columns), v.Sort, v.Filters);

    private static RecordResult<GridViewInfo> NameTaken() => RecordResult<GridViewInfo>.Fail(
        RecordErrorCode.ValidationFailed,
        "Bu adla bir görünümünüz zaten var.",
        [new RecordFieldError("name", "Bu adla bir görünümünüz zaten var.")]);

    private static RecordResult<GridViewInfo> Conflict(long current, long expected) => RecordResult<GridViewInfo>.Fail(
        RecordErrorCode.Conflict,
        $"Görünüm siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {current}). Güncel halini alıp tekrar deneyin.");

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan görünüm okunamaz.");
}
