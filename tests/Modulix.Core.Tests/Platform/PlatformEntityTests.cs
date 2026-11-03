using Modulix.Core.Modules;
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

public class TenantLifecycleTests
{
    [Fact]
    public void Trial_tenant_has_access_and_can_be_activated_only_once()
    {
        var tenant = new Tenant("Acme", "acme", trial: true);
        Assert.Equal(TenantStatus.Trial, tenant.Status);
        Assert.True(tenant.IsActive);

        tenant.Activate();

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Throws<InvalidOperationException>(tenant.Activate);
    }

    [Fact]
    public void Suspended_tenant_loses_access_and_reopen_restores_it()
    {
        var tenant = new Tenant("Acme", "acme");

        tenant.Suspend();

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.False(tenant.IsActive);
        Assert.Throws<InvalidOperationException>(tenant.Suspend);
        Assert.Throws<InvalidOperationException>(tenant.Activate);

        tenant.Reopen();

        Assert.True(tenant.IsActive);
    }

    [Fact]
    public void Closed_tenant_cannot_be_suspended()
    {
        var tenant = new Tenant("Acme", "acme");
        tenant.Close();

        Assert.Throws<InvalidOperationException>(tenant.Suspend);
    }

    [Theory]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Trial, true)]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Closed, false)]
    public void Operational_rule_decides_access_for_every_status(TenantStatus status, bool expected)
    {
        Assert.Equal(expected, TenantStatusRules.IsOperational(status));
        Assert.Equal(expected, TenantStatusRules.Operational.Contains(status));
    }

    [Fact]
    public void Every_status_has_an_access_decision()
    {
        // Yeni durum eklenince TenantStatusRules'a karar yazmak unutulmasın: tüm değerler Operational içinde ya da dışında bilinçli.
        var known = new[] { TenantStatus.Active, TenantStatus.Trial, TenantStatus.Suspended, TenantStatus.Closed };
        Assert.Equal(known.Order(), Enum.GetValues<TenantStatus>().Order());
    }
}

public class ModuleEntitlementTests
{
    [Fact]
    public void New_entitlement_defaults_to_manual_and_unlimited()
    {
        var entitlement = new ModuleEntitlement(Guid.NewGuid(), "crm");

        Assert.True(entitlement.IsEnabled);
        Assert.Equal(EntitlementSource.Manual, entitlement.Source);
        Assert.Null(entitlement.ValidUntil);
        Assert.Null(entitlement.Note);
    }

    [Fact]
    public void Terms_are_stored_trimmed_and_kept_when_enabling_again_without_terms()
    {
        var until = DateTimeOffset.UtcNow.AddDays(10);
        var entitlement = new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms(EntitlementSource.Trial, until, "  deneme  "));
        Assert.Equal("deneme", entitlement.Note);

        entitlement.Disable();
        entitlement.Enable();

        Assert.True(entitlement.IsEnabled);
        Assert.Equal(EntitlementSource.Trial, entitlement.Source);
        Assert.Equal(until, entitlement.ValidUntil);
    }

    [Fact]
    public void Exception_needs_a_note_and_valid_until_must_be_in_the_future()
    {
        var future = DateTimeOffset.UtcNow.AddDays(1);

        Assert.Equal("note", Assert.Throws<ArgumentException>(
            () => new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms(EntitlementSource.Exception, future, "  "))).ParamName);
        Assert.Equal("validUntil", Assert.Throws<ArgumentException>(
            () => new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms(EntitlementSource.Trial, DateTimeOffset.UtcNow.AddMinutes(-1)))).ParamName);
        Assert.Equal("note", Assert.Throws<ArgumentException>(
            () => new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms(Note: new string('a', ModuleEntitlement.NoteMaxLength + 1)))).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentException>(
            () => new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms((EntitlementSource)99))).ParamName);
    }

    [Fact]
    public void Invalid_terms_leave_an_existing_entitlement_unchanged()
    {
        var entitlement = new ModuleEntitlement(Guid.NewGuid(), "crm", new EntitlementTerms(EntitlementSource.Plan, null, "plan"));

        Assert.Throws<ArgumentException>(() => entitlement.Enable(new EntitlementTerms(EntitlementSource.Exception)));

        Assert.Equal(EntitlementSource.Plan, entitlement.Source);
        Assert.Equal("plan", entitlement.Note);
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
