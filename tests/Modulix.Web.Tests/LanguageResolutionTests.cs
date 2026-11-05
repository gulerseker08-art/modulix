using Modulix.Shared.Localization;
using Modulix.Shared.Settings;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>ADR-0034: etkin arayüz dilinin çözümü (kullanıcı → firma → giriş öncesi tarayıcı dili → Türkçe) ve metin yardımcıları.</summary>
public class LanguageResolutionTests
{
    [Theory]
    [InlineData("tr", "tr")]
    [InlineData("tr-TR", "tr")]
    [InlineData("en-US", "en")]
    [InlineData("en_GB", "en")]
    [InlineData("az-Latn-AZ", "az")]
    [InlineData("AZ", "az")]
    [InlineData("de-DE", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Browser_culture_maps_to_a_supported_language(string? culture, string? expected) =>
        Assert.Equal(expected, LanguageCodes.FromCulture(culture));

    private static SettingDto TenantLanguage(string value) =>
        new(SettingKeys.Language, "core", "Bölgesel ayarlar", "Arayüz dili", null, SettingKind.Choice, 0, false, [], "tr", value, 1);

    [Fact]
    public void User_preference_wins_over_tenant_default_and_browser_language()
    {
        UiLanguage.Apply("az", [TenantLanguage("en")], "en");

        Assert.Equal("az", UiLanguage.Code);
    }

    [Fact]
    public void Tenant_default_wins_over_browser_language_when_user_has_no_preference()
    {
        UiLanguage.Apply(null, [TenantLanguage("en")], "az");

        Assert.Equal("en", UiLanguage.Code);
    }

    [Fact]
    public void Browser_language_is_used_before_sign_in_and_turkish_when_nothing_is_known()
    {
        UiLanguage.Apply(null, null, "az");
        Assert.Equal("az", UiLanguage.Code);

        UiLanguage.Apply(null, null, null);
        Assert.Equal("tr", UiLanguage.Code);
    }

    [Fact]
    public void Strong_text_encodes_values_so_user_input_cannot_inject_markup()
    {
        Texts.Apply(new TextBundleResponse("tr", new Dictionary<string, string> { ["x.intro"] = "{0} sizi davet etti: {1}." }));

        var html = Texts.Strong("x.intro", "<img src=x onerror=alert(1)>", "a@b.c").Value;

        Assert.Equal("<strong>&lt;img src=x onerror=alert(1)&gt;</strong> sizi davet etti: <strong>a@b.c</strong>.", html);
    }

    [Fact]
    public void Unknown_key_is_shown_as_the_key_and_placeholders_use_the_invariant_culture()
    {
        Texts.Apply(new TextBundleResponse("tr", new Dictionary<string, string> { ["x.count"] = "{0} kayıt" }));

        Assert.Equal("yok.anahtar", Texts.Get("yok.anahtar"));
        Assert.Equal("1234.5 kayıt", Texts.Get("x.count", 1234.5));
    }
}
