using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Core.Tests;

/// <summary>Yer kuralları (ADR-0045): ad normalleştirme (tekillik anahtarı) ve koordinat sınırları.</summary>
public class PlaceRulesTests
{
    [Theory]
    [InlineData("İstanbul Depo", "istanbul depo")]
    [InlineData("  ISTANBUL   depo ", "istanbul depo")]
    [InlineData("Çerkezköy Şubesi", "cerkezkoy subesi")]
    [InlineData("Iğdır Gümrük", "igdir gumruk")]
    [InlineData("Hamburg Limanı", "hamburg limani")]
    [InlineData("Köstence", "kostence")]
    public void Names_are_folded_to_a_comparison_key(string name, string expected) => Assert.Equal(expected, PlaceRules.NormalizeName(name));

    [Fact]
    public void Blank_names_normalize_to_empty() => Assert.Equal(string.Empty, PlaceRules.NormalizeName("   "));

    [Theory]
    [InlineData(-90, true)]
    [InlineData(90, true)]
    [InlineData(41.0082, true)]
    [InlineData(90.0001, false)]
    [InlineData(-91, false)]
    public void Latitude_is_limited_to_90_degrees(double value, bool valid) => Assert.Equal(valid, PlaceRules.IsLatitude((decimal)value));

    [Theory]
    [InlineData(-180, true)]
    [InlineData(180, true)]
    [InlineData(28.9784, true)]
    [InlineData(180.5, false)]
    [InlineData(-181, false)]
    public void Longitude_is_limited_to_180_degrees(double value, bool valid) => Assert.Equal(valid, PlaceRules.IsLongitude((decimal)value));

    [Fact]
    public void Place_is_a_registered_shared_entity_with_a_kind_list()
    {
        Assert.Contains(SharedEntities.Place, SharedEntities.All);
        Assert.Equal("core.place", SharedEntities.Place);
        Assert.Equal("core.place-kind", Place.KindList);
    }
}
