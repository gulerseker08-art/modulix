using System.Text.Json;
using Modulix.Shared.Logistics;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 aşama 4, dilim 17b (Lojistik tanımları): seçenek tabloları sözlükten gelir; Türkçe kaynak metin (koddaki yedek) <c>tr.json</c> ile
/// aynıdır. Diller arası anahtar eşitliğini mimari testler denetler.
/// </summary>
public class LogisticsTextTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))!;
    }

    [Fact]
    public void Option_lists_have_a_turkish_text_equal_to_the_source_label()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string group, IReadOnlyList<(string Key, string Label)> list)
        {
            foreach (var (key, label) in list)
            {
                var name = $"{group}.{key.ToLowerInvariant()}";
                if (!tr.TryGetValue(name, out var text))
                {
                    problems.Add($"eksik: {name} ({label})");
                }
                else if (text != label)
                {
                    problems.Add($"farklı: {name} sözlük='{text}' kaynak='{label}'");
                }
            }
        }

        Check("logistics.trip-status", LogisticsAccess.TripStatuses);
        Check("logistics.stop-type", LogisticsAccess.StopTypes);
        Check("logistics.request-status", LogisticsAccess.RequestStatuses);
        Check("logistics.shipment-status", LogisticsAccess.ShipmentStatuses);
        Check("logistics.delivery-outcome", LogisticsAccess.DeliveryOutcomes);
        Check("logistics.driver-type", LogisticsAccess.DriverTypes);
        Check("logistics.driver-status", LogisticsAccess.Statuses);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_turkish_source_label_is_shown()
    {
        Assert.Equal("Yolda", LogisticsLabels.TripStatus("inProgress"));
        Assert.Equal("Boş dönüş", LogisticsLabels.StopType("emptyReturn"));
        Assert.Equal("Planlama bekliyor", LogisticsLabels.CargoStatus(LogisticsLabels.Shipments, "pending"));
        Assert.Equal("Taşeron", LogisticsLabels.DriverType("subcontracted"));
        Assert.Equal("İzinde", LogisticsLabels.Status("onLeave"));
        Assert.Equal("bilinmeyen", LogisticsLabels.Status("bilinmeyen"));
    }
}
