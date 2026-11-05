using Microsoft.EntityFrameworkCore;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Alan türü kategorisine bağlı kurallar (K12, K17, K18). Kod türün adına değil <b>kategorisine</b> bakar: tenant türleri yeniden
/// adlandırabilir ya da yenisini ekleyebilir, ama her tür bir kategoriye bağlıdır (<c>controlled</c> gümrük denetimli,
/// <c>free</c> serbest).
/// </summary>
internal static class ZoneRules
{
    /// <summary>Tür anahtarı → kategori (tenant'ın listesine göre; pasif türler dahil, eski alanlar anlamını korusun).</summary>
    public static async Task<Dictionary<string, string?>> CategoriesAsync(WarehouseStoreContext context, CancellationToken cancellationToken)
    {
        var list = await context.ChoiceLists.GetAsync(WarehouseModule.Lists.ZoneKind, cancellationToken);
        return list.Values.ToDictionary(v => v.Key, v => v.Category, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tesisteki alanların kategorileri; <paramref name="exceptZoneId"/> (düzenlenen alan) hariç.</summary>
    public static async Task<HashSet<string>> CategoriesInUseAsync(
        WarehouseStoreContext context, Guid facilityId, Guid? exceptZoneId, CancellationToken cancellationToken)
    {
        var kinds = await context.Data.Set<Zone>().AsNoTracking()
            .Where(z => z.FacilityId == facilityId && z.Id != exceptZoneId)
            .Select(z => z.KindKey).Distinct().ToListAsync(cancellationToken);
        var categories = await CategoriesAsync(context, cancellationToken);
        return [.. kinds.Select(k => categories.GetValueOrDefault(k)).OfType<string>()];
    }

    public static bool IsMixed(IReadOnlyCollection<string> categories) =>
        categories.Contains(WarehouseModule.ZoneKindCategories.Controlled) && categories.Contains(WarehouseModule.ZoneKindCategories.Free);
}
