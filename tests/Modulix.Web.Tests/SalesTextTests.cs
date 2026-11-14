using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 9a): satış raporu tabloları (dönem türü, hedef ölçütü ve kapsamı, tahmin kategorisi/durumu/düzeltme türü, kart, kısmi veri
/// nedeni, olasılık kaynağı, uyarı, sonuç) ve veri kalitesi sorun kodları (ad ve açıklama) sözlükten okunur. Sözlük yüklenmemişken kaynak
/// (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class SalesTextTests
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
        (SalesLabels.PeriodKindGroup, SalesLabels.PeriodKinds),
        (SalesLabels.MetricGroup, SalesLabels.Metrics),
        (SalesLabels.ScopeGroup, SalesLabels.Scopes),
        (SalesLabels.CategoryGroup, SalesLabels.Categories),
        (SalesLabels.StatusGroup, SalesLabels.Statuses),
        (SalesLabels.AdjustmentKindGroup, SalesLabels.AdjustmentKinds),
        (SalesLabels.CardGroup, SalesLabels.Cards),
        (SalesLabels.IssueGroup, SalesLabels.Issues),
        (SalesLabels.ProbabilitySourceGroup, SalesLabels.ProbabilitySources),
        (SalesLabels.FlagGroup, SalesLabels.Flags),
        (SalesLabels.OutcomeGroup, SalesLabels.Outcomes),
        (SalesAnalysisLabels.QualityGroup, [.. SalesAnalysisLabels.QualityCodes.Select(c => (c.Key, c.Label))]),
        (SalesAnalysisLabels.QualityHelpGroup, [.. SalesAnalysisLabels.QualityCodes.Select(c => (c.Key, c.Help))]),
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
    public void Label_lookup_falls_back_to_the_key_for_unknown_values()
    {
        Assert.Equal("Kazanıldı", SalesLabels.Outcome("Won"));
        Assert.Equal("Unknown", SalesLabels.Label(SalesLabels.Outcomes, "Unknown"));
        Assert.Equal("—", SalesLabels.Outcome(null));
        Assert.Equal("Eksik tutar", SalesAnalysisLabels.QualityLabel("MissingAmount"));
    }
}
