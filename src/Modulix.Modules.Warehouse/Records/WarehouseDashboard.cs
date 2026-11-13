using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Warehouse.Dashboard;
using Modulix.Modules.Warehouse.Domain;
using static Modulix.Modules.Warehouse.WarehouseModule;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Pano sorguları. Okuma <see cref="IModuleData"/> üzerindendir (tenant filtresi ve RLS her sorguda geçerli); depo kayıtları şirketler arasında ortaktır
/// (<see cref="ICompanyShared"/>) ve sahip alanı taşımaz, bu yüzden görünürlük bölüm başına okuma izniyle belirlenir. Sayılar tutar toplamaz: farklı birimlerdeki
/// miktarlar karıştırılmaz, hareketler adetle sayılır.
/// </summary>
internal sealed class WarehouseDashboard(IModuleData data, ICurrentAccess access, IChoiceLists choiceLists, TimeProvider time) : IWarehouseDashboard
{
    private const int StaleDraftDays = WarehouseDashboardRules.StaleDraftDays;
    private const int ExpiryWindowDays = WarehouseDashboardRules.ExpiryWindowDays;
    private const int RecentLimit = WarehouseDashboardRules.RecentLimit;
    private const int StaleSampleLimit = WarehouseDashboardRules.StaleSampleLimit;

    public async Task<WarehouseDashboardView?> GetAsync(Guid? facilityId, int days, CancellationToken cancellationToken)
    {
        if (facilityId is { } id && !await data.Set<Facility>().AsNoTracking().AnyAsync(f => f.Id == id, cancellationToken))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = today.AddDays(1 - days);
        var permissions = await access.GetPermissionsAsync(cancellationToken);
        bool Can(string permission) => permissions.Contains(permission);

        var facilities = Can(Permissions.FacilitiesRead) ? await FacilitiesAsync(cancellationToken) : null;
        var occupancy = Can(Permissions.LocationsRead) && Can(Permissions.MovementsRead) && Can(Permissions.FacilitiesRead)
            ? await OccupancyAsync(facilityId, cancellationToken)
            : null;
        var stockItems = Can(Permissions.StockItemsRead) ? await StockItemsAsync(Start(from), cancellationToken) : null;
        var declarations = Can(Permissions.DeclarationsRead) ? await DeclarationsAsync(now, cancellationToken) : null;
        var movements = Can(Permissions.MovementsRead)
            ? await MovementsAsync(facilityId, from, today, Can(Permissions.StockItemsRead), Can(Permissions.LotsRead), Can(Permissions.LocationsRead), cancellationToken)
            : null;
        var attorneys = Can(Permissions.AttorneysRead) ? await AttorneysAsync(today, cancellationToken) : null;

        return new WarehouseDashboardView(today, days, facilityId, facilities, occupancy, stockItems, declarations, movements, attorneys);
    }

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private async Task<FacilityCounts> FacilitiesAsync(CancellationToken ct)
    {
        var active = data.Set<Facility>().AsNoTracking().Where(f => f.IsActive);
        return new FacilityCounts(await active.CountAsync(ct), await active.CountAsync(f => f.CustomsEnabled, ct));
    }

    private async Task<OccupancyView> OccupancyAsync(Guid? facilityId, CancellationToken ct)
    {
        var all = data.Set<Location>().AsNoTracking();
        var scoped = facilityId is { } id ? all.Where(l => l.FacilityId == id) : all;
        var stocked = data.Set<StockBalance>().AsNoTracking().Where(b => b.Quantity > 0).Select(b => b.LocationId);

        // Stok tutulabilen yer: alt düğümü olmayan etkin lokasyon.
        var leaves = scoped.Where(l => l.IsActive && !all.Any(c => c.ParentId == l.Id));
        var totals = await leaves.GroupBy(l => l.FacilityId).Select(g => new { FacilityId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.FacilityId, g => g.Count, ct);
        var occupied = await leaves.Where(l => stocked.Contains(l.Id)).GroupBy(l => l.FacilityId)
            .Select(g => new { FacilityId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.FacilityId, g => g.Count, ct);
        var perFacility = totals.Select(t => (FacilityId: t.Key, Locations: t.Value, Occupied: occupied.GetValueOrDefault(t.Key))).ToList();
        var passiveWithStock = await scoped.CountAsync(l => !l.IsActive && stocked.Contains(l.Id), ct);

        var ids = perFacility.Select(p => p.FacilityId).ToList();
        var names = await data.Set<Facility>().AsNoTracking().Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.Code, f.Name, f.CustomsEnabled }).ToDictionaryAsync(f => f.Id, ct);
        var byFacility = perFacility
            .Where(p => names.ContainsKey(p.FacilityId))
            .Select(p => new FacilityOccupancy(p.FacilityId, names[p.FacilityId].Code, names[p.FacilityId].Name, names[p.FacilityId].CustomsEnabled, p.Locations, p.Occupied))
            .OrderByDescending(f => f.Locations == 0 ? 0d : (double)f.Occupied / f.Locations)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ToList();

        return new OccupancyView(perFacility.Sum(p => p.Locations), perFacility.Sum(p => p.Occupied), passiveWithStock, byFacility);
    }

    private async Task<StockItemCounts> StockItemsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var items = data.Set<StockItem>().AsNoTracking();
        return new StockItemCounts(await items.CountAsync(s => s.IsActive, ct), await items.CountAsync(s => s.CreatedAt >= since, ct));
    }

    private async Task<DeclarationView> DeclarationsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var list = await choiceLists.GetAsync(Lists.DeclarationStatus, ct);
        var declarations = data.Set<CustomsDeclaration>().AsNoTracking();
        var counts = await declarations.GroupBy(d => d.StatusKey).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(ct);

        // Listedeki sırayla; listede olmayan (silinmiş) değerler sonda, anahtarıyla gösterilir.
        var byStatus = new List<DeclarationStatusCount>();
        foreach (var value in list.Values)
        {
            var count = counts.Where(c => string.Equals(c.Key, value.Key, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Count);
            if (count > 0 || value.Active)
            {
                byStatus.Add(new DeclarationStatusCount(value.Key, value.Label, value.Category, count));
            }
        }

        foreach (var orphan in counts.Where(c => list.Find(c.Key) is null))
        {
            byStatus.Add(new DeclarationStatusCount(orphan.Key, orphan.Key, null, orphan.Count));
        }

        var draftKeys = list.KeysIn(DeclarationStatusCategories.Draft).ToList();
        var staleBefore = now.AddDays(-StaleDraftDays);
        var stale = declarations.Where(d => draftKeys.Contains(d.StatusKey) && d.CreatedAt < staleBefore);
        var staleCount = await stale.CountAsync(ct);
        var sample = await stale.OrderBy(d => d.CreatedAt).Take(StaleSampleLimit)
            .Select(d => new StaleDeclaration(d.Id, d.RegistrationNo, d.CreatedAt)).ToListAsync(ct);

        return new DeclarationView(byStatus, staleCount, sample);
    }

    private async Task<MovementView> MovementsAsync(
        Guid? facilityId, DateOnly from, DateOnly today, bool showItem, bool showLot, bool showLocation, CancellationToken ct)
    {
        var types = await choiceLists.GetAsync(Lists.MovementType, ct);
        var movements = data.Set<StockMovement>().AsNoTracking();
        if (facilityId is { } id)
        {
            var locations = data.Set<Location>().AsNoTracking().Where(l => l.FacilityId == id).Select(l => l.Id);
            movements = movements.Where(m => (m.FromLocationId != null && locations.Contains(m.FromLocationId.Value))
                || (m.ToLocationId != null && locations.Contains(m.ToLocationId.Value)));
        }

        var start = Start(from);
        var end = Start(today.AddDays(1));
        var grouped = await movements
            .Where(m => m.OccurredAt >= start && m.OccurredAt < end)
            .GroupBy(m => new { m.OccurredAt.Year, m.OccurredAt.Month, m.OccurredAt.Day, m.TypeKey })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.TypeKey, Count = g.Count() })
            .ToListAsync(ct);

        string? Category(string key) => types.Find(key)?.Category;
        var daily = new List<DailyMovement>();
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var rows = grouped.Where(g => g.Year == day.Year && g.Month == day.Month && g.Day == day.Day).ToList();
            int Sum(string category) => rows.Where(r => Category(r.TypeKey) == category).Sum(r => r.Count);
            daily.Add(new DailyMovement(day, Sum(MovementCategories.Inbound), Sum(MovementCategories.Outbound), Sum(MovementCategories.Transfer), Sum(MovementCategories.Adjustment)));
        }

        var recentRows = await movements.OrderByDescending(m => m.OccurredAt).Take(RecentLimit)
            .Select(m => new { m.Id, m.TypeKey, m.Quantity, m.OccurredAt, m.LotId, m.FromLocationId, m.ToLocationId })
            .ToListAsync(ct);
        var lotIds = recentRows.Select(r => r.LotId).Distinct().ToList();
        var lots = await data.Set<Lot>().AsNoTracking().Where(l => lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.LotNo, l.StockItemId }).ToDictionaryAsync(l => l.Id, ct);
        // İzni olmayan bilgi hiç okunmaz: kimlik listesi boş bırakılır.
        List<Guid> itemIds = showItem ? [.. lots.Values.Select(l => l.StockItemId).Distinct()] : [];
        var items = await data.Set<StockItem>().AsNoTracking().Where(s => itemIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Code, s.Name }).ToDictionaryAsync(s => s.Id, ct);
        List<Guid> locationIds = showLocation ? [.. recentRows.SelectMany(r => new[] { r.FromLocationId, r.ToLocationId }).OfType<Guid>().Distinct()] : [];
        var codes = await data.Set<Location>().AsNoTracking().Where(l => locationIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Code }).ToDictionaryAsync(l => l.Id, l => l.Code, ct);

        var recent = recentRows.Select(r =>
        {
            var lot = lots.GetValueOrDefault(r.LotId);
            var item = lot is not null ? items.GetValueOrDefault(lot.StockItemId) : null;
            var type = types.Find(r.TypeKey);
            return new RecentMovement(
                r.Id, r.TypeKey, type?.Label ?? r.TypeKey, type?.Category, r.Quantity, r.OccurredAt,
                item?.Code, item?.Name, showLot ? lot?.LotNo : null,
                r.FromLocationId is { } f ? codes.GetValueOrDefault(f) : null,
                r.ToLocationId is { } t ? codes.GetValueOrDefault(t) : null);
        }).ToList();

        return new MovementView(daily, daily.Sum(d => d.Inbound), daily.Sum(d => d.Outbound), daily.Sum(d => d.Transfer), recent);
    }

    private async Task<AttorneyAlert> AttorneysAsync(DateOnly today, CancellationToken ct)
    {
        var until = today.AddDays(ExpiryWindowDays);
        var expiring = data.Set<PowerOfAttorney>().AsNoTracking()
            .Where(p => p.IsActive && p.ValidTo != null && p.ValidTo >= today && p.ValidTo <= until);
        return new AttorneyAlert(await expiring.CountAsync(ct), await expiring.MinAsync(p => p.ValidTo, ct));
    }
}
