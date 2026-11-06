using Modulix.Core.Customs;

namespace Modulix.Core.Tests.Customs;

/// <summary>GTİP katalog satırı ve sürümü için saf kurallar (ADR-0032 K9).</summary>
public class GtipCatalogRulesTests
{
    [Fact]
    public void Entry_is_normalized_and_trimmed()
    {
        var (entry, error) = GtipCatalogRules.Validate("0902.30.00", "  Siyah çay  ", " kg ");

        Assert.Null(error);
        Assert.Equal(new GtipCatalogEntryInput("09023000", "Siyah çay", "kg"), entry);
    }

    [Fact]
    public void Blank_unit_is_stored_as_null()
    {
        var (entry, _) = GtipCatalogRules.Validate("09023000", "Siyah çay", "   ");

        Assert.Null(entry!.Unit);
    }

    [Theory]
    [InlineData(null, "Tanım")]
    [InlineData("", "Tanım")]
    [InlineData("abc", "Tanım")]
    [InlineData("1234", "Tanım")]
    [InlineData("09023000", null)]
    [InlineData("09023000", "   ")]
    public void Invalid_entry_is_rejected(string? code, string? description)
    {
        var (entry, error) = GtipCatalogRules.Validate(code, description, null);

        Assert.Null(entry);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Too_long_description_or_unit_is_rejected()
    {
        Assert.NotNull(GtipCatalogRules.Validate("09023000", new string('a', GtipCatalogRules.DescriptionMaxLength + 1), null).Error);
        Assert.NotNull(GtipCatalogRules.Validate("09023000", "Tanım", new string('k', GtipCatalogRules.UnitMaxLength + 1)).Error);
        Assert.Null(GtipCatalogRules.Validate("09023000", new string('a', GtipCatalogRules.DescriptionMaxLength), null).Error);
    }

    [Fact]
    public void Entry_list_must_be_non_empty_and_unique()
    {
        var a = new GtipCatalogEntryInput("09023000", "A", null);
        var b = new GtipCatalogEntryInput("18050000", "B", null);

        Assert.Null(GtipCatalogRules.ValidateEntries([a, b]));
        Assert.NotNull(GtipCatalogRules.ValidateEntries([]));
        Assert.Contains("09023000", GtipCatalogRules.ValidateEntries([a, b, a])!);
    }

    [Fact]
    public void Version_needs_a_name_and_at_least_one_entry()
    {
        var by = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var version = new GtipCatalogVersion("  2026 cetveli ", new DateOnly(2026, 1, 1), "  cetvel.xlsx ", 10, by, now);
        Assert.Equal("2026 cetveli", version.Name);
        Assert.Equal("cetvel.xlsx", version.SourceFileName);
        Assert.Throws<ArgumentException>(() => new GtipCatalogVersion(" ", new DateOnly(2026, 1, 1), null, 10, by, now));
        Assert.Throws<ArgumentException>(() => new GtipCatalogVersion(new string('a', 101), new DateOnly(2026, 1, 1), null, 10, by, now));
        Assert.Throws<ArgumentException>(() => new GtipCatalogVersion("Ad", new DateOnly(2026, 1, 1), null, 0, by, now));
    }
}
