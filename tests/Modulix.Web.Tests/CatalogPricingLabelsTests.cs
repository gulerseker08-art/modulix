using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 8c/8d): ürün kataloğu (tür, durum, ücretlendirme, periyot, sunum şekli) ve fiyatlandırma (kapsam, kademe yöntemi,
/// liste durumu, hesap hatası açıklamaları) tabloları sözlükten okunur. Sözlük yüklenmemişken kaynak (Türkçe) metin gösterilir; testler her
/// kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class CatalogPricingLabelsTests
{
    private static Dictionary<string, string> Dictionary(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", $"{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    // Sözlük testte yüklenmediği için tablolar kaynak metni döndürür.
    private static IEnumerable<(string Group, IReadOnlyList<(string Key, string Label)> Rows)> Tables() =>
    [
        (CatalogLabels.KindGroup, CatalogLabels.Kinds),
        (CatalogLabels.StatusGroup, CatalogLabels.Statuses),
        (CatalogLabels.ChargeTypeGroup, CatalogLabels.ChargeTypes),
        (CatalogLabels.RecurringPeriodGroup, CatalogLabels.RecurringPeriods),
        (CatalogLabels.DeliveryModeGroup, CatalogLabels.DeliveryModes),
        (PricingLabels.ScopeGroup, PricingLabels.Scopes),
        (PricingLabels.TierMethodGroup, PricingLabels.TierMethods),
        (PricingLabels.ListStatusGroup, PricingLabels.ListStatuses),
        (PricingLabels.ErrorHintGroup, PricingLabels.ErrorHints),
    ];

    private static string KeyOf(string group, string key) => $"{group}.{key.ToLowerInvariant()}";

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Tables()
            .SelectMany(t => t.Rows.Select(r => KeyOf(t.Group, r.Key)))
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Tables()
            .SelectMany(t => t.Rows.Select(r => (Key: KeyOf(t.Group, r.Key), r.Label)))
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Error_hint_returns_the_source_text_for_known_codes_and_null_otherwise()
    {
        Assert.Equal("Ürün bu tarihte satışa uygun değil.", PricingLabels.ErrorHint("product-not-sellable"));
        Assert.Null(PricingLabels.ErrorHint("unknown-code"));
        Assert.Null(PricingLabels.ErrorHint(null));
    }
}
