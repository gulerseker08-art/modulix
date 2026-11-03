using Modulix.Core.Authorization;

namespace Modulix.Core.Tests.Authorization;

/// <summary>Rolün "kapsam dışı ortak kayıtları gizle" ayarı (ADR-0026 K8).</summary>
public class RoleHidingTests
{
    [Fact]
    public void New_roles_keep_todays_behaviour_and_the_setting_can_be_toggled()
    {
        var role = new Role("Bayi satış", ["crm.companies.read"]);

        Assert.False(role.HideOutOfScopeShared);
        role.SetHideOutOfScopeShared(true);
        Assert.True(role.HideOutOfScopeShared);
        role.SetHideOutOfScopeShared(false);
        Assert.False(role.HideOutOfScopeShared);
    }

    [Fact]
    public void System_role_cannot_change_the_setting_because_it_sees_every_record()
    {
        var system = Role.CreateSystem();

        Assert.False(system.HideOutOfScopeShared);
        Assert.Throws<RoleConflictException>(() => system.SetHideOutOfScopeShared(true));
    }

    [Fact]
    public void Converting_an_old_role_to_the_system_role_clears_the_setting()
    {
        var role = new Role(Role.SystemRoleName, []);
        role.SetHideOutOfScopeShared(true);
        role.SetAssignLevel(Contracts.Authorization.RoleAssignLevel.Both);

        role.MarkAsSystem();

        Assert.False(role.HideOutOfScopeShared);
        Assert.Equal(Contracts.Authorization.RoleAssignLevel.User, role.AssignLevel);
    }
}
