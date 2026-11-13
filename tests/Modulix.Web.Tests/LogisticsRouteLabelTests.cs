using System.Text.Json.Nodes;
using Modulix.Shared.Logistics;
using Modulix.Shared.Records;
using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Güzergâh etiketi (ADR-0043): Türkiye dışındaki uç ülke koduyla yazılır; eski kayıt (ülke alanı yok) şehir adıyla kalır. Ülke kataloğu da burada denenir.</summary>
public class LogisticsRouteLabelTests
{
    private static RecordResponse Record(string from, string? fromCountry, string to, string? toCountry)
    {
        var values = new JsonObject { ["pickupCity"] = from, ["deliveryCity"] = to };
        if (fromCountry is not null) { values["pickupCountry"] = fromCountry; }
        if (toCountry is not null) { values["deliveryCountry"] = toCountry; }
        return new RecordResponse("logistics.shipment", Guid.NewGuid(), 1, values);
    }

    [Theory]
    [InlineData("Ankara", "TR", "İzmir", "TR", "Ankara → İzmir")]
    [InlineData("İstanbul", "TR", "Hamburg", "DE", "İstanbul → Hamburg (DE)")]
    [InlineData("Sofya", "BG", "Gebze", "TR", "Sofya (BG) → Gebze")]
    [InlineData("Ankara", null, "İzmir", null, "Ankara → İzmir")]
    public void Route_shows_the_country_code_only_outside_Turkey(string from, string? fromCountry, string to, string? toCountry, string expected) =>
        Assert.Equal(expected, LogisticsLabels.Route(Record(from, fromCountry, to, toCountry)));

    [Fact]
    public void Country_catalog_resolves_names_by_language_and_keeps_unknown_codes()
    {
        Assert.Equal("Almanya", CountryCatalog.NameOf("DE"));
        Assert.Equal("Germany", CountryCatalog.NameOf("DE", "en"));
        Assert.Equal("Türkiye", CountryCatalog.NameOf("TR", "tr"));
        Assert.Equal("ZZ", CountryCatalog.NameOf("ZZ"));
        Assert.Equal(string.Empty, CountryCatalog.NameOf(null));
        Assert.True(CountryCatalog.IsValid("US"));
        Assert.False(CountryCatalog.IsValid("us"));
        Assert.Equal(CountryCatalog.All.Count, CountryCatalog.All.Select(c => c.Code).Distinct().Count());
    }
}
