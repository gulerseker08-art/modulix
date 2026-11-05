using Modulix.Core.Contracts.Persistence;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.</summary>
internal sealed class WarehouseStoreContext(IModuleData data)
{
    public IModuleData Data => data;
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static bool? Bool(string value) => bool.TryParse(value, out var flag) ? flag : null;
}
