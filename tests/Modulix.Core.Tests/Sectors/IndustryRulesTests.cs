using Modulix.Core.Contracts.Sectors;

namespace Modulix.Core.Tests.Sectors;

/// <summary>ADR-0043: sektör kataloğu öğesinin kod ve ad kuralları.</summary>
public class IndustryRulesTests
{
    [Fact]
    public void A_new_industry_is_active_and_trims_its_name()
    {
        var industry = new Industry("gida-icecek", "  Gıda ve içecek ");

        Assert.Equal("gida-icecek", industry.Code);
        Assert.Equal("Gıda ve içecek", industry.Name);
        Assert.True(industry.IsActive);
        Assert.NotEqual(Guid.Empty, industry.Id);
    }

    [Fact]
    public void An_industry_can_be_deactivated_and_reactivated()
    {
        var industry = new Industry("enerji", "Enerji");

        industry.SetActive(false);
        Assert.False(industry.IsActive);
        industry.SetActive(true);
        Assert.True(industry.IsActive);
    }

    [Theory]
    [InlineData("Büyük")]
    [InlineData("bosluk var")]
    [InlineData("alt_cizgi")]
    [InlineData("")]
    public void Invalid_codes_are_rejected(string code)
    {
        Assert.Throws<ArgumentException>(() => new Industry(code, "Ad"));
    }

    [Fact]
    public void A_blank_or_too_long_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Industry("kod", " "));
        Assert.Throws<ArgumentException>(() => new Industry("kod", new string('a', Industry.NameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => new Industry(new string('a', Industry.CodeMaxLength + 1), "Ad"));
    }
}
