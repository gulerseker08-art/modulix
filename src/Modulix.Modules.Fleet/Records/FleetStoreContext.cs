using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;

namespace Modulix.Modules.Fleet.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.</summary>
internal sealed class FleetStoreContext(IModuleData data, IChoiceLists choiceLists)
{
    public IModuleData Data => data;

    public IChoiceLists ChoiceLists => choiceLists;
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    public static DateOnly? Day(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
}
