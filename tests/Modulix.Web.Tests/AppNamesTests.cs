using System.Text.Json;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>
/// ADR-0034: <see cref="AppCatalog"/> adlarının çevirisi. Katalogdaki ad Türkçe kaynak metindir; her uygulama, bölüm,
/// pasif kart ve ev sahibi menü adı için sözlükte anahtar bulunur ve Türkçe metin katalogla aynı kalır (ayrışmaz).
/// Diller arası anahtar eşitliğini mimari testler denetler.
/// </summary>
public class AppNamesTests
{
    private static Dictionary<string, string> Turkish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Modulix.Core", "Localization", "Texts", "tr.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void Every_catalog_name_has_a_turkish_text_equal_to_the_catalog_name()
    {
        var tr = Turkish();
        var problems = new List<string>();

        void Check(string key, string name)
        {
            if (!tr.TryGetValue(key, out var text))
            {
                problems.Add($"eksik: {key} ({name})");
            }
            else if (text != name)
            {
                problems.Add($"farklı: {key} sözlük='{text}' katalog='{name}'");
            }
        }

        foreach (var app in AppCatalog.All)
        {
            Check(AppNames.KeyOf(app), app.Name);
            if (app.Hosted?.Name is { } hostedName)
            {
                Check(AppNames.MenuKeyOf(app), hostedName);
            }
        }

        foreach (var group in AppCatalog.Groups)
        {
            Check(AppNames.KeyOf(group), group.Name);
        }

        foreach (var planned in AppCatalog.Planned)
        {
            Check(AppNames.KeyOf(planned), planned.Name);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Without_a_loaded_dictionary_the_catalog_name_is_shown()
    {
        var app = AppCatalog.All.First(a => a.Id == "contracts.list");

        Assert.Equal(app.Name, AppNames.Of(app));
        Assert.Equal(app.Hosted!.Name, AppNames.MenuNameOf(app));
        Assert.Equal(AppCatalog.Groups[0].Name, AppNames.Of(AppCatalog.Groups[0]));
    }
}
