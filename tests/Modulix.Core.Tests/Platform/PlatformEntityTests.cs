using Modulix.Core.Platform;

namespace Modulix.Core.Tests.Platform;

public class TenantTests
{
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("  Acme-Lojistik ", "acme-lojistik")]
    [InlineData("a1b", "a1b")]
    public void Slug_is_normalized(string input, string expected)
    {
        var tenant = new Tenant("Acme", input);

        Assert.Equal(expected, tenant.Slug);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme lojistik")]
    [InlineData("açme")]
    public void Invalid_slug_is_rejected(string slug)
    {
        Assert.Throws<ArgumentException>(() => new Tenant("Acme", slug));
    }

    [Fact]
    public void Slug_longer_than_limit_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Tenant("Acme", new string('a', Tenant.SlugMaxLength + 1)));
    }

    [Fact]
    public void New_tenant_is_active_and_close_keeps_data()
    {
        var tenant = new Tenant("Acme Lojistik", "acme");
        Assert.True(tenant.IsActive);

        tenant.Close();

        Assert.False(tenant.IsActive);
        Assert.Equal("Acme Lojistik", tenant.Name);
        Assert.Equal("acme", tenant.Slug);
    }
}

public class MembershipTests
{
    [Fact]
    public void New_membership_is_active_and_can_be_disabled()
    {
        var membership = new Membership(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(membership.IsActive);

        membership.Disable();

        Assert.False(membership.IsActive);
    }

    [Fact]
    public void Empty_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Membership(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new Membership(Guid.NewGuid(), Guid.Empty));
    }
}
