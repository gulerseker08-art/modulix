using Modulix.Core.Sectors;

namespace Modulix.Core.Tests.Sectors;

public class PartyRelationshipRulesTests
{
    [Fact]
    public void Normalize_drops_blanks_and_duplicates_and_sorts()
    {
        var (codes, error) = PartyRelationshipRules.Normalize(["tasiyici", " musteri ", "musteri", "", null]);

        Assert.Null(error);
        Assert.Equal(["musteri", "tasiyici"], codes);
    }

    [Theory]
    [InlineData("Büyük")]
    [InlineData("bosluk var")]
    [InlineData("alt_cizgi")]
    public void Normalize_rejects_invalid_codes(string code)
    {
        var (codes, error) = PartyRelationshipRules.Normalize(["musteri", code]);

        Assert.Empty(codes);
        Assert.Contains("Geçersiz kod", error);
    }

    [Fact]
    public void Normalize_limits_the_number_of_items()
    {
        var items = Enumerable.Range(0, PartyRelationshipRules.MaxItems + 1).Select(i => $"rol-{i}");

        Assert.Contains("En çok", PartyRelationshipRules.Normalize(items).Error);
        Assert.Null(PartyRelationshipRules.Normalize(items.Take(PartyRelationshipRules.MaxItems)).Error);
    }

    [Fact]
    public void Join_and_split_round_trip_and_empty_is_null()
    {
        Assert.Null(PartyRelationshipRules.Join([]));
        Assert.Equal("a,b", PartyRelationshipRules.Join(["a", "b"]));
        Assert.Equal(["a", "b"], PartyRelationshipRules.Split("a, b,"));
        Assert.Empty(PartyRelationshipRules.Split(null));
        Assert.Empty(PartyRelationshipRules.Split("  "));
    }

    [Fact]
    public void Stored_length_fits_the_maximum_number_of_longest_codes()
    {
        var longest = string.Join(',', Enumerable.Repeat(new string('a', ActivityTreeRules.CodeMaxLength), PartyRelationshipRules.MaxItems));

        Assert.True(longest.Length <= PartyRelationshipRules.StoredMaxLength);
    }

    [Fact]
    public void A_relationship_role_validates_its_code_and_name()
    {
        Assert.Throws<ArgumentException>(() => new RelationshipRole("Kötü Kod", "Ad"));
        Assert.Throws<ArgumentException>(() => new RelationshipRole("iyi-kod", " "));
        var role = new RelationshipRole("iyi-kod", " Ad ");
        Assert.Equal("Ad", role.Name);
        Assert.True(role.IsActive);
        role.SetActive(false);
        Assert.False(role.IsActive);
    }
}
