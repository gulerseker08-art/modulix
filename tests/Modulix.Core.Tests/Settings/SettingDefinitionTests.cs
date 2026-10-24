using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Core.Settings;

namespace Modulix.Core.Tests.Settings;

/// <summary>Y6: tipli ayar tanımları ve değer kuralları.</summary>
public class SettingDefinitionTests
{
    [Fact]
    public void Core_settings_are_in_the_catalog_with_valid_defaults()
    {
        var catalog = ModuleCatalog.Create();

        var timeZone = catalog.FindSetting(CoreModule.SettingKeys.TimeZone);
        Assert.NotNull(timeZone);
        Assert.Equal(CoreModule.ModuleKey, timeZone.Value.ModuleKey);
        Assert.Equal("Europe/Istanbul", timeZone.Value.Definition.DefaultValue);
        Assert.Null(catalog.FindSetting("core.yok"));
        Assert.All(RegionalOptions.TimeZones, zone => Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(zone.Value, out _), zone.Value));

        // Denetim kaydında ayarın adı gösterilir.
        var audited = catalog.FindAuditedEntity(CoreModule.AuditedEntityKeys.TenantSetting)!;
        Assert.Equal("Para birimi", audited.Options["key"][CoreModule.SettingKeys.Currency]);
    }

    [Theory]
    [InlineData("crm.currency")]
    [InlineData("sales.Currency")]
    [InlineData("sales")]
    public void Setting_key_must_start_with_the_module_key(string key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModuleCatalog.Create(new SettingsModule(new SettingDefinition(key, "Ayar", "Grup", SettingType.Text))));
    }

    [Fact]
    public void Invalid_default_and_choice_without_options_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(new SettingsModule(
            new SettingDefinition("sales.mode", "Mod", "Grup", SettingType.Choice, "a") { Options = [new("b", "B")] })));
        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(new SettingsModule(
            new SettingDefinition("sales.mode", "Mod", "Grup", SettingType.Choice))));
        Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(new SettingsModule(
            new SettingDefinition("sales.a", "A", "Grup", SettingType.Text), new SettingDefinition("sales.a", "A", "Grup", SettingType.Text))));
    }

    [Theory]
    [InlineData(CoreModule.SettingKeys.Currency, "USD", true)]
    [InlineData(CoreModule.SettingKeys.Currency, "XYZ", false)]
    [InlineData(CoreModule.SettingKeys.Email, "muhasebe@anka.com.tr", true)]
    [InlineData(CoreModule.SettingKeys.Email, "muhasebe", false)]
    [InlineData(CoreModule.SettingKeys.Phone, "+90 (212) 555-01-01", true)]
    [InlineData(CoreModule.SettingKeys.Phone, "12ab", false)]
    [InlineData(CoreModule.SettingKeys.TaxNumber, "1234567890", true)]
    [InlineData(CoreModule.SettingKeys.TaxNumber, "12345", false)]
    [InlineData(CoreModule.SettingKeys.DisplayName, "", true)]
    [InlineData(CoreModule.SettingKeys.DisplayName, " Anka", false)]
    [InlineData(CoreModule.SettingKeys.DisplayName, "Anka\nGıda", false)]
    [InlineData(CoreModule.SettingKeys.Address, "Merkez Mah.\nİstanbul", true)]
    public void Values_follow_the_definition(string key, string value, bool valid)
    {
        var definition = ModuleCatalog.Create().FindSetting(key)!.Value.Definition;

        Assert.Equal(valid, SettingRules.Validate(definition, value) is null);
    }

    private sealed class SettingsModule(params SettingDefinition[] settings) : IModule
    {
        public string Key => "sales";

        public string DisplayName => "Satış";

        public IReadOnlyList<PermissionDefinition> Permissions => [];

        public IReadOnlyList<SettingDefinition> Settings => settings;
    }
}
