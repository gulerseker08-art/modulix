using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>ADR-0043: Platform → Firma sektörleri ekranında addan önerilen kod, sunucudaki kod kuralına uyar.</summary>
public class IndustryCodeSuggestionTests
{
    [Theory]
    [InlineData("Gıda ve içecek", "gida-ve-icecek")]
    [InlineData("  İnşaat  ", "insaat")]
    [InlineData("Kâğıt, ambalaj ve baskı", "kagit-ambalaj-ve-baski")]
    [InlineData("E-ticaret", "e-ticaret")]
    [InlineData("Şeker & Çikolata (ihracat)", "seker-cikolata-ihracat")]
    [InlineData("Bilişim 4.0", "bilisim-4-0")]
    public void A_name_becomes_a_lowercase_ascii_code(string name, string expected)
    {
        Assert.Equal(expected, IndustryCodeSuggestion.FromName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void A_name_without_letters_or_digits_gives_an_empty_suggestion(string? name)
    {
        Assert.Equal(string.Empty, IndustryCodeSuggestion.FromName(name));
    }

    [Fact]
    public void A_long_name_is_cut_at_fifty_characters_without_a_trailing_dash()
    {
        string code = IndustryCodeSuggestion.FromName(string.Join(' ', Enumerable.Repeat("sektör", 20)));

        Assert.True(code.Length <= 50);
        Assert.DoesNotContain("--", code);
        Assert.False(code.EndsWith('-'));
        Assert.All(code, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));
    }
}
