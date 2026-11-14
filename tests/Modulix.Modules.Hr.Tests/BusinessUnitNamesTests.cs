using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Hr.Records;

namespace Modulix.Modules.Hr.Tests;

/// <summary>
/// ADR-0042 bölüm 22: departmanı adla bulma (içe aktarma ve eski İK değerlerinin eşlenmesi). Kendi adı ya da ağaç yolu (başı yazılmayabilir); Türkçe büyük/küçük harf;
/// yol ayracı olarak "›", "&gt;" ve "/"; kapalı departman aranmaz; aynı adı taşıyan iki departman belirsizdir (veritabanı gerektirmez).
/// </summary>
public class BusinessUnitNamesTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid IstanbulSales = Guid.NewGuid();
    private static readonly Guid IstanbulFinance = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();
    private static readonly Guid Closed = Guid.NewGuid();

    private static readonly IReadOnlyList<BusinessUnitInfo> Units =
    [
        new(Root, null, "Merkez", "Merkez", [Root], 0, true),
        new(Sales, Root, "Satış", "Merkez › Satış", [Root, Sales], 1, true),
        new(IstanbulSales, Sales, "İstanbul", "Merkez › Satış › İstanbul", [Root, Sales, IstanbulSales], 2, true),
        new(Finance, Root, "Finans", "Merkez › Finans", [Root, Finance], 1, true),
        new(IstanbulFinance, Finance, "İstanbul", "Merkez › Finans › İstanbul", [Root, Finance, IstanbulFinance], 2, true),
        new(Closed, Root, "Eski Şube", "Merkez › Eski Şube", [Root, Closed], 1, false),
    ];

    [Theory]
    [InlineData("Satış")]
    [InlineData("SATIŞ")]
    [InlineData("  satış ")]
    [InlineData("Merkez › Satış")]
    [InlineData("merkez > satış")]
    [InlineData("Merkez/Satış")]
    public void Name_or_tree_path_finds_the_department(string name) =>
        Assert.Equal(Sales, Assert.Single(BusinessUnitNames.Find(Units, name)).Id);

    [Fact]
    public void Turkish_casing_is_respected() =>
        Assert.Equal(IstanbulSales, Assert.Single(BusinessUnitNames.Find(Units, "MERKEZ / SATIŞ / İSTANBUL")).Id);

    [Fact]
    public void Same_name_in_two_branches_is_ambiguous_until_the_path_is_given()
    {
        Assert.Equal(2, BusinessUnitNames.Find(Units, "İstanbul").Count);
        Assert.Equal(IstanbulFinance, Assert.Single(BusinessUnitNames.Find(Units, "Merkez › Finans › İstanbul")).Id);
    }

    [Fact]
    public void The_start_of_the_path_may_be_left_out() =>
        Assert.Equal(IstanbulSales, Assert.Single(BusinessUnitNames.Find(Units, "satış / istanbul")).Id);

    [Theory]
    [InlineData("Eski Şube")]
    [InlineData("Pazarlama")]
    [InlineData("Satış › Ankara")]
    [InlineData("Merkez › Eski Şube")]
    public void Closed_unknown_and_partial_paths_are_not_found(string name) =>
        Assert.Empty(BusinessUnitNames.Find(Units, name));

    [Fact]
    public void Inside_an_ancestor_means_the_ancestor_is_on_the_path()
    {
        var istanbul = Units.Single(u => u.Id == IstanbulSales);
        Assert.True(istanbul.IsWithin(Sales));
        Assert.True(istanbul.IsWithin(Root));
        Assert.False(istanbul.IsWithin(Finance));
    }
}
