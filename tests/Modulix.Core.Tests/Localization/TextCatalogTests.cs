using Modulix.Core.Localization;
using Modulix.Core.Settings;

namespace Modulix.Core.Tests.Localization;

/// <summary>ADR-0034: metin kataloğu. Sözlükler gömülüdür; eksik çeviri Türkçeye, o da yoksa anahtara düşer.</summary>
public class TextCatalogTests
{
    private readonly TextCatalog catalog = new();

    [Theory]
    [InlineData("tr", "Kaydet")]
    [InlineData("en", "Save")]
    [InlineData("az", "Yadda saxla")]
    public void Text_comes_in_the_requested_language(string language, string expected) =>
        Assert.Equal(expected, catalog.Get(language, "common.save"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("tr-TR")]
    public void Unknown_language_falls_back_to_turkish(string? language) =>
        Assert.Equal("Kaydet", catalog.Get(language!, "common.save"));

    [Fact]
    public void Unknown_key_returns_the_key_itself() =>
        Assert.Equal("yok.boyle.bir.anahtar", catalog.Get("en", "yok.boyle.bir.anahtar"));

    [Fact]
    public void Placeholders_are_filled_with_the_invariant_culture()
    {
        // Sözlüklerde henüz yer tutuculu metin yok; biçimlendirme davranışı tanımsız anahtarın kendisi üzerinden denenir.
        Assert.Equal("5 kayıt", catalog.Get("tr", "{0} kayıt", 5));
        Assert.Equal("1234.5", catalog.Get("az", "{0}", 1234.5));
    }

    [Fact]
    public void Bundle_of_every_supported_language_contains_all_source_keys()
    {
        var source = catalog.Bundle("tr").Keys;

        foreach (var language in LanguageOptions.Languages.Select(l => l.Value))
        {
            Assert.True(source.All(catalog.Bundle(language).ContainsKey), language);
        }
    }

    [Fact]
    public void Bundle_of_an_unknown_language_is_the_turkish_one() =>
        Assert.Equal(catalog.Bundle("tr"), catalog.Bundle("xx"));

    [Fact]
    public void Embedded_dictionaries_load_for_every_supported_language()
    {
        foreach (var language in LanguageOptions.Languages.Select(l => l.Value))
        {
            Assert.NotEmpty(TextCatalog.Load(language));
        }
    }
}
