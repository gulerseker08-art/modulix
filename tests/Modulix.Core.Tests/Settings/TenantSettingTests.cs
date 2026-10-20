using Modulix.Core.Settings;

namespace Modulix.Core.Tests.Settings;

public class TenantSettingTests
{
    [Theory]
    [InlineData("locale", "locale")]
    [InlineData(" Currency.Default ", "currency.default")]
    [InlineData("crm.pipeline-stages_v2", "crm.pipeline-stages_v2")]
    public void Key_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, new TenantSetting(input, "x").Key);
    }

    [Theory]
    [InlineData(".locale")]
    [InlineData("vergi bölgesi")]
    [InlineData("a/b")]
    public void Invalid_key_is_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new TenantSetting(key, "x"));
    }

    [Fact]
    public void New_setting_has_no_tenant_until_saved()
    {
        // TenantId kayıt sırasında aktif tenant'tan atanır; kod tarafından yazılamaz.
        Assert.Equal(Guid.Empty, new TenantSetting("locale", "tr-TR").TenantId);
    }

    [Fact]
    public void Too_long_value_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new TenantSetting("note", new string('x', TenantSetting.ValueMaxLength + 1)));
    }
}
