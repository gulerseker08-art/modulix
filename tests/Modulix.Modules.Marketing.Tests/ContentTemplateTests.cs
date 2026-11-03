using Modulix.Modules.Marketing.Runs;

namespace Modulix.Modules.Marketing.Tests;

/// <summary>ADR-0025 dilim 11: kişiye özel değişken şablonu (saf kurallar).</summary>
public sealed class ContentTemplateTests
{
    private static readonly Dictionary<string, string> Anka = new() { ["ad"] = "Anka", ["soyad"] = "Demir", ["firma"] = "Anka Tekstil" };

    [Fact]
    public void Variables_are_filled_and_missing_values_use_the_fallback()
    {
        Assert.Equal("Merhaba Anka Demir, Anka Tekstil için", ContentTemplate.Render("Merhaba {{ad_soyad|Değerli müşterimiz}}, {{firma|firmanız}} için", Anka));
        Assert.Equal("Merhaba Değerli müşterimiz,", ContentTemplate.Render("Merhaba {{ad|Değerli müşterimiz}},", new Dictionary<string, string>()));
        Assert.Equal("Merhaba Değerli müşterimiz,", ContentTemplate.Render("Merhaba {{ad|Değerli müşterimiz}},", null));
        Assert.Equal("Sayın Demir", ContentTemplate.Render("Sayın {{ad_soyad|x}}", new Dictionary<string, string> { ["soyad"] = "Demir" }));
    }

    [Fact]
    public void Text_without_variables_is_returned_unchanged()
    {
        Assert.Empty(ContentTemplate.Validate("Merhaba, kampanyamız başladı. 10:30'da görüşürüz."));
        Assert.Equal("Merhaba", ContentTemplate.Render("Merhaba", Anka));
        Assert.Empty(ContentTemplate.Validate(null));
    }

    [Theory]
    [InlineData("Merhaba {{ad}}", "yedek metin gerekir")]
    [InlineData("Merhaba {{ad|}}", "yedek metin gerekir")]
    [InlineData("Merhaba {{telefon|x}}", "Bilinmeyen değişken")]
    [InlineData("Merhaba {{Ad|x}}", "söz dizimi geçersiz")]
    [InlineData("Merhaba {{ad|x", "söz dizimi geçersiz")]
    [InlineData("Merhaba }} {{ad|x}}", "söz dizimi geçersiz")]
    public void Invalid_templates_are_reported(string text, string expected) =>
        Assert.Contains(ContentTemplate.Validate(text), m => m.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void A_fallback_must_be_single_line_and_short()
    {
        Assert.Contains(ContentTemplate.Validate("{{ad|bir\niki}}"), m => m.Contains("tek satır", StringComparison.Ordinal));
        Assert.Contains(ContentTemplate.Validate("{{ad|" + new string('a', ContentTemplate.FallbackMaxLength + 1) + "}}"), m => m.Contains("en fazla", StringComparison.Ordinal));
        Assert.Empty(ContentTemplate.Validate("{{ad|" + new string('a', ContentTemplate.FallbackMaxLength) + "}}"));
    }

    [Fact]
    public void Values_cannot_break_the_subject_line_or_grow_without_bound()
    {
        var evil = new Dictionary<string, string> { ["ad"] = "Ali\r\nBcc: x@y.test\tZ", ["firma"] = new string('f', 500) };
        var subject = ContentTemplate.Render("Merhaba {{ad|x}}", evil);
        Assert.DoesNotContain('\n', subject);
        Assert.DoesNotContain('\r', subject);
        Assert.Equal("Merhaba Ali Bcc: x@y.test Z", subject);
        Assert.Equal("Merhaba " + new string('f', ContentTemplate.ValueMaxLength), ContentTemplate.Render("Merhaba {{firma|x}}", evil));
    }

    [Fact]
    public void Values_are_inserted_once_and_are_not_interpreted_as_templates()
    {
        var tricky = new Dictionary<string, string> { ["ad"] = "{{firma|SIZDI}}" };
        Assert.Equal("Merhaba {{firma|SIZDI}}", ContentTemplate.Render("Merhaba {{ad|x}}", tricky));
    }

    [Fact]
    public void An_invalid_template_is_not_partially_filled()
    {
        const string broken = "Merhaba {{ad}} ve {{ad|x}}";
        Assert.Equal(broken, ContentTemplate.Render(broken, Anka));
    }

    [Fact]
    public void Content_validation_reports_template_errors_on_the_right_field()
    {
        var errors = ContentRules.Validate(Modulix.Core.Contracts.Communication.CommunicationChannel.Email, "Merhaba {{ad}}", "Selam {{kimlik|x}}");
        Assert.Contains(errors, e => e.Field == "subject" && e.Message.Contains("yedek metin", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Field == "body" && e.Message.Contains("Bilinmeyen", StringComparison.Ordinal));
        Assert.Empty(ContentRules.Validate(Modulix.Core.Contracts.Communication.CommunicationChannel.Email, "Merhaba {{ad|müşterimiz}}", "Selam {{ad_soyad|dostum}}"));
    }
}
