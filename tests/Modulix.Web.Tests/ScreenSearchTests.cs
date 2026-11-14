using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Genel aramanın "Ekranlar" grubu: ad eşleştirme, sıralama ve Türkçe harf duyarsızlığı.</summary>
public class ScreenSearchTests
{
    private static readonly ScreenLink[] Screens =
    [
        new("Firmalar", "crm/firmalar", "building", "CRM"),
        new("Fırsatlar", "crm/firsatlar", "target", "CRM"),
        new("Müşteri adayları", "crm/leadler", "inbox", "CRM"),
        new("Satış fırsat raporu", "crm/raporlar/firsat", "dashboard", "CRM"),
        new("İzinlerim", "ik/izinlerim", "calendar", "İnsan Kaynakları"),
    ];

    [Theory]
    [InlineData("firsat")]
    [InlineData("FIRSAT")]
    [InlineData("fırsat")]
    public void Matching_ignores_case_and_turkish_marks(string query)
    {
        IReadOnlyList<ScreenLink> found = ScreenSearch.Find(Screens, query, 5);

        Assert.Equal(["crm/firsatlar", "crm/raporlar/firsat"], found.Select(s => s.Href));
    }

    [Fact]
    public void Name_prefix_ranks_before_word_prefix_and_plain_containment()
    {
        ScreenLink[] screens =
        [
            new("Teklif onayları", "a", "x"),
            new("Onay bekleyenler", "b", "x"),
            new("Sonayar", "c", "x"),
        ];

        Assert.Equal(["b", "a", "c"], ScreenSearch.Find(screens, "onay", 5).Select(s => s.Href));
    }

    [Fact]
    public void Dotted_capital_i_matches_plain_i()
    {
        Assert.Equal(["ik/izinlerim"], ScreenSearch.Find(Screens, "izin", 5).Select(s => s.Href));
    }

    [Fact]
    public void Result_is_limited_and_a_route_is_listed_once()
    {
        ScreenLink[] screens = [new("Firmalar", "crm/firmalar", "x"), new("Firmalar (CRM)", "crm/firmalar", "x"), new("Firma raporu", "r", "x")];

        Assert.Equal(["crm/firmalar"], ScreenSearch.Find(screens, "firma", 1).Select(s => s.Href));
        Assert.Equal(["crm/firmalar", "r"], ScreenSearch.Find(screens, "firma", 5).Select(s => s.Href));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yokboyleekran")]
    public void Blank_or_unmatched_query_returns_nothing(string query)
    {
        Assert.Empty(ScreenSearch.Find(Screens, query, 5));
    }
}
