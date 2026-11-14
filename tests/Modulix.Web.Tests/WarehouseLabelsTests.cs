using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034 (dilim 14a): Depo modülünün etiket tabloları (<see cref="WarehouseLabels"/>) sözlükten okunur. Sözlük yüklenmemişken kaynak
/// (Türkçe) metin gösterilir; testler her kaynak satırının üç sözlükte karşılığı olduğunu ve Türkçe sözlüğün kaynakla aynı kaldığını denetler.
/// </summary>
public class WarehouseLabelsTests
{
    // Etkin/pasif ve açık/kapalı metinleri ortak yardımcılardan gelir; Türkçe kaynakları yardımcıdaki yedek metinlerdir.
    private static readonly (string Key, string Label)[] HelperTexts =
    [
        ("warehouse.active", "Etkin"), ("common.inactive", "Pasif"), ("warehouse.on", "Açık"), ("warehouse.off", "Kapalı"),
    ];

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
    private static IEnumerable<(string Key, string Label)> Rows() =>
        WarehouseLabels.CharacterSets.Select(r => (WarehouseLabels.KeyOf(WarehouseLabels.CharacterSetGroup, r.Key), r.Label)).Concat(HelperTexts);

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("az")]
    public void Every_table_row_has_a_text_in_each_language(string language)
    {
        var texts = Dictionary(language);
        var missing = Rows()
            .Select(r => r.Key)
            .Where(k => !texts.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Turkish_dictionary_matches_the_source_labels()
    {
        var texts = Dictionary("tr");
        var different = Rows()
            .Where(r => texts[r.Key] != r.Label)
            .Select(r => $"{r.Key}: {texts[r.Key]} != {r.Label}")
            .ToList();

        Assert.True(different.Count == 0, string.Join(Environment.NewLine, different));
    }

    [Fact]
    public void Lookups_return_the_source_text_for_known_keys_and_the_key_otherwise()
    {
        Assert.Equal("Yalnızca rakam", WarehouseLabels.CharacterSetLabel("digits"));
        Assert.Equal("hex", WarehouseLabels.CharacterSetLabel("hex"));
        Assert.Equal(string.Empty, WarehouseLabels.CharacterSetLabel(null));
        Assert.Equal("Etkin", WarehouseLabels.ActiveText(true));
        Assert.Equal("Pasif", WarehouseLabels.ActiveText(false));
        Assert.Equal("Açık", WarehouseLabels.OnOffText(true));
        Assert.Equal("Kapalı", WarehouseLabels.OnOffText(false));
        Assert.Equal("warehouse.character-set.alphanumeric", WarehouseLabels.KeyOf(WarehouseLabels.CharacterSetGroup, "alphanumeric"));
    }
}
