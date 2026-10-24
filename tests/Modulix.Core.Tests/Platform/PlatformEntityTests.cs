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
    public void Invited_membership_becomes_active_only_by_accepting()
    {
        var at = DateTimeOffset.UtcNow;
        var membership = Membership.CreateInvited(Guid.NewGuid(), Guid.NewGuid(), awaitsAcceptance: true, at);
        Assert.Equal(MembershipStatus.Invited, membership.Status);
        Assert.Equal(at, membership.InvitedAt);
        Assert.Throws<InvalidOperationException>(membership.Disable);
        Assert.Throws<InvalidOperationException>(membership.Enable);

        membership.AcceptInvitation();

        Assert.True(membership.IsActive);
        Assert.Throws<InvalidOperationException>(membership.AcceptInvitation);
        Assert.Throws<InvalidOperationException>(() => membership.MarkInvitationSent(at));
    }

    [Fact]
    public void Invitation_for_an_existing_account_is_active_at_once()
    {
        var membership = Membership.CreateInvited(Guid.NewGuid(), Guid.NewGuid(), awaitsAcceptance: false, DateTimeOffset.UtcNow);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public void Only_disabled_membership_can_be_enabled()
    {
        var membership = new Membership(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(membership.Enable);

        membership.Disable();
        Assert.Throws<InvalidOperationException>(membership.Disable);
        membership.Enable();

        Assert.True(membership.IsActive);
    }

    [Fact]
    public void Empty_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Membership(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new Membership(Guid.NewGuid(), Guid.Empty));
    }
}
