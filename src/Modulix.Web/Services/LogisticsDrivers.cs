using Modulix.Shared.Logistics;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>Sefer ekranlarının sürücü seçimi için sürücü kartı (ad soyad kişi kaydından).</summary>
public sealed record DriverOption(Guid Id, string Name, string Status);

/// <summary>
/// Sürücü kartlarını adlarıyla yükler (sefer listesi ve formu; ADR-0038 dilim 5). Sürücünün adı ortak kişi kaydındadır; kişi adları sürücü kişi
/// lookup'ının çözümleme ucundan alınır. İlk 200 kart okunur; daha büyük filolar için arama ile seçim sonraki iştir.
/// </summary>
public static class LogisticsDrivers
{
    public const int MaxDrivers = 200;

    public static async Task<IReadOnlyList<DriverOption>> LoadAsync(ApiClient api)
    {
        var page = await api.GetAsync<RecordPageResponse>($"/api/records/{LogisticsAccess.DriverEntityKey}?page=1&pageSize={MaxDrivers}");
        if (page.Value is not { } drivers)
        {
            return [];
        }

        var names = new Dictionary<Guid, string>();
        await WarehouseLabels.ResolveNamesAsync(api, LogisticsAccess.DriverPersonLookup, drivers.Items.Select(d => WarehouseLabels.Id(d, "personId")).OfType<Guid>(), names);
        return
        [
            .. drivers.Items.Select(d => new DriverOption(
                d.Id, WarehouseLabels.NameOf(names, WarehouseLabels.Id(d, "personId")), WarehouseLabels.Text(d, "status"))),
        ];
    }

    public static string NameOf(IReadOnlyList<DriverOption> drivers, Guid? id) =>
        id is { } key && drivers.FirstOrDefault(d => d.Id == key) is { } driver ? driver.Name : "—";
}
