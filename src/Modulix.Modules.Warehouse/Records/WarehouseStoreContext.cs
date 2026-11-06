using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Contracts.Persistence;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.</summary>
internal sealed class WarehouseStoreContext(IModuleData data, IChoiceLists choiceLists, IRegimeProfileCatalog regimeProfiles)
{
    public IModuleData Data => data;

    public IChoiceLists ChoiceLists => choiceLists;

    /// <summary>Platformun yayınladığı rejim profilleri (yalnızca okuma).</summary>
    public IRegimeProfileCatalog RegimeProfiles => regimeProfiles;
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static bool? Bool(string value) => bool.TryParse(value, out var flag) ? flag : null;

    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
}
