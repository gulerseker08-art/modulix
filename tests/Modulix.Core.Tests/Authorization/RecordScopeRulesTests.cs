using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Organization;

namespace Modulix.Core.Tests.Authorization;

/// <summary>Kayıt kapsamlarının genişlik ve kapsama kuralları (ADR-0026 K5): sayısal değere değil kurala bakılır.</summary>
public class RecordScopeRulesTests
{
    [Fact]
    public void Persisted_numeric_values_do_not_change()
    {
        Assert.Equal(1, (int)RecordScope.All);
        Assert.Equal(2, (int)RecordScope.Team);
        Assert.Equal(3, (int)RecordScope.Own);
        Assert.Equal(4, (int)RecordScope.Unit);
        Assert.Equal(5, (int)RecordScope.UnitAndBelow);
    }

    [Theory]
    [InlineData(RecordScope.All, RecordScope.All, true)]
    [InlineData(RecordScope.All, RecordScope.UnitAndBelow, true)]
    [InlineData(RecordScope.All, RecordScope.Unit, true)]
    [InlineData(RecordScope.All, RecordScope.Team, true)]
    [InlineData(RecordScope.All, RecordScope.Own, true)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.Unit, true)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.Own, true)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.All, false)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.Team, false)]
    [InlineData(RecordScope.Unit, RecordScope.UnitAndBelow, false)]
    [InlineData(RecordScope.Unit, RecordScope.Own, true)]
    [InlineData(RecordScope.Unit, RecordScope.Team, false)]
    [InlineData(RecordScope.Team, RecordScope.Unit, false)]
    [InlineData(RecordScope.Team, RecordScope.Own, true)]
    [InlineData(RecordScope.Team, RecordScope.All, false)]
    [InlineData(RecordScope.Own, RecordScope.Own, true)]
    [InlineData(RecordScope.Own, RecordScope.Team, false)]
    [InlineData(RecordScope.Own, RecordScope.Unit, false)]
    public void Covers_follows_the_rule_not_the_numeric_order(RecordScope holder, RecordScope wanted, bool expected)
    {
        Assert.Equal(expected, RecordScopeRules.Covers(holder, wanted));
    }

    [Theory]
    // Tavan kapsamı kapsıyorsa kapsam aynen kalır.
    [InlineData(RecordScope.Own, RecordScope.Unit, RecordScope.Own)]
    [InlineData(RecordScope.Unit, RecordScope.UnitAndBelow, RecordScope.Unit)]
    [InlineData(RecordScope.Team, RecordScope.All, RecordScope.Team)]
    [InlineData(RecordScope.Unit, RecordScope.Unit, RecordScope.Unit)]
    // Kapsam tavandan genişse tavana iner.
    [InlineData(RecordScope.All, RecordScope.Unit, RecordScope.Unit)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.Unit, RecordScope.Unit)]
    [InlineData(RecordScope.All, RecordScope.Own, RecordScope.Own)]
    [InlineData(RecordScope.All, RecordScope.Team, RecordScope.Team)]
    // Karşılaştırılamayanlar (ekip ve departman) güvenli varsayılana, kendi kayıtlarına düşer.
    [InlineData(RecordScope.Unit, RecordScope.Team, RecordScope.Own)]
    [InlineData(RecordScope.Team, RecordScope.Unit, RecordScope.Own)]
    [InlineData(RecordScope.UnitAndBelow, RecordScope.Team, RecordScope.Own)]
    public void Clamp_lowers_scope_to_the_ceiling_and_falls_back_to_own_when_incomparable(
        RecordScope scope, RecordScope ceiling, RecordScope expected) =>
        Assert.Equal(expected, RecordScopeRules.Clamp(scope, ceiling));

    [Fact]
    public void Clamp_never_widens_and_always_stays_covered_by_the_ceiling()
    {
        foreach (var scope in Enum.GetValues<RecordScope>())
        {
            foreach (var ceiling in Enum.GetValues<RecordScope>())
            {
                var clamped = RecordScopeRules.Clamp(scope, ceiling);
                Assert.True(RecordScopeRules.Covers(ceiling, clamped), $"{scope}/{ceiling}: sonuç tavan altında değil.");
                Assert.True(RecordScopeRules.Covers(scope, clamped), $"{scope}/{ceiling}: sonuç kapsamdan geniş.");
            }
        }
    }

    [Fact]
    public void Team_and_unit_do_not_cover_each_other()
    {
        Assert.False(RecordScopeRules.Covers(RecordScope.Team, RecordScope.Unit));
        Assert.False(RecordScopeRules.Covers(RecordScope.Unit, RecordScope.Team));
        Assert.True(RecordScopeRules.CoversAny([RecordScope.Team, RecordScope.Unit], RecordScope.Unit));
        Assert.False(RecordScopeRules.CoversAny([RecordScope.Own], RecordScope.Team));
        Assert.False(RecordScopeRules.CoversAny([], RecordScope.Team));
    }

    [Fact]
    public void Widest_is_for_display_and_defaults_to_own()
    {
        Assert.Equal(RecordScope.Own, RecordScopeRules.Widest([]));
        Assert.Equal(RecordScope.UnitAndBelow, RecordScopeRules.Widest([RecordScope.Own, RecordScope.Unit, RecordScope.UnitAndBelow]));
        Assert.Equal(RecordScope.All, RecordScopeRules.Widest([RecordScope.Team, RecordScope.All]));
        Assert.True(RecordScopeRules.Breadth(RecordScope.Unit) > RecordScopeRules.Breadth(RecordScope.Team));
    }

    [Fact]
    public void Role_defaults_to_user_only_and_the_level_can_be_changed()
    {
        var role = new Role("Satış", ["crm.companies.read"]);

        Assert.Equal(RoleAssignLevel.User, role.AssignLevel);
        Assert.True(role.AssignableToUser);
        Assert.False(role.AssignableToUnit);

        role.SetAssignLevel(RoleAssignLevel.Both);
        Assert.True(role.AssignableToUser);
        Assert.True(role.AssignableToUnit);

        role.SetAssignLevel(RoleAssignLevel.Unit);
        Assert.False(role.AssignableToUser);
        Assert.True(role.AssignableToUnit);
    }

    [Fact]
    public void Role_accepts_the_department_scopes_and_rejects_undefined_values()
    {
        var role = new Role("Bölge", [], RecordScope.UnitAndBelow);
        Assert.Equal(RecordScope.UnitAndBelow, role.Scope);

        role.SetScope(RecordScope.Unit);
        Assert.Equal(RecordScope.Unit, role.Scope);
        Assert.Throws<ArgumentException>(() => role.SetScope((RecordScope)99));
        Assert.Throws<ArgumentException>(() => role.SetAssignLevel((RoleAssignLevel)99));
    }

    [Fact]
    public void System_role_stays_user_only_and_cannot_change_level()
    {
        var system = Role.CreateSystem();

        Assert.Equal(RoleAssignLevel.User, system.AssignLevel);
        Assert.Throws<RoleConflictException>(() => system.SetAssignLevel(RoleAssignLevel.Both));
    }

    [Fact]
    public void Unit_role_requires_both_identifiers_and_can_toggle_inheritance()
    {
        Assert.Throws<ArgumentException>(() => new BusinessUnitRole(Guid.Empty, Guid.NewGuid(), false));
        Assert.Throws<ArgumentException>(() => new BusinessUnitRole(Guid.NewGuid(), Guid.Empty, false));

        var assignment = new BusinessUnitRole(Guid.NewGuid(), Guid.NewGuid(), inheritToChildren: false);
        Assert.False(assignment.InheritToChildren);
        assignment.SetInheritToChildren(true);
        Assert.True(assignment.InheritToChildren);
    }
}
