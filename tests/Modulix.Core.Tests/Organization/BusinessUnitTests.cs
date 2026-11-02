using Modulix.Core.Organization;

namespace Modulix.Core.Tests.Organization;

/// <summary>Departman ağacı kuralları (ADR-0026): kök, alt departman, yol, derinlik, tür ve kapatma.</summary>
public class BusinessUnitTests
{
    [Fact]
    public void Root_is_the_center_at_depth_zero_with_its_own_path()
    {
        var root = BusinessUnit.CreateRoot("  Acme Lojistik ");

        Assert.Equal("Acme Lojistik", root.Name);
        Assert.Null(root.ParentId);
        Assert.True(root.IsRoot);
        Assert.Equal(0, root.Depth);
        Assert.Equal(BusinessUnitKinds.Center, root.Kind);
        Assert.True(root.IsActive);
        Assert.Equal($"/{root.Id:N}/", root.Path);
    }

    [Fact]
    public void Child_extends_the_parent_path_and_depth()
    {
        var root = BusinessUnit.CreateRoot("Merkez");
        var dealer = BusinessUnit.CreateChild(root, " Marmara Bayi ", " MRM ", BusinessUnitKinds.Dealer);
        var city = BusinessUnit.CreateChild(dealer, "İstanbul", null, BusinessUnitKinds.Other);

        Assert.Equal(root.Id, dealer.ParentId);
        Assert.Equal("Marmara Bayi", dealer.Name);
        Assert.Equal("MRM", dealer.Code);
        Assert.Equal(1, dealer.Depth);
        Assert.Equal($"{root.Path}{dealer.Id:N}/", dealer.Path);
        Assert.Equal(2, city.Depth);
        Assert.StartsWith(dealer.Path, city.Path);
        Assert.False(city.IsRoot);
    }

    [Fact]
    public void Tree_has_at_most_four_levels()
    {
        var unit = BusinessUnit.CreateRoot("Merkez");
        for (var depth = 1; depth <= BusinessUnit.MaxDepth; depth++)
        {
            unit = BusinessUnit.CreateChild(unit, $"Seviye {depth}", null, BusinessUnitKinds.Other);
        }

        Assert.Equal(BusinessUnit.MaxDepth, unit.Depth);
        var failure = Assert.Throws<ArgumentException>(() => BusinessUnit.CreateChild(unit, "Çok derin", null, BusinessUnitKinds.Other));
        Assert.Equal("parent", failure.ParamName);
    }

    [Fact]
    public void Closed_parent_cannot_receive_children()
    {
        var root = BusinessUnit.CreateRoot("Merkez");
        var dealer = BusinessUnit.CreateChild(root, "Bayi", null, BusinessUnitKinds.Dealer);
        dealer.Close();

        Assert.False(dealer.IsActive);
        Assert.Throws<ArgumentException>(() => BusinessUnit.CreateChild(dealer, "Alt", null, BusinessUnitKinds.Other));

        dealer.Reopen();
        Assert.True(dealer.IsActive);
        Assert.NotNull(BusinessUnit.CreateChild(dealer, "Alt", null, BusinessUnitKinds.Other));
    }

    [Fact]
    public void Root_cannot_be_closed()
    {
        var root = BusinessUnit.CreateRoot("Merkez");

        Assert.Throws<ArgumentException>(() => root.Close());
        Assert.True(root.IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string? name)
    {
        var root = BusinessUnit.CreateRoot("Merkez");

        Assert.Throws<ArgumentException>(() => BusinessUnit.CreateRoot(name!));
        Assert.Throws<ArgumentException>(() => BusinessUnit.CreateChild(root, name!, null, BusinessUnitKinds.Dealer));
        Assert.Throws<ArgumentException>(() => root.Rename(name!));
    }

    [Fact]
    public void Name_and_code_have_length_limits()
    {
        var root = BusinessUnit.CreateRoot("Merkez");

        Assert.Throws<ArgumentException>(() => root.Rename(new string('a', BusinessUnit.NameMaxLength + 1)));
        root.Rename(new string('a', BusinessUnit.NameMaxLength));
        Assert.Throws<ArgumentException>(() => root.SetCode(new string('1', BusinessUnit.CodeMaxLength + 1)));
        root.SetCode("   ");
        Assert.Null(root.Code);
    }

    [Theory]
    [InlineData("Center")]
    [InlineData("Bilinmeyen")]
    [InlineData("")]
    [InlineData(null)]
    public void Child_kind_must_be_valid_and_cannot_be_the_center(string? kind)
    {
        var root = BusinessUnit.CreateRoot("Merkez");

        Assert.Throws<ArgumentException>(() => BusinessUnit.CreateChild(root, "Bayi", null, kind!));
    }

    [Fact]
    public void Root_kind_stays_the_center()
    {
        var root = BusinessUnit.CreateRoot("Merkez");

        Assert.Throws<ArgumentException>(() => root.SetKind(BusinessUnitKinds.Dealer));
        root.SetKind(BusinessUnitKinds.Center);
        Assert.Equal(BusinessUnitKinds.Center, root.Kind);
    }

    [Fact]
    public void Membership_requires_both_identifiers()
    {
        Assert.Throws<ArgumentException>(() => new BusinessUnitMember(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new BusinessUnitMember(Guid.NewGuid(), Guid.Empty));

        var unit = Guid.NewGuid();
        var member = new BusinessUnitMember(unit, Guid.NewGuid());
        Assert.Equal(unit, member.BusinessUnitId);

        var target = Guid.NewGuid();
        member.MoveTo(target);
        Assert.Equal(target, member.BusinessUnitId);
        Assert.Throws<ArgumentException>(() => member.MoveTo(Guid.Empty));
    }
}
