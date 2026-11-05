using Modulix.Core.Modules;
using Modulix.Core.Settings;

namespace Modulix.Core.Tests.Settings;

/// <summary>ADR-0033: arayüz dili seçenekleri ve çözümleme sırası.</summary>
public class LanguageOptionsTests
{
    [Fact]
    public void Supported_languages_are_turkish_english_and_azerbaijani()
    {
        Assert.Equal(["tr", "en", "az"], LanguageOptions.Languages.Select(l => l.Value));
        Assert.All(LanguageOptions.Languages, l => Assert.InRange(l.Value.Length, 1, LanguageOptions.MaxLength));
    }

    [Theory]
    [InlineData("az", "en", "az")]
    [InlineData(null, "en", "en")]
    [InlineData("", "az", "az")]
    [InlineData("  ", "az", "az")]
    [InlineData(null, null, "tr")]
    [InlineData("xx", "yy", "tr")]
    [InlineData("xx", "en", "en")]
    [InlineData(" AZ ", null, "az")]
    public void User_preference_wins_then_tenant_default_then_turkish(string? user, string? tenant, string expected) =>
        Assert.Equal(expected, LanguageOptions.Resolve(user, tenant));

    [Theory]
    [InlineData("tr", "tr")]
    [InlineData("EN", "en")]
    [InlineData("tr-TR", null)]
    [InlineData("de", null)]
    [InlineData(null, null)]
    public void Normalize_accepts_only_supported_codes(string? code, string? expected) =>
        Assert.Equal(expected, LanguageOptions.Normalize(code));

    [Fact]
    public void Tenant_language_setting_is_a_choice_defaulting_to_turkish()
    {
        var setting = ModuleCatalog.Create().FindSetting(CoreModule.SettingKeys.Language);

        Assert.NotNull(setting);
        Assert.Equal(CoreModule.ModuleKey, setting.Value.ModuleKey);
        Assert.Equal(LanguageOptions.Default, setting.Value.Definition.DefaultValue);
        Assert.Equal(LanguageOptions.Languages, setting.Value.Definition.Options);
    }
}
